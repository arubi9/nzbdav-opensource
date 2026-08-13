using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Clients.JellyfinSetup;
using NzbWebDAV.Clients;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Setup.Core;

public sealed class SetupGrantService
{
    private const int MaxWriteAttempts = 4;
    private const string RevocationPendingWarning =
        "Previous setup session cleanup is pending; recover before continuing.";
    private const string CandidateCleanupPendingWarning =
        "Setup session cleanup is pending; recover before continuing.";
    private static readonly TimeSpan UpstreamTokenRevokeTimeout = TimeSpan.FromSeconds(2);
    private readonly DavDatabaseContext _dbContext;
    private readonly ConfigManager _configManager;
    private readonly TimeProvider _timeProvider;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly SetupConfigPersistence _persistence;
    private readonly SetupRunLeaseService _runLeaseService;
    private readonly Func<IDbContextTransaction, CancellationToken, Task>? _beforeCommit;
    private readonly Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>> _beginTransaction;

    public SetupGrantService(
        DavDatabaseContext dbContext,
        ConfigManager configManager,
        TimeProvider timeProvider,
        Func<HttpClient>? httpClientFactory = null,
        SetupConfigPersistence? persistence = null)
        : this(dbContext, configManager, timeProvider, httpClientFactory, persistence, null, null)
    {
    }

    internal SetupGrantService(
        DavDatabaseContext dbContext,
        ConfigManager configManager,
        TimeProvider timeProvider,
        Func<HttpClient>? httpClientFactory,
        SetupConfigPersistence? persistence,
        Func<IDbContextTransaction, CancellationToken, Task>? beforeCommit,
        Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? beginTransaction = null)
    {
        _dbContext = dbContext;
        _configManager = configManager;
        _timeProvider = timeProvider;
        _httpClientFactory = httpClientFactory ?? SetupHttpClientFactory.Create;
        _persistence = persistence ?? new SetupConfigPersistence(configManager, dbContext);
        _runLeaseService = new SetupRunLeaseService(dbContext, timeProvider);
        _beforeCommit = beforeCommit;
        _beginTransaction = beginTransaction ?? ((isolation, cancellationToken) =>
            _dbContext.Database.BeginTransactionAsync(isolation, cancellationToken));
    }

    public async Task<SetupGrantResult> IssueAsync(string username, string password, CancellationToken cancellationToken = default)
    {
        var lease = await AcquireGrantLeaseAsync("grant-issue", cancellationToken).ConfigureAwait(false);
        try
        {
            return await IssueCoreAsync(username, password, cancellationToken, lease).ConfigureAwait(false);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SetupGrantResult> IssueCoreAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle lease)
    {
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(password))
            throw new BadHttpRequestException("Password is required.");

        var options = SetupEnvironmentOptions.FromEnvironment();
        EnsureEnabledAndIncomplete(options);

        return await _configManager.WithMutationGateAsync(
                async () =>
                {
                    await EnsureNormalSetupNotCompletedAsync(cancellationToken).ConfigureAwait(false);
                    // An emergency-only journal is just as authoritative as a
                    // DB candidate. A pre-auth reservation is the one
                    // recoverable exception: a previous auth response may have
                    // been lost, so reuse its deterministic device identity.
                    var priorAuthenticationIntent = await ReadAuthenticationIntentAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (priorAuthenticationIntent is null)
                        await EnsureNoRecoveryPendingUnderMutationGateAsync(cancellationToken).ConfigureAwait(false);
                    else
                        await EnsureNoRecoveryPendingExceptAuthenticationIntentUnderMutationGateAsync(
                                priorAuthenticationIntent,
                                cancellationToken)
                            .ConfigureAwait(false);

                    // Authentication is deliberately outside the serializable
                    // transaction. A session can be authenticated without
                    // holding a database lock, and only its durable handoff is
                    // retried below. Allocate the operation once, outside the
                    // retry delegate: a PostgreSQL serialization retry must use
                    // the same durable intent and Jellyfin DeviceId rather than
                    // creating a second orphanable session identity.
                    var operationId = priorAuthenticationIntent ?? $"issue:{Guid.NewGuid():N}";
                    return await ExecuteWithRetriesAsync(
                            async _ =>
                            {
                                // The operation identity is allocated before
                                // authentication and reserved durably. The
                                // reservation is the restart/reauth identity
                                // record for the unavoidable external-auth
                                // versus local-DB atomicity gap.
                                await ReserveAuthenticationIntentAsync(operationId, cancellationToken, lease)
                                    .ConfigureAwait(false);
                                JellyfinAuthenticationResult authentication;
                                try
                                {
                                    authentication = await AuthenticateJellyfinAdminAsync(
                                            normalizedUsername,
                                            password,
                                            options,
                                            operationId,
                                            cancellationToken,
                                            lease)
                                        .ConfigureAwait(false);
                                }
                                catch (Exception exception)
                                {
                                    await HandleAuthenticationFailureAsync(operationId, exception, priorAuthenticationIntent is not null, lease).ConfigureAwait(false);
                                    throw;
                                }

                                var adminAccessToken = authentication.AccessToken;
                                // The returned token is journaled before any
                                // post-auth lease check. Same-device Jellyfin
                                // authentication may already have invalidated
                                // the prior token; this closes that crash gap.
                                await PersistCandidateSessionAsync(
                                        adminAccessToken,
                                        operationId,
                                        options.JellyfinUrl,
                                        cancellationToken,
                                        lease)
                                    .ConfigureAwait(false);
                                if (!authentication.IsAdministrator)
                                    await RejectNonAdministratorAuthenticationAsync(adminAccessToken, operationId, options.JellyfinUrl, lease).ConfigureAwait(false);
                                // PersistCandidateSessionAsync has recorded the
                                // response before this fence. This post-check
                                // is intentionally cancellation-independent so
                                // cancellation cannot turn ownership loss into
                                // an unfenced handoff.
                                if (!await lease.AssertCurrentAsync(CancellationToken.None).ConfigureAwait(false))
                                    throw new SetupRunBusyException();

                                var persisted = await PersistNewSessionAsync(
                                        normalizedUsername,
                                        password,
                                        adminAccessToken,
                                        operationId,
                                        options.JellyfinUrl,
                                        cancellationToken,
                                        lease)
                                    .ConfigureAwait(false);

                                // The handoff transaction is now durable. Release
                                // this short-lived grant lease before best-effort
                                // retirement of an older Jellyfin session, so the
                                // browser can immediately start its configure
                                // request. ReleaseAsync is generation/owner fenced
                                // and therefore cannot clear a replacement owner.
                                await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);

                                // The old external session is retired only after
                                // the new account, recovery token, and grant have
                                // committed. Failure leaves the new grant/token
                                // intact and the durable pending marker enables
                                // restart or authenticated recovery. This cleanup
                                // is separately mutation-fenced; normal configure
                                // remains blocked by its revocation-pending marker
                                // until cleanup has completed.
                                try
                                {
                                    await RetirePreviousSessionAsync(
                                            persisted.PreviousAccessToken,
                                            adminAccessToken,
                                            options.JellyfinUrl,
                                            cancellationToken)
                                        .ConfigureAwait(false);
                                    var result = persisted.Result;
                                    if (persisted.CandidateCleanupPending)
                                    {
                                        result = result with
                                        {
                                            Warning = CandidateCleanupPendingWarning,
                                        };
                                    }

                                    return result;
                                }
                                catch
                                {
                                    // The account, token, and grant are already
                                    // committed. Never turn a cleanup failure
                                    // into a failed rotation: the raw grant is
                                    // returned exactly once and the durable
                                    // marker gates setup mutations until a
                                    // recovery attempt retires the old session.
                                    return persisted.Result with
                                    {
                                        RevocationPending = true,
                                        Warning = RevocationPendingWarning,
                                    };
                                }
                            },
                            cancellationToken,
                            () => _dbContext.ChangeTracker.Clear())
                        .ConfigureAwait(false);
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task PersistCandidateSessionAsync(
        string accessToken,
        string operationId,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        // Write the secondary journal with an exclusive create semantics
        // before reserving the DB slot. This closes the crash window: a
        // process can never hold a reservation for a token that is not
        // recoverable from either durable store, and a competing process
        // cannot replace an existing journal entry.
        try
        {
            if (!await _persistence.TrySaveEmergencyCandidateIfEmptyUnderMutationGateAsync(
                    accessToken, operationId, CancellationToken.None, runLease).ConfigureAwait(false))
                throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
            await EnsureCandidateSlotAvailableAsync(operationId, cancellationToken, runLease).ConfigureAwait(false);
            await _persistence.SaveCandidateUnderMutationGateAsync(
                    accessToken,
                    operationId,
                    cancellationToken,
                    runLease)
                .ConfigureAwait(false);
            try
            {
                _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                        accessToken,
                        operationId,
                        CancellationToken.None,
                        runLease: runLease)
                    .ConfigureAwait(false);
            }
            catch
            {
                // The primary handoff is durable; retaining the secondary file
                // is conservative and restart recovery can clear it.
            }
        }
        catch (SetupTransactionRecoveryRequiredException)
        {
            // Never issue HTTP while rollback/disposal has an unknown outcome.
            throw;
        }
        catch (Exception)
        {

            await LogoutAndClearCandidateAsync(
                    accessToken,
                    jellyfinUrl,
                    CancellationToken.None,
                    expectedOperationId: operationId,
                    runLease: runLease)
                .ConfigureAwait(false);
            await ClearCandidateReservationAsync(operationId, runLease).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<bool> ClearCandidateReservationAsync(string operationId, SetupRunLeaseHandle? runLease = null)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable).ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, CancellationToken.None).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            var intentFence = await _dbContext.SetupMutationFences
                .SingleAsync(row => row.Id == SetupMutationFence.SingletonId, CancellationToken.None)
                .ConfigureAwait(false);
            var canClearFence = string.IsNullOrWhiteSpace(intentFence.ReservedCandidateOperationId)
                || string.Equals(intentFence.ReservedCandidateOperationId, operationId, StringComparison.Ordinal);

            // Use a compare-and-clear statement rather than a tracked entity. The
            // same DbContext may have observed an older fence row during the
            // failed handoff, while the database fence remains authoritative.
            if (canClearFence)
            {
                await _dbContext.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE \"setup_mutation_fence\" SET \"reserved_candidate_operation_id\" = NULL WHERE \"id\" = 1 AND (\"reserved_candidate_operation_id\" IS NULL OR \"reserved_candidate_operation_id\" = {operationId})",
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }

            // The intent is an independent handoff record. Read it without the
            // context cache and compare its decrypted value before deleting it;
            // a stale cleanup must never remove a newer encrypted intent. The
            // fence CAS and this delete are protected by the same row lock.
            _dbContext.ChangeTracker.Clear();
            var intentRows = await _dbContext.ConfigItems
                .AsNoTracking()
                .Where(item => item.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionIntent)
                .ToListAsync(CancellationToken.None)
                .ConfigureAwait(false);
            if (intentRows.Count > 1)
                throw new InvalidOperationException("Duplicate setup authentication intent rows detected.");

            var intentCleared = intentRows.Count == 0;
            if (intentRows.Count == 1)
            {
                var intentRow = intentRows[0];
                var storedIntent = intentRow.IsEncrypted
                    ? _configManager.DecryptSetupValue(SetupConfigKeys.CandidateSessionIntent, intentRow.ConfigValue).Plaintext
                    : intentRow.ConfigValue;
                intentCleared = TokensEqual(storedIntent, operationId);
                if (intentCleared)
                    _dbContext.ConfigItems.Remove(intentRow);
            }

            if (_dbContext.ChangeTracker.HasChanges())
                await _dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            _dbContext.ChangeTracker.Clear();
            return canClearFence && intentCleared;
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
    }

    private async Task<PersistedGrant> PersistNewSessionAsync(
        string username,
        string password,
        string adminAccessToken,
        string operationId,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        // This read is only a compensation guard. The transaction reads the
        // authoritative value again before writing; in particular, a failed
        // same-token rotation must never log out the durable prior session.
        string? durablePriorAccessToken = null;
        var priorAccessTokenReadSucceeded = false;
        TransactionLifetime? transaction = null;
        List<ConfigItem>? cachedSetupValues = null;
        Exception? operationFailure = null;

        try
        {
            durablePriorAccessToken = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken)
                .ConfigureAwait(false);
            priorAccessTokenReadSucceeded = true;
            transaction = new TransactionLifetime(await _beginTransaction(
                    IsolationLevel.Serializable,
                    cancellationToken)
                .ConfigureAwait(false));
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction.Transaction, cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction.Transaction, cancellationToken).ConfigureAwait(false))
                throw new SetupRunBusyException();

            await EnsureNormalSetupNotCompletedAsync(cancellationToken).ConfigureAwait(false);
            await EnsureNoRecoveryPendingInCurrentTransactionAsync(operationId, cancellationToken).ConfigureAwait(false);
            var persistedCandidate = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken)
                .ConfigureAwait(false);
            var persistedOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!TokensEqual(persistedCandidate, adminAccessToken)
                || !TokensEqual(persistedOperation, operationId))
                throw new InvalidOperationException("The authenticated setup session candidate changed before handoff.");

            var previousAccessToken = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken)
                .ConfigureAwait(false);
            var retirePrevious = !string.IsNullOrWhiteSpace(previousAccessToken)
                                 && !TokensEqual(previousAccessToken, adminAccessToken);

            await EnsureLocalAdminAccountAsync(username, password, cancellationToken).ConfigureAwait(false);
            await EnsureNormalSetupNotCompletedAsync(cancellationToken).ConfigureAwait(false);
            await _persistence.SaveAsync(
                    new SetupSecretValues
                    {
                        JellyfinApiKey = adminAccessToken,
                        RevocationPending = retirePrevious,
                        RevocationPendingToken = retirePrevious ? previousAccessToken : null,
                    },
                    completed: false,
                    transaction.Transaction,
                    rows => cachedSetupValues = rows
                        .Where(row => !IsCandidateKey(row.ConfigName))
                        .ToList(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction.Transaction, cancellationToken).ConfigureAwait(false))
                throw new SetupRunBusyException();

            await EnsureNormalSetupNotCompletedAsync(cancellationToken).ConfigureAwait(false);
            var result = await EnsureAdminAndRotateGrantAsync(username, cancellationToken, transaction.Transaction, runLease)
                .ConfigureAwait(false);

            // Candidate persistence intentionally retained the pre-auth
            // reservation until the complete local handoff is ready. Clear
            // only this exact operation under the same mutation fence before
            // the promotion commit is acknowledged.
            var intentFence = await _dbContext.SetupMutationFences
                .SingleAsync(item => item.Id == SetupMutationFence.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(intentFence.ReservedCandidateOperationId, operationId, StringComparison.Ordinal))
                intentFence.ReservedCandidateOperationId = null;
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction.Transaction, CancellationToken.None).ConfigureAwait(false))
                throw new SetupRunBusyException();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            if (_beforeCommit is not null)
                await _beforeCommit(transaction.Transaction, cancellationToken).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction.Transaction, CancellationToken.None).ConfigureAwait(false))
                throw new SetupRunBusyException();

            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var promotionDisposeFailure = await transaction.TryDisposeAsync().ConfigureAwait(false);
            if (promotionDisposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(
                    promotionDisposeFailure, null, promotionDisposeFailure);

            // Candidate journal cleanup is intentionally a separate, gated
            // transaction. The promotion transaction must leave the journal
            // durable until its commit has been acknowledged; an uncertain
            // commit therefore remains fully recoverable.
            var candidateCleanupPending = false;
            try
            {
                var candidate = await _persistence.ReadCandidateSessionTokenAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (TokensEqual(candidate, adminAccessToken)
                    && TokensEqual(candidateOperation, operationId))
                {
                    var cleared = await _persistence.ClearCandidateUnderMutationGateAsync(
                            candidate!, candidateOperation!, CancellationToken.None, runLease)
                        .ConfigureAwait(false);
                    candidateCleanupPending = !cleared;
                }

                var authenticationIntent = await _persistence.ReadAuthenticationIntentAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (!candidateCleanupPending && TokensEqual(authenticationIntent, operationId))
                {
                    // Fence and intent are separate post-promotion cleanup
                    // phases. A CAS miss means another operation owns newer
                    // recovery state; surface it as pending rather than
                    // returning a falsely clean handoff.
                    candidateCleanupPending = !await ClearCandidateReservationAsync(operationId, runLease)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // The grant promotion is already acknowledged. A failed or
                // unknown cleanup outcome must not turn it into a failed grant;
                // the journal remains available to authenticated/restart
                // recovery (or is already gone if the cleanup committed).
                candidateCleanupPending = true;
            }

            if (cachedSetupValues is not null)
                _configManager.ApplySetupValuesNoLock(cachedSetupValues);

            return new PersistedGrant(result, previousAccessToken, candidateCleanupPending);
        }
        catch (Exception exception)
        {

            operationFailure = exception;
            if (transaction is null)
            {
                // The authenticated token cannot be promoted without a main
                // transaction. Once the prior-state read succeeded, a failed
                // transaction start is a confirmed handoff abort.
                if (priorAccessTokenReadSucceeded)
                {
                    await LogoutAndClearCandidateAsync(
                            adminAccessToken,
                            jellyfinUrl,
                            CancellationToken.None,
                            expectedOperationId: operationId,
                            runLease: runLease)
                        .ConfigureAwait(false);
                    await ClearCandidateReservationAsync(operationId, runLease).ConfigureAwait(false);
                }

                throw;
            }

            // Classify before rollback changes the observed state. A network
            // exception from CommitAsync remains uncertain; a later disposal
            // must not turn it into a guessed rollback.
            var stateAtFailure = transaction.State;
            var commitOutcome = transaction.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(operationFailure, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;

            // Close and dispose before deciding whether any external
            // compensation is legal. A commit acknowledgement loss remains
            // uncertain even if the provider accepts a best-effort rollback.
            var rollbackFailure = await transaction.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await transaction.TryDisposeAsync().ConfigureAwait(false);

            if (commitOutcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
            {
                throw new SetupTransactionRecoveryRequiredException(
                    operationFailure,
                    rollbackFailure,
                    disposeFailure);
            }

            var canCompensate = commitOutcome == SetupCommitOutcome.DefinitivelyAborted
                                && priorAccessTokenReadSucceeded;

            if (canCompensate)
                await CompensateAuthenticatedSessionAsync(
                        adminAccessToken,
                        operationId,
                        jellyfinUrl,
                        cancellationToken,
                        runLease)
                    .ConfigureAwait(false);

            // Only a confirmed provider abort gets another authentication and
            // persistence attempt. The retry is deliberately after the new
            // token's compensating logout, so a failed attempt cannot orphan a
            // Jellyfin session.
            if (commitOutcome == SetupCommitOutcome.DefinitivelyAborted
                && SetupTransactionOutcome.IsRetryableWriteAbort(operationFailure))
                throw new SetupRetryableTransactionException(operationFailure);

            throw;
        }
    }

    private async Task CompensateAuthenticatedSessionAsync(
        string adminAccessToken,
        string operationId,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        await LogoutAndClearCandidateAsync(
                adminAccessToken,
                jellyfinUrl,
                CancellationToken.None,
                expectedOperationId: operationId,
                runLease: runLease)
            .ConfigureAwait(false);
        await ClearCandidateReservationAsync(operationId, runLease).ConfigureAwait(false);
    }

    private async Task LogoutAndClearCandidateAsync(
        string candidateToken,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        bool protectActiveToken = true,
        string? expectedOperationId = null,
        bool protectCompletionOwnedCandidate = true,
        SetupRunLeaseHandle? runLease = null)
    {
        if (protectCompletionOwnedCandidate
            && expectedOperationId is not null
            && await _persistence.CompletionOwnsCandidateAsync(
                    candidateToken, expectedOperationId, CancellationToken.None)
                .ConfigureAwait(false))
        {
            // A late Issue/Renew compensation is older than phase A. It must
            // not logout or clear a token now owned by completion.
            return;
        }

        var activeToken = protectActiveToken
            ? await _persistence.ReadJellyfinApiKeyAsync(CancellationToken.None).ConfigureAwait(false)
            : null;
        Exception? cleanupFailure = null;
        if (!protectActiveToken || !TokensEqual(candidateToken, activeToken))
        {
            try
            {
                await RevokeJellyfinSessionAsync(candidateToken, jellyfinUrl, CancellationToken.None, runLease)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (cleanupFailure is null)
        {
            try
            {
                var journalToken = await _persistence.ReadCandidateSessionTokenAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var journalOperation = await _persistence.ReadCandidateSessionOperationAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                var ownsJournal = expectedOperationId is not null
                                  && journalOperation is not null
                                  && TokensEqual(journalToken, candidateToken)
                                  && TokensEqual(journalOperation, expectedOperationId);
                if (ownsJournal)
                {
                    _ = await _persistence.ClearCandidateUnderMutationGateAsync(
                            candidateToken, expectedOperationId!, CancellationToken.None, runLease)
                        .ConfigureAwait(false);
                }

                var emergency = await _persistence.ReadEmergencyCandidateAsync(CancellationToken.None)
                    .ConfigureAwait(false);
                if (emergency is not null
                    && TokensEqual(emergency.Token, candidateToken)
                    && (expectedOperationId is null
                        || TokensEqual(emergency.OperationId, expectedOperationId)))
                {
                    _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                            emergency.Token,
                            emergency.OperationId,
                            CancellationToken.None,
                            protectCompletionOwnership: protectCompletionOwnedCandidate,
                            runLease: runLease)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        if (cleanupFailure is not null)
            throw new JellyfinSetupException(
                JellyfinSetupFailure.UpstreamFailure,
                "session logout",
                inner: cleanupFailure);
    }

    private async Task RetirePreviousSessionAsync(
        string? previousAccessToken,
        string newAccessToken,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        if (string.IsNullOrWhiteSpace(previousAccessToken)
            || TokensEqual(previousAccessToken, newAccessToken))
            return;

        // Cleanup is after the durable rotation transaction. It must use the
        // caller's token so hosted recovery remains bounded; cancellation
        // leaves the already-committed pending marker for recovery.
        await RevokeJellyfinSessionAsync(previousAccessToken, jellyfinUrl, cancellationToken, runLease)
            .ConfigureAwait(false);

        await _persistence.SaveUnderMutationGateAsync(
                new SetupSecretValues
                {
                    ClearRevocationPending = true,
                    ClearRevocationPendingToken = true,
                },
                completed: false,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);
    }

    public Task<SetupGrantResult> RenewAsync(string username, string password, CancellationToken cancellationToken = default)
        => IssueAsync(username, password, cancellationToken);

    private async Task<SetupRunLeaseHandle> AcquireGrantLeaseAsync(
        string purpose,
        CancellationToken cancellationToken)
    {
        // Grant mutations serialize with one another through the durable lease
        // row, but wait briefly for an older grant operation instead of
        // turning ordinary concurrent renewals into lost handoffs. A live
        // orchestration/verification lease is different: fail closed with a
        // retryable busy response and never race its external mutations.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var lease = await _runLeaseService.TryAcquireAsync(string.Empty, purpose, cancellationToken).ConfigureAwait(false);
            if (lease is not null)
                return lease;
            if (await _runLeaseService.IsActiveSetupRunAsync(cancellationToken).ConfigureAwait(false))
                throw new SetupRunBusyException();
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }

        throw new SetupRunBusyException();
    }

    /// <summary>
    /// Issues the only grant accepted after completion. It is bound to the
    /// persisted repair latch and carries one authenticated Jellyfin session;
    /// it cannot be used to reopen normal setup or mutate provider input.
    /// </summary>
    public async Task<SetupGrantResult> IssueRepairAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var lease = await AcquireGrantLeaseAsync("grant-repair", cancellationToken).ConfigureAwait(false);
        try
        {
            return await IssueRepairCoreAsync(username, password, cancellationToken, lease).ConfigureAwait(false);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SetupGrantResult> IssueRepairCoreAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle lease)
    {
        var options = SetupEnvironmentOptions.FromEnvironment();
        EnsureEnabledAndIncomplete(options);
        if (string.IsNullOrWhiteSpace(password))
            throw new BadHttpRequestException("Password is required.");

        return await _configManager.WithMutationGateAsync(async () =>
        {
            if (!await IsSetupCompletedInCurrentContextAsync(cancellationToken).ConfigureAwait(false)
                || !await IsSetupRepairRequiredInCurrentContextAsync(cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Setup repair is not currently required.");

            var priorAuthenticationIntent = await ReadAuthenticationIntentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (priorAuthenticationIntent is null)
                await EnsureNoRecoveryPendingUnderMutationGateAsync(cancellationToken).ConfigureAwait(false);
            else
                await EnsureNoRecoveryPendingExceptAuthenticationIntentUnderMutationGateAsync(
                        priorAuthenticationIntent,
                        cancellationToken)
                    .ConfigureAwait(false);

            // Allocate all identity/grant values and persist the pre-auth
            // operation intent before the external call. DeviceId is derived
            // solely from this durable operation, so a restart/next re-auth
            // uses the same Jellyfin device without pretending external auth
            // and the database are one atomic transaction.
            var operation = priorAuthenticationIntent ?? $"repair:{Guid.NewGuid():N}";
            var raw = SetupGrantCrypto.GenerateToken();
            var now = _timeProvider.GetUtcNow().UtcDateTime;
            var expires = now + SetupGrantConstants.DefaultGrantLifetime;
            await ReserveAuthenticationIntentAsync(operation, cancellationToken, lease).ConfigureAwait(false);

            JellyfinAuthenticationResult authentication;
            try
            {
                authentication = await AuthenticateJellyfinAdminAsync(
                        NormalizeUsername(username), password, options, operation, cancellationToken, lease)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                await HandleAuthenticationFailureAsync(operation, exception, priorAuthenticationIntent is not null, lease).ConfigureAwait(false);
                throw;
            }

            if (!await lease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
                throw new SetupRunBusyException();

            var token = authentication.AccessToken;
            TransactionLifetime? lifetime = null;
            try
            {
                // This is intentionally the first local action after the token
                // response. The encrypted journal is the literal handoff
                // record before any repair transaction work.
                if (!await _persistence.TrySaveEmergencyCandidateIfEmptyUnderMutationGateAsync(
                        token, operation, CancellationToken.None, lease).ConfigureAwait(false))
                    throw new BadHttpRequestException("Setup repair recovery is pending; recover the existing repair session before continuing.");
                if (!authentication.IsAdministrator)
                    await RejectNonAdministratorAuthenticationAsync(token, operation, options.JellyfinUrl, lease).ConfigureAwait(false);
                try
                {
                    lifetime = new TransactionLifetime(await _beginTransaction(
                            IsolationLevel.Serializable, cancellationToken)
                        .ConfigureAwait(false));
                    await SetupMutationFenceLock.AcquireAsync(_dbContext, lifetime.Transaction, cancellationToken)
                        .ConfigureAwait(false);
                    var journal = await _persistence.ReadEmergencyCandidateAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (journal is null
                        || !TokensEqual(journal.Token, token)
                        || !TokensEqual(journal.OperationId, operation))
                        throw new InvalidOperationException("The authenticated repair session journal changed before handoff.");

                    if (!await IsSetupCompletedInCurrentContextAsync(cancellationToken).ConfigureAwait(false)
                        || !await IsSetupRepairRequiredInCurrentContextAsync(cancellationToken).ConfigureAwait(false))
                        throw new BadHttpRequestException("Setup repair is not currently required.");
                    var row = await _dbContext.SetupGrants.SingleOrDefaultAsync(
                        item => item.Id == SetupGrant.SingletonId, cancellationToken).ConfigureAwait(false);
                    if (row is not null
                        && row.Purpose == SetupGrantConstants.RepairPurpose
                        && !string.IsNullOrWhiteSpace(row.RepairSessionCiphertext))
                        throw new BadHttpRequestException("Setup repair recovery is pending; recover the existing repair session before continuing.");
                    if (row is null)
                    {
                        row = new SetupGrant { Id = SetupGrant.SingletonId };
                        _dbContext.SetupGrants.Add(row);
                    }
                    row.GrantedTokenHash = SetupGrantCrypto.ComputeIssuedTokenHash(raw);
                    row.IssuedAtUtc = now;
                    row.ExpiresAtUtc = expires;
                    row.IsRevoked = false;
                    row.RevokedAtUtc = null;
                    row.IssuedByUsername = NormalizeUsername(username);
                    row.Purpose = SetupGrantConstants.RepairPurpose;
                    // The journal's token is transferred to the repair row in
                    // this transaction. The journal is not cleared until the
                    // commit acknowledgement below.
                    row.RepairSessionCiphertext = _configManager.EncryptSetupSnapshot("setup.grant.repair-session", token);
                    row.RepairSessionOperationId = operation;
                    var intentFence = await _dbContext.SetupMutationFences
                        .SingleAsync(item => item.Id == SetupMutationFence.SingletonId, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(intentFence.ReservedCandidateOperationId, operation, StringComparison.Ordinal))
                        throw new InvalidOperationException("The authenticated repair intent changed before handoff.");
                    // Transfer the durable pre-auth intent into the encrypted
                    // repair row in the same fenced transaction.
                    intentFence.ReservedCandidateOperationId = null;
                    await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (!await lease.AssertCurrentWithinTransactionAsync(lifetime.Transaction, CancellationToken.None).ConfigureAwait(false))
                        throw new BadHttpRequestException("Another setup worker owns the active setup run.");
                    await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                    var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                    if (disposeFailure is not null)
                        throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
                    lifetime = null;
                }
                catch (Exception exception)
                {
                    if (lifetime is null)
                    {
                        // A failed begin has no transaction whose ownership is
                        // uncertain. Compensate only after the exact journaled
                        // token has been durably handed off.
                        await LogoutAndClearCandidateAsync(
                                token,
                                options.JellyfinUrl,
                                CancellationToken.None,
                                protectActiveToken: false,
                                expectedOperationId: operation,
                                protectCompletionOwnedCandidate: false,
                                runLease: lease)
                            .ConfigureAwait(false);
                        await ClearCandidateReservationAsync(operation, lease).ConfigureAwait(false);
                        throw;
                    }

                    var stateAtFailure = lifetime.State;
                    var outcome = lifetime.CommitAttempted
                        ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                        : SetupCommitOutcome.DefinitivelyAborted;
                    var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
                    var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                    _dbContext.ChangeTracker.Clear();
                    if (outcome == SetupCommitOutcome.Uncertain
                        || rollbackFailure is not null
                        || disposeFailure is not null)
                        throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);

                    // No external call is made until the transaction is closed
                    // and disposed. A confirmed abort logs out this exact
                    // token and then performs a token+operation CAS clear.
                    await LogoutAndClearCandidateAsync(
                            token,
                            options.JellyfinUrl,
                            CancellationToken.None,
                            protectActiveToken: false,
                            expectedOperationId: operation,
                            protectCompletionOwnedCandidate: false,
                            runLease: lease)
                        .ConfigureAwait(false);
                    await ClearCandidateReservationAsync(operation, lease).ConfigureAwait(false);
                    throw;
                }

                // The repair row now owns the token. A failed CAS is retained
                // as recovery state rather than clearing a newer journal.
                var cleared = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                        token,
                        operation,
                        CancellationToken.None,
                        protectCompletionOwnership: false,
                        runLease: lease)
                    .ConfigureAwait(false);
                if (!cleared)
                    throw new SetupTransactionRecoveryRequiredException(
                        new IOException("The repair session journal could not be cleared after commit."),
                        null,
                        null);

                return new SetupGrantResult(raw, expires) { Scope = SetupGrantScope.Repair };
            }
            finally
            {
                token = string.Empty;
                _dbContext.ChangeTracker.Clear();
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<string?> ReadRepairSessionAsync(string grant, CancellationToken cancellationToken = default)
    {
        if (await ValidateScopeAsync(grant, cancellationToken).ConfigureAwait(false) != SetupGrantScope.Repair)
            return null;
        var row = await _dbContext.SetupGrants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SetupGrant.SingletonId, cancellationToken).ConfigureAwait(false);
        return row?.RepairSessionCiphertext is { Length: > 0 } ciphertext
            ? _configManager.DecryptSetupSnapshot("setup.grant.repair-session", ciphertext).Plaintext
            : null;
    }

    public async Task<bool> RecoverRepairAsync(
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        var ownedLease = runLease is null
            ? await AcquireGrantLeaseAsync("grant-repair-recover", cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            runLease ??= ownedLease;
            return await _configManager.WithMutationGateAsync(
                    () => RecoverRepairUnderMutationGateAsync(cancellationToken, runLease), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            if (ownedLease is not null)
                await ownedLease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal async Task<bool> RecoverRepairUnderMutationGateAsync(
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        var row = await _dbContext.SetupGrants.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == SetupGrant.SingletonId
                                           && item.Purpose == SetupGrantConstants.RepairPurpose
                                           && !item.IsRevoked,
                cancellationToken).ConfigureAwait(false);
        if (row?.RepairSessionCiphertext is not { Length: > 0 } ciphertext)
            return false;
        if (runLease is not null && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");

        var token = _configManager.DecryptSetupSnapshot("setup.grant.repair-session", ciphertext).Plaintext;
        try
        {
            await RevokeJellyfinSessionAsync(
                    token,
                    SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
                    cancellationToken,
                    runLease)
                .ConfigureAwait(false);
            await RevokeRepairGrantRowAsync(cancellationToken, runLease).ConfigureAwait(false);

            // IssueRepair keeps the emergency journal until the repair-row
            // transaction is acknowledged. Clear only that exact ownership
            // record; this also prevents a restart from logging out the same
            // repair session a second time.
            if (!string.IsNullOrWhiteSpace(row.RepairSessionOperationId))
                _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                        token,
                        row.RepairSessionOperationId,
                        CancellationToken.None,
                        protectCompletionOwnership: false,
                        runLease: runLease)
                    .ConfigureAwait(false);
            return true;
        }
        finally
        {
            token = string.Empty;
        }
    }

    internal async Task CompleteRepairAsync(
        string grant,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        if (runLease is not null && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        var token = await ReadRepairSessionAsync(grant, cancellationToken).ConfigureAwait(false)
                    ?? throw new BadHttpRequestException("Repair grant is invalid.");
        await RevokeJellyfinSessionAsync(
                token,
                SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);
        await RevokeRepairGrantRowAsync(cancellationToken, runLease).ConfigureAwait(false);
    }

    private async Task RevokeRepairGrantRowAsync(
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            var row = await _dbContext.SetupGrants.SingleOrDefaultAsync(item => item.Id == SetupGrant.SingletonId, cancellationToken).ConfigureAwait(false);
            if (row is not null && row.Purpose == SetupGrantConstants.RepairPurpose)
            {
                row.IsRevoked = true;
                row.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                row.GrantedTokenHash = string.Empty;
                row.RepairSessionCiphertext = null;
                row.RepairSessionOperationId = null;
            }
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
        finally { _dbContext.ChangeTracker.Clear(); }
    }

    public async Task<bool> ValidateAsync(string? grant, CancellationToken cancellationToken = default)
        => await ValidateScopeAsync(grant, cancellationToken).ConfigureAwait(false) == SetupGrantScope.Normal;

    public async Task<bool> ValidateAnyAsync(string? grant, CancellationToken cancellationToken = default)
        => await ValidateScopeAsync(grant, cancellationToken).ConfigureAwait(false) is not null;

    public async Task<SetupGrantScope?> ValidateScopeAsync(string? grant, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(grant) || !SetupEnvironmentOptions.FromEnvironment().FullStackEnabled)
            return null;

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var completed = await IsSetupCompletedInCurrentContextAsync(cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupGrants.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == SetupGrant.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || row.IsRevoked || row.ExpiresAtUtc <= _timeProvider.GetUtcNow().UtcDateTime)
                return null;

            var candidate = SetupGrantCrypto.ComputeIssuedTokenHash(grant);
            if (!SetupGrantCrypto.ConstantTimeEquals(candidate, row.GrantedTokenHash))
                return null;

            SetupGrantScope? scope = row.Purpose switch
            {
                SetupGrantConstants.RepairPurpose => SetupGrantScope.Repair,
                SetupGrantConstants.NormalPurpose => SetupGrantScope.Normal,
                _ => null,
            };
            if (scope is null)
                return null;
            if (!completed && scope == SetupGrantScope.Repair)
                return null;
            if (completed && scope == SetupGrantScope.Normal)
                return null;
            if (scope == SetupGrantScope.Repair
                && (!await IsSetupRepairRequiredInCurrentContextAsync(cancellationToken).ConfigureAwait(false)
                    || string.IsNullOrWhiteSpace(row.RepairSessionCiphertext)))
                return null;
            return scope;
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>Returns only the safe pending state used by filters/status.</summary>
    public async Task<bool> IsRevocationPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    /// <summary>
    /// Safe, bounded, credential-free recovery check. It returns a boolean only;
    /// tokens and operation ids never cross the filter boundary. The fence is
    /// held for the complete read so a filter cannot observe a half-published
    /// candidate/completion state.
    /// </summary>
    public async Task<bool> HasRecoveryPendingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _configManager.WithMutationGateAsync(
                    () => HasRecoveryPendingUnderMutationGateAsync(cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // The filter receives only a safe boolean. Any unavailable or
            // malformed recovery store is itself recovery state.
            return true;
        }
    }

    /// <summary>
    /// Returns only recovery state represented in the database. This is the
    /// read-only status snapshot used by GET; it deliberately does not inspect
    /// or rotate the encrypted emergency journal and does not enter the
    /// mutation gate.
    /// </summary>
    internal async Task<bool> HasDatabaseRecoveryPendingAsync(CancellationToken cancellationToken = default)
    {
        var completion = await _dbContext.SetupCompletionOperations.AsNoTracking()
            .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        var configRows = await _dbContext.ConfigItems.AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidate = configRows.Any(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                                              && (canonical == SetupConfigKeys.CandidateSessionToken
                                                  || canonical == SetupConfigKeys.CandidateSessionOperation
                                                  || canonical == SetupConfigKeys.CandidateSessionIntent
                                                  || canonical == SetupConfigKeys.RevocationPendingToken));
        // The marker is a value, not merely a row: normal installations may
        // retain an explicit false marker in the cached DB snapshot.
        var revocation = await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false);
        var reservation = await _dbContext.SetupMutationFences.AsNoTracking()
            .AnyAsync(x => x.Id == SetupMutationFence.SingletonId && x.ReservedCandidateOperationId != null, cancellationToken)
            .ConfigureAwait(false);
        var repair = await _dbContext.SetupGrants.AsNoTracking()
            .AnyAsync(x => x.Id == SetupGrant.SingletonId
                           && x.Purpose == SetupGrantConstants.RepairPurpose
                           && !string.IsNullOrWhiteSpace(x.RepairSessionCiphertext), cancellationToken)
            .ConfigureAwait(false);
        return completion || candidate || revocation || reservation || repair;
    }

    internal async Task<bool> HasRecoveryPendingUnderMutationGateAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            var completion = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken).ConfigureAwait(false);
            var configRows = await _dbContext.ConfigItems.AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var candidate = configRows.Any(row =>
                SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                && (canonical == SetupConfigKeys.CandidateSessionToken
                    || canonical == SetupConfigKeys.CandidateSessionOperation
                    || canonical == SetupConfigKeys.CandidateSessionIntent));
            var revocation = await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false)
                             || configRows.Any(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                                                      && canonical == SetupConfigKeys.RevocationPendingToken);
            bool emergency;
            try
            {
                emergency = await SetupEmergencyCandidateJournal.ExistsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // An unreadable emergency record is recovery state. Fail
                // closed without returning diagnostics or secret material.
                emergency = true;
            }

            var reservation = await _dbContext.SetupMutationFences.AsNoTracking()
                .AnyAsync(x => x.Id == SetupMutationFence.SingletonId
                            && x.ReservedCandidateOperationId != null,
                    cancellationToken).ConfigureAwait(false);
            var repairSession = await _dbContext.SetupGrants.AsNoTracking()
                .AnyAsync(x => x.Id == SetupGrant.SingletonId
                            && x.Purpose == SetupGrantConstants.RepairPurpose
                            && !string.IsNullOrWhiteSpace(x.RepairSessionCiphertext),
                    cancellationToken).ConfigureAwait(false);
            return completion || candidate || revocation || emergency || reservation || repairSession;
        }
        finally
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    public async Task RevokeAsync(CancellationToken cancellationToken = default)
    {
        var lease = await AcquireGrantLeaseAsync("grant-revoke", cancellationToken).ConfigureAwait(false);
        try
        {
            await _configManager.WithMutationGateAsync(
                    () => RevokeUnderMutationGateAsync(cancellationToken, lease),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Phase A of completion. It first persists a fenced, encrypted snapshot
    /// of every exact bearer identity and disables the grant. It deliberately
    /// performs no HTTP. The returned operation id is required by phase C.
    /// </summary>
    internal async Task<SetupCompletionPlan> PrepareCompletionLogoutUnderMutationGateAsync(
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        // Importing is a fenced DB operation. The emergency file is then read
        // again while the phase-A fence is held, so an emergency-only candidate
        // is part of the exact snapshot rather than an inferred current value.
        if (runLease is not null
            && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        await _persistence.ImportEmergencyCandidateUnderMutationGateAsync(cancellationToken, runLease)
            .ConfigureAwait(false);
        if (runLease is not null
            && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");

        var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);

        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            var existing = await _dbContext.SetupCompletionOperations
                .SingleOrDefaultAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (existing is not null)
            {
                var existingPlan = DecryptCompletionPlan(existing);
                await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
                var existingDisposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                if (existingDisposeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(existingDisposeFailure, null, existingDisposeFailure);
                return existingPlan;
            }

            var active = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken).ConfigureAwait(false);
            var revocation = await _persistence.ReadRevocationPendingTokenAsync(cancellationToken).ConfigureAwait(false);
            var revocationPending = await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false);
            var candidate = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken).ConfigureAwait(false);
            var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false);
            var emergency = await _persistence.ReadEmergencyCandidateAsync(cancellationToken).ConfigureAwait(false);
            var operationId = $"completion:{Guid.NewGuid():N}";
            var plan = new SetupCompletionPlan(
                operationId,
                active,
                revocation,
                revocationPending,
                candidate,
                candidateOperation,
                emergency?.Token,
                emergency?.OperationId);

            _dbContext.SetupCompletionOperations.Add(new SetupCompletionOperation
            {
                Id = SetupCompletionOperation.SingletonId,
                OperationId = operationId,
                CreatedAtUtc = _timeProvider.GetUtcNow().UtcDateTime,
                RevocationPending = revocationPending,
                ActiveSessionCiphertext = EncryptCompletionValue("active", active),
                RevocationSessionCiphertext = EncryptCompletionValue("revocation", revocation),
                CandidateSessionCiphertext = EncryptCompletionValue("candidate", candidate),
                CandidateOperationCiphertext = EncryptCompletionValue("candidate-operation", candidateOperation),
                EmergencySessionCiphertext = EncryptCompletionValue("emergency", emergency?.Token),
                EmergencyOperationCiphertext = EncryptCompletionValue("emergency-operation", emergency?.OperationId),
            });

            var grant = await _dbContext.SetupGrants
                .SingleOrDefaultAsync(row => row.Id == SetupGrant.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (grant is not null)
            {
                grant.IsRevoked = true;
                grant.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                grant.GrantedTokenHash = string.Empty;
            }

            // Transfer ownership in this same fenced transaction. Once the
            // encrypted operation exists, older candidate/revocation cleanup
            // must see no mutable rows to delete; phase C and restart use only
            // the immutable snapshot.
            await _persistence.TransferRecoveryStateToCompletionAsync(
                    plan, transaction, cancellationToken)
                .ConfigureAwait(false);
            var fence = await _dbContext.SetupMutationFences
                .SingleAsync(row => row.Id == SetupMutationFence.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (string.Equals(fence.ReservedCandidateOperationId, plan.CandidateOperation, StringComparison.Ordinal))
                fence.ReservedCandidateOperationId = null;

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            return plan;
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
    }

    /// <summary>Phase B: logout only identities captured by phase A.</summary>
    internal Task LogoutCompletionSessionsAsync(
        SetupCompletionPlan plan,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
        => RevokeSessionsAsync(plan.Sessions, jellyfinUrl, cancellationToken, runLease);

    /// <summary>
    /// Phase C. The operation id is a compare-and-swap guard. It reacquires
    /// the database fence, clears only the still-matching snapshot, and writes
    /// setup.completed in SetupConfigPersistence's final SaveChanges call.
    /// </summary>
    internal async Task CompleteAfterExternalLogoutUnderMutationGateAsync(
        SetupCompletionPlan plan,
        string indexerJson,
        string? pluginApiKey,
        string? previousPluginApiKey,
        string? previousPluginApiKeyExpiresAt,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);
        List<ConfigItem>? cacheRows = null;
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            var pending = await _dbContext.SetupCompletionOperations
                .SingleOrDefaultAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (pending is null || !string.Equals(pending.OperationId, plan.OperationId, StringComparison.Ordinal))
            {
                // A different/newer operation owns the state. Never clear it.
                await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
                var noOpDisposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                if (noOpDisposeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(noOpDisposeFailure, null, noOpDisposeFailure);
                return;
            }

            // Candidate and revocation rows were transferred during phase A.
            // There is deliberately no read-back comparison here: requiring
            // those rows would make a fresh-service restart depend on state the
            // completion operation already owns.

            // Retire the emergency file before the DB commit. It is a CAS
            // delete; if commit later fails, the encrypted DB snapshot remains
            // the recovery source. A newer emergency operation is untouched.
            if (plan.EmergencySession is not null && plan.EmergencyOperation is not null)
            {
                if (runLease is not null
                    && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");
                _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                        plan.EmergencySession,
                        plan.EmergencyOperation,
                        cancellationToken,
                        protectCompletionOwnership: false,
                        runLease: runLease,
                        ownerTransaction: transaction)
                    .ConfigureAwait(false);
            }

            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");

            _dbContext.SetupCompletionOperations.Remove(pending);
            var grant = await _dbContext.SetupGrants
                .SingleOrDefaultAsync(row => row.Id == SetupGrant.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (grant is not null)
            {
                grant.IsRevoked = true;
                grant.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                grant.GrantedTokenHash = string.Empty;
            }

            await _persistence.SaveAsync(
                    new SetupSecretValues
                    {
                        Indexers = indexerJson,
                        PluginApiKey = pluginApiKey,
                        PluginApiKeyPrevious = previousPluginApiKey,
                        PluginApiKeyPreviousExpiresAt = previousPluginApiKeyExpiresAt,
                        ClearJellyfinApiKey = true,
                        ClearRevocationPending = true,
                        ClearRevocationPendingToken = true,
                        ClearCandidateSession = true,
                    },
                    completed: true,
                    transaction,
                    rows => cacheRows = rows.Where(row => !IsCandidateKey(row.ConfigName)).ToList(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");

            // No database write is permitted after SaveAsync's marker write.
            await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            if (cacheRows is not null)
                _configManager.ApplySetupValuesNoLock(cacheRows);
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
        finally
        {
            _dbContext.ChangeTracker.Clear();
        }
    }

    // Compatibility overload for callers outside orchestration. A completion
    // operation must already exist; phase B is intentionally not hidden here.
    internal async Task CompleteAfterExternalLogoutUnderMutationGateAsync(
        string indexerJson,
        string? pluginApiKey,
        string? previousPluginApiKey,
        string? previousPluginApiKeyExpiresAt,
        CancellationToken cancellationToken)
    {
        var plan = await ReadCompletionPlanAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Completion phase A has not been persisted.");
        await CompleteAfterExternalLogoutUnderMutationGateAsync(
                plan, indexerJson, pluginApiKey, previousPluginApiKey,
                previousPluginApiKeyExpiresAt, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Revokes the current and any previously pending external sessions first,
    /// then durably removes recovery ciphertext and grant. Logout is
    /// idempotent (including Jellyfin 404), so a process crash or DB failure
    /// after logout can safely retry cleanup.
    /// The caller must hold ConfigManager's mutation gate.
    /// </summary>
    internal async Task RevokeUnderMutationGateAsync(
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        if (runLease is not null && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new SetupRunBusyException();
        if (await IsCompletionPendingUnderMutationGateAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Setup completion cleanup is pending; retry completion recovery.");
        await _persistence.ImportEmergencyCandidateUnderMutationGateAsync(cancellationToken, runLease)
            .ConfigureAwait(false);
        var storedAccessToken = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken)
            .ConfigureAwait(false);
        var pendingAccessToken = await _persistence.ReadRevocationPendingTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidateAccessToken = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var authenticationIntent = await ReadAuthenticationIntentAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasCandidateState = !string.IsNullOrWhiteSpace(candidateAccessToken)
                                || !string.IsNullOrWhiteSpace(candidateOperation);

        // Phase 1 commits before any network call. It invalidates the grant and
        // leaves the token(s) needed for restart recovery encrypted in storage.
        await PersistRevocationIntentAsync(cancellationToken, runLease).ConfigureAwait(false);

        await RevokeSessionsAsync(
                [candidateAccessToken, storedAccessToken, pendingAccessToken],
                SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);

        if (runLease is not null && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new SetupRunBusyException();

        var reservationCleared = true;
        if (hasCandidateState)
        {
            if (string.IsNullOrWhiteSpace(candidateAccessToken)
                || string.IsNullOrWhiteSpace(candidateOperation))
                throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");

            var candidateCleared = await _persistence.ClearCandidateUnderMutationGateAsync(
                    candidateAccessToken,
                    candidateOperation,
                    CancellationToken.None,
                    runLease)
                .ConfigureAwait(false);
            if (!candidateCleared)
                throw new BadHttpRequestException("Setup session recovery changed during revoke; retry cleanup.");
            reservationCleared = await ClearCandidateReservationAsync(candidateOperation, runLease)
                .ConfigureAwait(false);
        }
        else if (!string.IsNullOrWhiteSpace(authenticationIntent))
        {
            // A crash can leave only the encrypted pre-auth intent: the token
            // response was never journaled. Revoke the durable session(s) above,
            // then clear only this exact intent/fence pair. A newer intent is
            // deliberately retained and keeps recovery pending.
            reservationCleared = await ClearCandidateReservationAsync(authenticationIntent, runLease)
                .ConfigureAwait(false);
        }

        if (!reservationCleared)
            throw new BadHttpRequestException("Setup session recovery changed during revoke; retry cleanup.");

        var emergency = await _persistence.ReadEmergencyCandidateAsync(CancellationToken.None)
            .ConfigureAwait(false);
        if (emergency is not null
            && TokensEqual(emergency.Token, candidateAccessToken)
            && TokensEqual(emergency.OperationId, candidateOperation))
        {
            _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                    emergency.Token,
                    emergency.OperationId,
                    CancellationToken.None,
                    runLease: runLease)
                .ConfigureAwait(false);
        }

        if (runLease is not null && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new SetupRunBusyException();

        // Phase 3 is deliberately separate. A database failure or hosted
        // timeout leaves the marker/ciphertext durable and the next recovery
        // retries 404 safely.
        await _persistence.SaveUnderMutationGateAsync(
                new SetupSecretValues
                {
                    ClearJellyfinApiKey = true,
                    ClearRevocationPending = true,
                    ClearRevocationPendingToken = true,
                },
                completed: false,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Authenticated recovery for a pending cleanup. The local password and
    /// Jellyfin administrator re-authenticate this request. The newly-created
    /// authentication token is journaled encrypted before cleanup and cleared
    /// only after cleanup succeeds.
    /// </summary>
    public async Task<bool> RecoverRevocationAsync(
        string username,
        string password,
        CancellationToken cancellationToken = default)
    {
        var lease = await AcquireGrantLeaseAsync("grant-recovery", cancellationToken).ConfigureAwait(false);
        try
        {
            return await RecoverRevocationCoreAsync(username, password, cancellationToken, lease).ConfigureAwait(false);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<bool> RecoverRevocationCoreAsync(
        string username,
        string password,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle lease)
    {
        if (!await lease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new SetupRunBusyException();
        var normalizedUsername = NormalizeUsername(username);
        if (string.IsNullOrWhiteSpace(password))
            throw new BadHttpRequestException("Password is required.");

        var options = SetupEnvironmentOptions.FromEnvironment();
        EnsureEnabledAndIncomplete(options);

        return await _configManager.WithMutationGateAsync(
                async () =>
                {
                    if (await IsCompletionPendingUnderMutationGateAsync(cancellationToken).ConfigureAwait(false))
                        throw new BadHttpRequestException("Setup completion cleanup is pending; retry completion recovery.");
                    await _persistence.ImportEmergencyCandidateUnderMutationGateAsync(cancellationToken, lease)
                        .ConfigureAwait(false);
                    // A pre-auth intent may be the only durable record when a
                    // process crashed before the initial local admin account
                    // was created. Jellyfin authentication below is the
                    // credential proof for that narrow recovery case; all
                    // ordinary cleanup/replay paths still require the local
                    // administrator as before.
                    var authenticationIntent = await ReadAuthenticationIntentAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (authenticationIntent is null)
                        await EnsureExistingLocalAdminAccountAsync(normalizedUsername, password, cancellationToken)
                            .ConfigureAwait(false);

                    var hasRevocation = await IsRevocationPendingAsync(cancellationToken).ConfigureAwait(false);
                    var pendingToken = await _persistence.ReadRevocationPendingTokenAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var candidate = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken)
                        .ConfigureAwait(false);
                    var hasCandidate = !string.IsNullOrWhiteSpace(candidate)
                                       || !string.IsNullOrWhiteSpace(candidateOperation);
                    if (!hasRevocation
                        && string.IsNullOrWhiteSpace(pendingToken)
                        && !hasCandidate
                        && string.IsNullOrWhiteSpace(authenticationIntent))
                    {
                        // A recovery request can be replayed after its success
                        // response was lost. Treat the already-reconciled state
                        // as success, but expose no existing grant material.
                        return true;
                    }

                    // Never overwrite an older candidate with the fresh
                    // authentication session. Reconcile the durable journal
                    // first; if this phase fails, the old candidate remains.
                    // When the candidate belongs to an older uncertain
                    // authentication intent, do not logout or clear it before
                    // credential validation. A 401/403 retry must leave that
                    // session and intent untouched; successful same-device
                    // auth replaces the journal before cleanup. Candidates
                    // without an auth intent retain the older reconciliation
                    // behavior.
                    if (hasCandidate && authenticationIntent is null)
                    {
                        await RecoverPendingUnderMutationGateAsync(
                                null,
                                options.JellyfinUrl,
                                cancellationToken,
                                lease)
                            .ConfigureAwait(false);
                    }

                    var operationId = authenticationIntent ?? $"recovery:{Guid.NewGuid():N}";
                    await ReserveAuthenticationIntentAsync(operationId, cancellationToken, lease).ConfigureAwait(false);
                    JellyfinAuthenticationResult reauthentication;
                    try
                    {
                        reauthentication = await AuthenticateJellyfinAdminAsync(
                                normalizedUsername,
                                password,
                                options,
                                operationId,
                                cancellationToken,
                                lease)
                            .ConfigureAwait(false);
                    }
                    catch (Exception exception)
                    {
                        await HandleAuthenticationFailureAsync(operationId, exception, authenticationIntent is not null, lease).ConfigureAwait(false);
                        throw;
                    }
                    var reauthenticatedToken = reauthentication.AccessToken;
                    await PersistCandidateSessionAsync(
                            reauthenticatedToken,
                            operationId,
                            options.JellyfinUrl,
                            cancellationToken,
                            lease)
                        .ConfigureAwait(false);
                    if (!reauthentication.IsAdministrator)
                        await RejectNonAdministratorAuthenticationAsync(reauthenticatedToken, operationId, options.JellyfinUrl, lease).ConfigureAwait(false);
                    if (authenticationIntent is null)
                        await ClearCandidateReservationAsync(operationId, lease).ConfigureAwait(false);
                    // The fresh re-authentication is journaled before any
                    // cleanup. Recovery logs it out on success and leaves the
                    // candidate for the next attempt on every failure path.
                    await RecoverPendingUnderMutationGateAsync(
                            null,
                            options.JellyfinUrl,
                            cancellationToken,
                            lease)
                        .ConfigureAwait(false);
                    await ClearCandidateReservationAsync(operationId, lease).ConfigureAwait(false);
                    return true;
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded, credential-free restart recovery. It only acts on the durable
    /// cleanup marker or candidate journal and never issues a grant or changes
    /// setup configuration.
    /// </summary>
    public async Task<bool> RecoverPendingAsync(CancellationToken cancellationToken = default)
    {
        var options = SetupEnvironmentOptions.FromEnvironment();
        if (!options.FullStackEnabled)
            return false;

        var lease = await AcquireGrantLeaseAsync("recovery-startup", cancellationToken).ConfigureAwait(false);
        try
        {
            return await _configManager.WithMutationGateAsync(
                    () => RecoverPendingUnderMutationGateAsync(null, options.JellyfinUrl, cancellationToken, lease),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal async Task<bool> RecoverPendingUnderMutationGateAsync(
        string? reauthenticatedToken,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        // Completion pending has priority over every older recovery record. It
        // is recovered using only the exact phase-A snapshot; importing or
        // authenticating a new candidate here could orphan a newer session.
        var completion = await ReadCompletionPlanAsync(cancellationToken).ConfigureAwait(false);
        if (completion is not null)
        {
            await LogoutCompletionSessionsAsync(completion, jellyfinUrl, cancellationToken, runLease).ConfigureAwait(false);

            // Phase A cannot hold the emergency journal lock across the
            // external logout. An issuer can therefore publish a newer
            // emergency candidate after the immutable snapshot was created.
            // It is foreign to completion: retire that exact token and use a
            // token+operation CAS so a newer replacement is never deleted.
            var lateEmergency = await _persistence.ReadEmergencyCandidateAsync(cancellationToken)
                .ConfigureAwait(false);
            if (lateEmergency is not null
                && !(TokensEqual(lateEmergency.Token, completion.EmergencySession)
                     && TokensEqual(lateEmergency.OperationId, completion.EmergencyOperation)))
            {
                await RevokeSessionsAsync([lateEmergency.Token], jellyfinUrl, cancellationToken, runLease)
                    .ConfigureAwait(false);
                _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                        lateEmergency.Token,
                        lateEmergency.OperationId,
                        CancellationToken.None,
                        protectCompletionOwnership: false,
                        runLease: runLease)
                    .ConfigureAwait(false);
            }

            await CompleteAfterExternalLogoutUnderMutationGateAsync(
                    completion,
                    _configManager.GetSetupIndexers() ?? "[]",
                    _configManager.GetSetupPluginApiKey(),
                    _configManager.GetSetupPluginApiKeyPrevious(),
                    _configManager.GetSetupPluginApiKeyPreviousExpiresAtUtc()
                        ?.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    cancellationToken,
                    runLease)
                .ConfigureAwait(false);
            return true;
        }

        // A repair worker can crash after Jellyfin authentication. Recover its
        // exact encrypted session before considering ordinary setup journals;
        // this never clears the repair latch.
        await RecoverRepairUnderMutationGateAsync(cancellationToken, runLease).ConfigureAwait(false);

        await _persistence.ImportEmergencyCandidateUnderMutationGateAsync(cancellationToken, runLease)
            .ConfigureAwait(false);
        var emergencyCandidate = await _persistence.ReadEmergencyCandidateAsync(cancellationToken)
            .ConfigureAwait(false);
        var pending = await IsRevocationPendingAsync(cancellationToken).ConfigureAwait(false);
        var candidateAccessToken = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken)
            .ConfigureAwait(false);
        var pendingAccessToken = await _persistence.ReadRevocationPendingTokenAsync(cancellationToken)
            .ConfigureAwait(false);
        var currentAccessToken = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken)
            .ConfigureAwait(false);
        var authenticationIntent = await ReadAuthenticationIntentAsync(cancellationToken)
            .ConfigureAwait(false);
        var hasPendingToken = !string.IsNullOrWhiteSpace(pendingAccessToken);
        var grantState = await ReadDurableGrantStateAsync(cancellationToken).ConfigureAwait(false);
        var hasLiveGrant = grantState.Live;
        if (!pending
            && !hasPendingToken
            && string.IsNullOrWhiteSpace(candidateAccessToken)
            && string.IsNullOrWhiteSpace(candidateOperation)
            && emergencyCandidate is null)
        {
            if (string.IsNullOrWhiteSpace(authenticationIntent))
                return false;

            // Promotion commits the active session and grant before its
            // separate fence/intent cleanup phase. If the process dies in
            // that narrow window, the live grant proves that this intent is
            // already owned by the durable current session; clear only the
            // exact reservation and do not reauthenticate or logout it.
            if (hasLiveGrant && !string.IsNullOrWhiteSpace(currentAccessToken))
            {
                if (!await ClearCandidateReservationAsync(authenticationIntent, runLease)
                        .ConfigureAwait(false))
                    throw new BadHttpRequestException("Setup session recovery changed during promotion cleanup; retry recovery.");
                return true;
            }

            // Stock Jellyfin 10.11 SessionInfoDto does not contain a bearer
            // token. A restart therefore cannot safely discover or revoke the
            // uncertain session. Keep the pre-auth intent until an
            // administrator reauthenticates with the same deterministic
            // DeviceId; Jellyfin then invalidates the old device token and
            // returns the replacement, which is journaled before handoff.
            throw new SetupAuthenticationRecoveryRequiredException(
                authenticationIntent,
                new InvalidOperationException("Administrator reauthentication is required to recover the setup session."));
        }

        var candidateIsActiveLiveGrant = !string.IsNullOrWhiteSpace(candidateAccessToken)
                                         && !string.IsNullOrWhiteSpace(currentAccessToken)
                                         && TokensEqual(candidateAccessToken, currentAccessToken)
                                         && hasLiveGrant;
        var currentGrantWasRevoked = grantState.Revoked;
        var candidateToRevoke = candidateIsActiveLiveGrant ? null : candidateAccessToken;
        var emergencyIsActiveLiveGrant = emergencyCandidate is not null
                                         && !string.IsNullOrWhiteSpace(currentAccessToken)
                                         && TokensEqual(emergencyCandidate.Token, currentAccessToken)
                                         && hasLiveGrant;
        var emergencyToRevoke = emergencyIsActiveLiveGrant ? null : emergencyCandidate?.Token;
        var temporaryReauthenticationToken = TokensEqual(reauthenticatedToken, currentAccessToken)
            ? null
            : reauthenticatedToken;

        // A pending token is the previous session retained by a successful
        // rotation and is always retired. If the marker has no separate
        // token, it represents a revoke intent for the current session. A
        // revoked grant also proves that a revoke intent targeted the current
        // session even when an older rotation token is retained. Never infer
        // current-session revocation merely from the presence of a candidate:
        // a live active grant must survive candidate recovery.
        var candidateIsTheStaleCurrentSession = !hasLiveGrant
                                                 && candidateToRevoke is not null
                                                 && TokensEqual(candidateToRevoke, currentAccessToken);
        var shouldRevokeCurrent = (pending
                                   && (!hasPendingToken || currentGrantWasRevoked))
                                  || candidateIsTheStaleCurrentSession;
        var currentTokenToRevoke = shouldRevokeCurrent ? currentAccessToken : null;
        var clearCurrentSession = TokensEqual(currentTokenToRevoke, currentAccessToken)
                                  && !string.IsNullOrWhiteSpace(currentAccessToken);
        if (hasLiveGrant && clearCurrentSession)
        {
            // A marker that targets the current session must invalidate the
            // grant before the external logout. This preserves the invariant
            // that no valid grant can point at a token once it is revoked.
            await PersistRevocationIntentAsync(cancellationToken, runLease).ConfigureAwait(false);
        }

        await RevokeSessionsAsync(
                [candidateToRevoke, emergencyToRevoke, temporaryReauthenticationToken, pendingAccessToken, currentTokenToRevoke],
                jellyfinUrl,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);

        var candidateCleared = false;
        if (!string.IsNullOrWhiteSpace(candidateAccessToken)
            && !string.IsNullOrWhiteSpace(candidateOperation))
        {
            candidateCleared = await _persistence.ClearCandidateUnderMutationGateAsync(
                    candidateAccessToken,
                    candidateOperation,
                    CancellationToken.None,
                    runLease)
                .ConfigureAwait(false);
        }
        if (emergencyCandidate is not null)
        {
            _ = await _persistence.ClearEmergencyCandidateUnderMutationGateAsync(
                    emergencyCandidate.Token,
                    emergencyCandidate.OperationId,
                    CancellationToken.None,
                    runLease: runLease)
                .ConfigureAwait(false);
        }

        await _persistence.SaveUnderMutationGateAsync(
                new SetupSecretValues
                {
                    ClearJellyfinApiKey = clearCurrentSession,
                    ClearRevocationPending = pending,
                    ClearRevocationPendingToken = pending || hasPendingToken,
                },
                completed: false,
                cancellationToken,
                runLease)
            .ConfigureAwait(false);

        if (candidateCleared && candidateOperation is not null)
            await ClearCandidateReservationAsync(candidateOperation, runLease).ConfigureAwait(false);

        return true;
    }

    private async Task PersistRevocationIntentAsync(CancellationToken cancellationToken, SetupRunLeaseHandle? runLease = null)
    {
        var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            var row = await _dbContext.SetupGrants
                .SingleOrDefaultAsync(x => x.Id == SetupGrant.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (row is not null)
            {
                row.IsRevoked = true;
                row.RevokedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
                row.GrantedTokenHash = string.Empty;
            }

            List<ConfigItem>? cacheRows = null;
            await _persistence.SaveAsync(
                    new SetupSecretValues { RevocationPending = true },
                    completed: false,
                    transaction,
                    rows => cacheRows = rows
                        .Where(row => !IsCandidateKey(row.ConfigName))
                        .ToList(),
                    cancellationToken)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);

            if (cacheRows is not null)
            {
                var cachedJellyfin = cacheRows.FirstOrDefault(row => row.ConfigName == SetupConfigKeys.JellyfinApiKey);
                if (cachedJellyfin is null)
                {
                    cacheRows.Add(new ConfigItem
                    {
                        ConfigName = SetupConfigKeys.JellyfinApiKey,
                        ConfigValue = string.Empty,
                        IsEncrypted = false,
                    });
                }
                else
                {
                    cachedJellyfin.ConfigValue = string.Empty;
                    cachedJellyfin.IsEncrypted = false;
                }

                _configManager.ApplySetupValuesNoLock(cacheRows);
            }
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            _dbContext.ChangeTracker.Clear();
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
    }

    private async Task EnsureLocalAdminAccountAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Type == Account.AccountType.Admin && x.Username == username, cancellationToken)
            .ConfigureAwait(false);

        if (admin is not null)
        {
            if (!PasswordUtil.Verify(admin.PasswordHash, password, admin.RandomSalt))
                throw new UnauthorizedAccessException("Invalid local administrator credentials.");

            return;
        }

        var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var record = new Account
        {
            Type = Account.AccountType.Admin,
            Username = username,
            RandomSalt = salt,
            PasswordHash = PasswordUtil.Hash(password, salt),
        };

        _dbContext.Accounts.Add(record);
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureExistingLocalAdminAccountAsync(string username, string password, CancellationToken cancellationToken)
    {
        var admin = await _dbContext.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.Type == Account.AccountType.Admin && x.Username == username, cancellationToken)
            .ConfigureAwait(false);
        if (admin is null || !PasswordUtil.Verify(admin.PasswordHash, password, admin.RandomSalt))
            throw new UnauthorizedAccessException("Invalid local administrator credentials.");
    }

    private async Task HandleAuthenticationFailureAsync(string operationId, Exception exception, bool preserveIntent, SetupRunLeaseHandle? runLease = null)
    {
        // Only an HTTP 401/403 produced by the authentication endpoint proves
        // that Jellyfin rejected the credentials before returning a session.
        // Every other outcome, including invalid JSON and cancellation, keeps
        // the durable intent for deterministic session recovery. Cancellation
        // remains an OperationCanceledException so callers can safely stop a
        // request without losing the durable recovery signal.
        if (exception is OperationCanceledException)
            throw exception;
        // Policy validation is performed only after the authenticated token has
        // been journaled. A transport failure has no known token and therefore
        // remains represented by the pre-auth intent.
        if (IsExplicitAuthenticationRejection(exception))
        {
            // A wrong credential retry must not erase the older uncertain
            // intent. The old device token is still authoritative until a
            // successful same-device authentication replaces it and its
            // replacement is journaled. Only a brand-new failed handoff may
            // clear its own reservation.
            if (!preserveIntent)
                await ClearCandidateReservationAsync(operationId, runLease).ConfigureAwait(false);
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception).Throw();
        }

        throw new SetupAuthenticationRecoveryRequiredException(operationId, exception);
    }

    private static bool IsExplicitAuthenticationRejection(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is JellyfinSetupException jellyfin
                && jellyfin.Failure == JellyfinSetupFailure.Unauthorized
                && jellyfin.StatusCode is 401 or 403)
                return true;
        }

        return false;
    }

    private async Task RejectNonAdministratorAuthenticationAsync(
        string token,
        string operationId,
        string jellyfinUrl,
        SetupRunLeaseHandle lease)
    {
        try
        {
            await LogoutAndClearCandidateAsync(
                    token,
                    jellyfinUrl,
                    CancellationToken.None,
                    protectActiveToken: false,
                    expectedOperationId: operationId,
                    protectCompletionOwnedCandidate: false,
                    runLease: lease)
                .ConfigureAwait(false);
            await ClearCandidateReservationAsync(operationId, lease).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The encrypted candidate and pre-auth reservation remain intact
            // when logout fails; restart/recovery can safely retry the logout.
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "session logout", inner: exception);
        }

        throw new UnauthorizedAccessException(
            "Jellyfin administrator privileges are required.",
            new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "authentication", sessionCreated: true));
    }

    private async Task<JellyfinAuthenticationResult> AuthenticateJellyfinAdminAsync(
        string username,
        string password,
        SetupEnvironmentOptions options,
        string operationId,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        try
        {
            using var httpClient = _httpClientFactory();
            httpClient.BaseAddress = new Uri(options.JellyfinUrl);
            var setupOptions = new JellyfinSetupOptions(options.JellyfinUrl, username, password)
            {
                ClientName = "NzbDav Setup",
                DeviceName = "NzbDav Setup",
                DeviceId = SetupAuthenticationIdentity.DeviceId(operationId),
                AssertMutationLeaseResultAsync = runLease is null
                    ? null
                    : async token => await runLease.AssertCurrentAsync(token).ConfigureAwait(false),
            };
            var setupClient = new JellyfinSetupClient(httpClient, setupOptions);
            var auth = await setupClient.AuthenticateTransportAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(auth.AccessToken))
                throw new JellyfinSetupException(JellyfinSetupFailure.InvalidResponse, "authentication");

            return auth;
        }
        catch (JellyfinSetupException ex) when (ex.Failure == JellyfinSetupFailure.Unauthorized
                                                || (ex.Failure == JellyfinSetupFailure.InvalidResponse && ex.SessionCreated))
        {
            throw new UnauthorizedAccessException("Jellyfin authentication failed.", ex);
        }
    }

    private async Task<SetupGrantResult> EnsureAdminAndRotateGrantAsync(
        string username,
        CancellationToken cancellationToken,
        IDbContextTransaction? transaction = null,
        SetupRunLeaseHandle? runLease = null)
    {
        var raw = SetupGrantCrypto.GenerateToken();
        var hash = SetupGrantCrypto.ComputeIssuedTokenHash(raw);
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var expiresAt = now.Add(SetupGrantConstants.DefaultGrantLifetime);

        var ownsTransaction = transaction is null;
        var ownedTransaction = ownsTransaction
            ? await _dbContext.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false)
            : null;
        transaction ??= ownedTransaction;
        var lifetime = ownsTransaction ? new TransactionLifetime(transaction!) : null;

        try
        {
            if (ownsTransaction)
                await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction!, cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupGrants.SingleOrDefaultAsync(x => x.Id == SetupGrant.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (row is null)
            {
                row = new SetupGrant
                {
                    Id = SetupGrant.SingletonId,
                };
                _dbContext.SetupGrants.Add(row);
            }

            row.GrantedTokenHash = hash;
            row.IssuedAtUtc = now;
            row.ExpiresAtUtc = expiresAt;
            row.IsRevoked = false;
            row.RevokedAtUtc = null;
            row.IssuedByUsername = username;
            row.Purpose = SetupGrantConstants.NormalPurpose;
            row.RepairSessionCiphertext = null;
            row.RepairSessionOperationId = null;

            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction!, cancellationToken).ConfigureAwait(false))
                throw new SetupRunBusyException();
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (ownsTransaction)
            {
                await lifetime!.CommitAsync(cancellationToken).ConfigureAwait(false);
                var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                if (disposeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            }

            return new SetupGrantResult(raw, expiresAt)
            {
                Scope = SetupGrantScope.Normal,
            };
        }
        catch (Exception exception)
        {
            if (!ownsTransaction)
                throw;

            var stateAtFailure = lifetime!.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
    }

    private string? EncryptCompletionValue(string key, string? value)
        => string.IsNullOrWhiteSpace(value) ? null : _configManager.EncryptSetupSnapshot($"setup.completion.{key}", value);

    private string? DecryptCompletionValue(string key, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : _configManager.DecryptSetupSnapshot($"setup.completion.{key}", value).Plaintext;

    private SetupCompletionPlan DecryptCompletionPlan(SetupCompletionOperation row)
        => new(
            row.OperationId,
            DecryptCompletionValue("active", row.ActiveSessionCiphertext),
            DecryptCompletionValue("revocation", row.RevocationSessionCiphertext),
            row.RevocationPending,
            DecryptCompletionValue("candidate", row.CandidateSessionCiphertext),
            DecryptCompletionValue("candidate-operation", row.CandidateOperationCiphertext),
            DecryptCompletionValue("emergency", row.EmergencySessionCiphertext),
            DecryptCompletionValue("emergency-operation", row.EmergencyOperationCiphertext));

    private async Task<SetupCompletionPlan?> ReadCompletionPlanAsync(CancellationToken cancellationToken)
    {
        var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var lifetime = new TransactionLifetime(transaction);
        try
        {
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
            var row = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            return row is null ? null : DecryptCompletionPlan(row);
        }
        catch (Exception exception)
        {
            var stateAtFailure = lifetime.State;
            var outcome = lifetime.CommitAttempted
                ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                : SetupCommitOutcome.DefinitivelyAborted;
            var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            if (outcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null
                || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
            throw;
        }
    }

    private async Task<bool> ReadRevocationPendingMarkerAsync(CancellationToken cancellationToken)
    {
        var rows = await _dbContext.ConfigItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var matching = rows.Where(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                                         && canonical == SetupConfigKeys.RevocationPending).ToList();
        if (matching.Count > 1)
            throw new InvalidOperationException("Duplicate setup revocation marker rows detected.");
        if (matching.Count == 0)
            return false;
        var row = matching[0];
        var value = row.IsEncrypted
            ? _configManager.DecryptSetupValue(SetupConfigKeys.RevocationPending, row.ConfigValue).Plaintext
            : row.ConfigValue;
        return !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(value);
    }

    private async Task EnsureCompletionSnapshotStillCurrentAsync(SetupCompletionPlan plan, CancellationToken cancellationToken)
    {
        var active = await _persistence.ReadJellyfinApiKeyAsync(cancellationToken).ConfigureAwait(false);
        var revocation = await _persistence.ReadRevocationPendingTokenAsync(cancellationToken).ConfigureAwait(false);
        var marker = await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false);
        var candidate = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken).ConfigureAwait(false);
        var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false);
        var emergency = await _persistence.ReadEmergencyCandidateAsync(cancellationToken).ConfigureAwait(false);
        if (!TokensEqual(active, plan.ActiveSession)
            || !TokensEqual(revocation, plan.RevocationSession)
            || marker != plan.RevocationPending
            || !TokensEqual(candidate, plan.CandidateSession)
            || !TokensEqual(candidateOperation, plan.CandidateOperation)
            || !TokensEqual(emergency?.Token, plan.EmergencySession)
            || !TokensEqual(emergency?.OperationId, plan.EmergencyOperation))
            throw new InvalidOperationException("Completion snapshot no longer owns setup recovery state.");
    }

    private static bool IsCandidateKey(string configName)
        => string.Equals(configName, SetupConfigKeys.CandidateSessionToken, StringComparison.Ordinal)
           || string.Equals(configName, SetupConfigKeys.CandidateSessionOperation, StringComparison.Ordinal)
           || string.Equals(configName, SetupConfigKeys.CandidateSessionIntent, StringComparison.Ordinal);

    private static bool TryReadCanonicalSetupCompletedValue(ConfigItem configItem, out bool completed)
    {
        completed = false;

        if (!SensitiveConfigKeys.TryGetCanonicalSetupKey(configItem.ConfigName, out var canonical)
            || !string.Equals(canonical, SetupConfigKeys.Completed, StringComparison.Ordinal))
            return false;

        return bool.TryParse(configItem.ConfigValue, out completed);
    }

    private async Task<bool> IsSetupCompletedInCurrentContextAsync(CancellationToken cancellationToken)
    {
        _dbContext.ChangeTracker.Clear();

        var configItems = await _dbContext.ConfigItems
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = false;
        foreach (var row in configItems)
        {
            if (!TryReadCanonicalSetupCompletedValue(row, out var completed))
                continue;

            if (found)
                throw new InvalidOperationException("Duplicate setup completion rows detected.");

            found = true;
            if (completed)
                return true;
        }

        return false;
    }

    private async Task EnsureSetupNotCompletedAsync(CancellationToken cancellationToken)
    {
        if (!await IsSetupCompletedInCurrentContextAsync(cancellationToken).ConfigureAwait(false))
            return;

        // This compatibility helper is used only by narrow recovery paths.
        if (await IsSetupRepairRequiredInCurrentContextAsync(cancellationToken).ConfigureAwait(false))
            return;

        throw new BadHttpRequestException("Setup has already completed.");
    }

    private async Task EnsureNormalSetupNotCompletedAsync(CancellationToken cancellationToken)
    {
        if (await IsSetupCompletedInCurrentContextAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Setup has already completed.");
    }

    private async Task<bool> IsSetupRepairRequiredInCurrentContextAsync(CancellationToken cancellationToken)
    {
        var repairRows = await _dbContext.ConfigItems.AsNoTracking()
            .Where(row => row.ConfigName == SetupConfigKeys.RepairRequired
                       || row.ConfigName.ToLower() == SetupConfigKeys.RepairRequired
                       || row.ConfigName == SetupConfigKeys.RepairCheckedAtUtc
                       || row.ConfigName.ToLower() == SetupConfigKeys.RepairCheckedAtUtc)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var repairRow = repairRows.SingleOrDefault(row => string.Equals(
            row.ConfigName, SetupConfigKeys.RepairRequired, StringComparison.OrdinalIgnoreCase));
        var checkedRow = repairRows.SingleOrDefault(row => string.Equals(
            row.ConfigName, SetupConfigKeys.RepairCheckedAtUtc, StringComparison.OrdinalIgnoreCase));
        if (repairRow is null || checkedRow is null)
            return false;

        var value = repairRow.IsEncrypted
            ? _configManager.DecryptSetupValue(SetupConfigKeys.RepairRequired, repairRow.ConfigValue).Plaintext
            : repairRow.ConfigValue;
        var checkedValue = checkedRow.IsEncrypted
            ? _configManager.DecryptSetupValue(SetupConfigKeys.RepairCheckedAtUtc, checkedRow.ConfigValue).Plaintext
            : checkedRow.ConfigValue;
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
               && DateTime.TryParse(checkedValue, CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out _);
    }

    private async Task EnsureNoRecoveryPendingUnderMutationGateAsync(CancellationToken cancellationToken)
    {
        if (await HasRecoveryPendingUnderMutationGateAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
    }

    private async Task EnsureNoRecoveryPendingExceptAuthenticationIntentUnderMutationGateAsync(
        string operationId,
        CancellationToken cancellationToken)
    {
        var candidate = await _persistence.ReadCandidateSessionTokenAsync(cancellationToken).ConfigureAwait(false);
        var candidateOperation = await _persistence.ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(candidate) || !string.IsNullOrWhiteSpace(candidateOperation))
            throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
        if (await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(item => item.Id == SetupCompletionOperation.SingletonId, cancellationToken)
            .ConfigureAwait(false)
            || await _persistence.ReadEmergencyCandidateAsync(cancellationToken).ConfigureAwait(false) is not null
            || await IsRevocationPendingAsync(cancellationToken).ConfigureAwait(false)
            || !string.IsNullOrWhiteSpace(await _persistence.ReadRevocationPendingTokenAsync(cancellationToken).ConfigureAwait(false)))
            throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");

        var intent = await ReadAuthenticationIntentAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(intent, operationId, StringComparison.Ordinal))
            throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
    }

    private async Task<string?> ReadAuthenticationIntentAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            var durableIntent = await _persistence.ReadAuthenticationIntentAsync(cancellationToken)
                .ConfigureAwait(false);
            var reservedIntent = await _dbContext.SetupMutationFences.AsNoTracking()
                .Where(item => item.Id == SetupMutationFence.SingletonId)
                .Select(item => item.ReservedCandidateOperationId)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(durableIntent) ? reservedIntent : durableIntent;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Durable pre-auth intent. The setup mutation fence row tracks the active
    /// in-transaction reservation owner, while a persisted setup intent key
    /// records the unresolved authentication attempt for deterministic recovery.
    /// </summary>
    private async Task ReserveAuthenticationIntentAsync(
        string operationId,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("An authentication operation id is required.", nameof(operationId));

        await using var transaction = await _dbContext.Database
            .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");

            var durableIntent = await _persistence.ReadAuthenticationIntentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fence.ReservedCandidateOperationId)
                && !string.Equals(fence.ReservedCandidateOperationId, operationId, StringComparison.Ordinal)
                && !string.Equals(durableIntent, operationId, StringComparison.Ordinal))
            {

                throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
            }

            var candidateRows = await _dbContext.ConfigItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var hasTokenCandidate = candidateRows
                .Any(x => string.Equals(x.ConfigName, SetupConfigKeys.CandidateSessionToken, StringComparison.OrdinalIgnoreCase));
            var hasOperationCandidate = candidateRows
                .Any(x => string.Equals(x.ConfigName, SetupConfigKeys.CandidateSessionOperation, StringComparison.OrdinalIgnoreCase));
            var hasIntentCandidate = candidateRows
                .Any(x => string.Equals(x.ConfigName, SetupConfigKeys.CandidateSessionIntent, StringComparison.OrdinalIgnoreCase));

            var hasCandidateRecovery = hasTokenCandidate || hasOperationCandidate || hasIntentCandidate;
            var candidateOperation = hasCandidateRecovery
                ? await _persistence.ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false)
                : null;
            var completionPending = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);

            var candidateRowConflict =
                ((hasTokenCandidate || hasOperationCandidate)
                    && !string.Equals(candidateOperation, operationId, StringComparison.Ordinal))
                || (hasIntentCandidate
                    && !string.Equals(candidateOperation, operationId, StringComparison.Ordinal)
                    && !string.Equals(durableIntent, operationId, StringComparison.Ordinal));

            if (completionPending || candidateRowConflict)
                throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");

            if (string.IsNullOrWhiteSpace(fence.ReservedCandidateOperationId)
                || string.Equals(fence.ReservedCandidateOperationId, operationId, StringComparison.Ordinal))
            {
                fence.ReservedCandidateOperationId = operationId;
            }

            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _persistence.SaveAuthenticationIntentUnderMutationGateAsync(
                    operationId,
                    transaction,
                    cancellationToken,
                    runLease)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private Task EnsureCandidateSlotAvailableAsync(string operationId, CancellationToken cancellationToken, SetupRunLeaseHandle? runLease = null)
        => ReserveAuthenticationIntentAsync(operationId, cancellationToken, runLease);

    private async Task EnsureNoRecoveryPendingInCurrentTransactionAsync(string operationId, CancellationToken cancellationToken)
    {
        // The candidate and emergency records are this operation's own
        // prepare journal and are checked for exact ownership immediately
        // below. Only unrelated durable recovery state is a blocker here.
        var candidateRows = await _dbContext.ConfigItems.AsNoTracking()
            .Where(item => item.ConfigName != null)
            .Select(item => item.ConfigName.ToLowerInvariant())
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var hasRevocationRow = candidateRows.Any(name =>
            name == SetupConfigKeys.RevocationPendingToken.ToLowerInvariant());
        var hasCandidateRow = candidateRows.Any(name =>
            name == SetupConfigKeys.CandidateSessionToken.ToLowerInvariant()
            || name == SetupConfigKeys.CandidateSessionOperation.ToLowerInvariant()
            || name == SetupConfigKeys.CandidateSessionIntent.ToLowerInvariant());

        var candidateOperation = hasCandidateRow
            ? await _persistence.ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var durableIntent = hasCandidateRow
            ? await _persistence.ReadAuthenticationIntentAsync(cancellationToken).ConfigureAwait(false)
            : null;

        if (await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken).ConfigureAwait(false)
            || hasRevocationRow
            || (hasCandidateRow
                && !string.Equals(candidateOperation, operationId, StringComparison.Ordinal)
                && !string.Equals(durableIntent, operationId, StringComparison.Ordinal))
            || await ReadRevocationPendingMarkerAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");
    }

    private async Task<bool> IsCompletionPendingUnderMutationGateAsync(CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            return await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    private async Task<(bool Live, bool Revoked)> ReadDurableGrantStateAsync(CancellationToken cancellationToken)
    {
        var row = await _dbContext.SetupGrants
            .AsNoTracking()
            .SingleOrDefaultAsync(grant => grant.Id == SetupGrant.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
            return (false, false);

        return (
            !row.IsRevoked
            && !string.IsNullOrWhiteSpace(row.GrantedTokenHash)
            && row.ExpiresAtUtc > _timeProvider.GetUtcNow().UtcDateTime,
            row.IsRevoked || string.IsNullOrWhiteSpace(row.GrantedTokenHash));
    }

    private async Task RevokeSessionsAsync(
        IEnumerable<string?> accessTokens,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        Exception? firstFailure = null;
        var seen = new List<string>();
        foreach (var accessToken in accessTokens)
        {
            if (string.IsNullOrWhiteSpace(accessToken)
                || seen.Any(existing => TokensEqual(existing, accessToken)))
                continue;

            seen.Add(accessToken);
            try
            {
                await RevokeJellyfinSessionAsync(accessToken, jellyfinUrl, cancellationToken, runLease)
                    .ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
                // A failed owner/generation assertion is terminal for this
                // compound logout. Continuing would let a stale runner issue
                // later POSTs after takeover.
                if (runLease is not null && exception is BadHttpRequestException)
                    break;
            }
        }

        if (firstFailure is not null)
            throw firstFailure;
    }

    private async Task RevokeJellyfinSessionAsync(
        string? accessToken,
        string jellyfinUrl,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease = null)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
            return;
        if (runLease is not null
            && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(UpstreamTokenRevokeTimeout);
        try
        {
            using var client = _httpClientFactory();
            client.BaseAddress = new Uri(jellyfinUrl);
            var setupClient = new JellyfinSetupClient(
                client,
                new JellyfinSetupOptions(jellyfinUrl, "setup", "setup")
                {
                    // Logout is itself a destructive upstream request. The
                    // outer fence protects the compound operation; this
                    // callback closes the takeover window immediately before
                    // and after the actual Jellyfin POST.
                    AssertMutationLeaseResultAsync = runLease is null
                        ? null
                        : async token => await runLease.AssertCurrentAsync(token).ConfigureAwait(false),
                });
            await ExecuteLeaseFencedLogoutAsync(
                    () => setupClient.RevokeSessionAsync(accessToken, timeout.Token),
                    runLease,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "session logout");
        }
        catch (HttpRequestException ex)
        {
            throw new JellyfinSetupException(JellyfinSetupFailure.UpstreamFailure, "session logout", inner: ex);
        }
    }

    private async Task ExecuteLeaseFencedLogoutAsync(
        Func<Task> logout,
        SetupRunLeaseHandle? runLease,
        CancellationToken cancellationToken)
    {
        if (runLease is null)
        {
            await logout().ConfigureAwait(false);
            return;
        }

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatDuringLogoutAsync(runLease, heartbeatStop.Token);
        Exception? failure = null;
        try
        {
            await logout().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            heartbeatStop.Cancel();
            try { await heartbeat.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (failure is null) { failure = exception; }

            try
            {
                if (!await runLease.AssertCurrentAsync(CancellationToken.None).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            }
            catch (Exception exception) when (failure is null) { failure = exception; }
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task HeartbeatDuringLogoutAsync(
        SetupRunLeaseHandle runLease,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _timeProvider, cancellationToken).ConfigureAwait(false);
            if (!await runLease.HeartbeatAsync(cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        }
    }

    private static bool TokensEqual(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right);

        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private void EnsureEnabledAndIncomplete(SetupEnvironmentOptions options)
    {
        if (!options.FullStackEnabled)
            throw new BadHttpRequestException(options.ValidationError ?? "Full-stack setup is disabled.");
    }

    private static string NormalizeUsername(string username)
        => username.Trim().ToLowerInvariant();

    private static async Task<T> ExecuteWithRetriesAsync<T>(
        Func<int, Task<T>> operation,
        CancellationToken cancellationToken,
        Action? onRetry = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation(attempt).ConfigureAwait(false);
            }
            catch (SetupRetryableTransactionException) when (attempt < MaxWriteAttempts)
            {
                onRetry?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed class TransactionLifetime : IAsyncDisposable
    {
        public TransactionLifetime(IDbContextTransaction transaction)
        {
            Transaction = transaction ?? throw new ArgumentNullException(nameof(transaction));
        }

        public IDbContextTransaction Transaction { get; }
        public SetupTransactionState State { get; private set; } = SetupTransactionState.Active;
        public bool CommitAttempted { get; private set; }
        public bool IsDisposed { get; private set; }

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            CommitAttempted = true;
            await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            State = SetupTransactionState.Committed;
        }

        public async Task<Exception?> TryRollbackAsync()
        {
            if (IsDisposed || State == SetupTransactionState.Committed || State == SetupTransactionState.RolledBack)
                return null;

            try
            {
                await Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                State = SetupTransactionState.RolledBack;
                return null;
            }
            catch (Exception exception)
            {
                State = SetupTransactionState.Unknown;
                return exception;
            }
        }

        public async Task<Exception?> TryDisposeAsync()
        {
            if (IsDisposed)
                return null;

            try
            {
                await Transaction.DisposeAsync().ConfigureAwait(false);
                IsDisposed = true;
                return null;
            }
            catch (Exception exception)
            {
                State = SetupTransactionState.Unknown;
                return exception;
            }
        }

        public async ValueTask DisposeAsync()
        {
            _ = await TryDisposeAsync().ConfigureAwait(false);
        }
    }

    private sealed record PersistedGrant(
        SetupGrantResult Result,
        string? PreviousAccessToken,
        bool CandidateCleanupPending);
}
