using System.Collections.Frozen;
using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Setup.Core;

internal sealed record SetupStatusPersistenceSnapshot(
    IReadOnlyDictionary<string, string?> Values,
    bool RecoveryPending,
    bool SetupRunBusy);

/// <summary>
/// The setup-only ConfigItems persistence boundary. Setup writes share
/// ConfigManager's process-wide mutation gate and use a serializable DB
/// transaction so a failed write cannot publish an uncommitted cache snapshot.
/// </summary>
public sealed class SetupConfigPersistence
{
    private const int MaxWriteAttempts = 4;
    private const int MaxLiveHealthProgressBytes = 16 * 1024;

    private static readonly string[] LiveHealthProgressSteps =
    {
        "provider-indexers",
        "arr-key-discovery",
        "prowlarr",
        "arr-clients",
        "jellyfin",
        "health-check",
    };

    private static readonly string[] PersistedKeys =
    {
        SetupConfigKeys.UsenetProviders,
        SetupConfigKeys.Indexers,
        SetupConfigKeys.JellyfinApiKey,
        SetupConfigKeys.PluginApiKey,
        SetupConfigKeys.PluginApiKeyPrevious,
        SetupConfigKeys.PluginApiKeyPreviousExpiresAt,
        SetupConfigKeys.RunProgress,
        SetupConfigKeys.RunProgressReasons,
        SetupConfigKeys.RepairRequired,
        SetupConfigKeys.RepairCheckedAtUtc,
        SetupConfigKeys.SonarrRepairReason,
        SetupConfigKeys.RadarrRepairReason,
        SetupConfigKeys.RepairReasons,
        SetupConfigKeys.RevocationPending,
        SetupConfigKeys.RevocationPendingToken,
        SetupConfigKeys.CandidateSessionToken,
        SetupConfigKeys.CandidateSessionOperation,
        SetupConfigKeys.CandidateSessionIntent,
        SetupConfigKeys.Completed,
    };

    public static IReadOnlySet<string> PersistedKeyNames { get; } = PersistedKeys.ToFrozenSet(StringComparer.Ordinal);


    private static readonly IReadOnlySet<string> PersistedKeySet =
        new HashSet<string>(PersistedKeys, StringComparer.Ordinal);

    private static readonly string[] RequiredCompletionKeys =
    {
        SetupConfigKeys.Indexers,
        SetupConfigKeys.PluginApiKey,
    };

    private readonly ConfigManager _configManager;
    private readonly DavDatabaseContext _dbContext;
    private readonly Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>> _beginTransaction;

    public SetupConfigPersistence(
        ConfigManager configManager,
        DavDatabaseContext dbContext,
        Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? beginTransaction = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
        _beginTransaction = beginTransaction ?? ((isolation, cancellationToken) =>
            _dbContext.Database.BeginTransactionAsync(isolation, cancellationToken));
    }

    /// <summary>
    /// Merges and persists setup values. Null secrets mean "leave the existing
    /// value alone", rather than delete it; ClearJellyfinApiKey is the explicit
    /// deletion operation for the temporary privileged session. The completion marker is written
    /// last and can never be changed from true back to false.
    /// </summary>
    /// <summary>
    /// Persists the administrator's provider document inside a caller-owned
    /// transaction. The caller must already hold ConfigManager's mutation gate;
    /// this deliberately shares the setup-owned encryption boundary instead of
    /// using the generic config writer.
    /// </summary>
    internal async Task<string> SaveUsenetProvidersInTransactionAsync(
        string providersJson,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providersJson);
        ArgumentNullException.ThrowIfNull(transaction);
        DetachTrackedSetupRows();
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);

        var rows = await _dbContext.ConfigItems
            .Where(row => row.ConfigName == SetupConfigKeys.UsenetProviders
                       || row.ConfigName.ToLower() == SetupConfigKeys.UsenetProviders)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var canonical = rows.SingleOrDefault(row => row.ConfigName == SetupConfigKeys.UsenetProviders);
        foreach (var alias in rows.Where(row => !ReferenceEquals(row, canonical)).ToList())
            _dbContext.ConfigItems.Remove(alias);

        var prepared = _configManager.PrepareSetupForStorage(new List<ConfigItem>
        {
            new() { ConfigName = SetupConfigKeys.UsenetProviders, ConfigValue = providersJson, IsEncrypted = false }
        }).Single();

        if (canonical is null)
        {
            canonical = new ConfigItem
            {
                ConfigName = SetupConfigKeys.UsenetProviders,
                ConfigValue = prepared.ConfigValue,
                IsEncrypted = prepared.IsEncrypted,
            };
            _dbContext.ConfigItems.Add(canonical);
        }
        else
        {
            canonical.ConfigValue = prepared.ConfigValue;
            canonical.IsEncrypted = prepared.IsEncrypted;
            _dbContext.ConfigItems.Update(canonical);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return canonical.ConfigValue;
    }

    public async Task SaveAsync(
        SetupSecretValues secrets,
        bool completed,
        CancellationToken cancellationToken = default)
    {
        await SaveAsync(secrets, completed, null, true, null, cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task SaveAsync(
        SetupSecretValues secrets,
        bool completed,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        return SaveAsync(secrets, completed, transaction, false, null, cancellationToken);
    }

    internal Task SaveAsync(
        SetupSecretValues secrets,
        bool completed,
        IDbContextTransaction transaction,
        Action<IReadOnlyList<ConfigItem>> onSetupValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSetupValues);
        return SaveAsync(secrets, completed, transaction, false, onSetupValues, cancellationToken);
    }

    private Task SaveAsync(
        SetupSecretValues secrets,
        bool requestedCompleted,
        IDbContextTransaction? existingTransaction,
        bool publishToCache,
        Action<IReadOnlyList<ConfigItem>>? onSetupValues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        // ConfigManager owns the shared process-wide gate. It remains held
        // through the transaction and optional cache publication. A caller
        // supplying a transaction already owns that gate (setup grant and
        // completion orchestration use this path to keep revocation and the
        // completion marker in one mutation domain).
        return existingTransaction is null
            ? _configManager.WithMutationGateAsync(
                () => ExecuteWithRetriesAsync(
                    _ => SaveAttemptAsync(
                        secrets,
                        requestedCompleted,
                        existingTransaction,
                        publishToCache,
                        onSetupValues,
                        cancellationToken),
                    cancellationToken,
                    onRetry: _dbContext.ChangeTracker.Clear),
                cancellationToken)
            : SaveAttemptAsync(
                secrets,
                requestedCompleted,
                existingTransaction,
                publishToCache,
                onSetupValues,
                cancellationToken);
    }

    /// <summary>
    /// Persists setup state when the caller already holds ConfigManager's
    /// process-wide mutation gate. This is used by completion so no renewal
    /// can enter between privileged-session cleanup and the completion marker.
    /// </summary>
    internal Task SaveLiveHealthStateAsync(
        bool ready,
        DateTime checkedAtUtc,
        string? sonarrReason,
        string? radarrReason,
        string? reasonsJson,
        CancellationToken cancellationToken = default)
        => SaveLiveHealthStateAsync(
            ready,
            checkedAtUtc,
            sonarrReason,
            radarrReason,
            reasonsJson,
            runProgressJson: null,
            cancellationToken);

    internal Task SaveLiveHealthStateAsync(
        bool ready,
        DateTime checkedAtUtc,
        string? sonarrReason,
        string? radarrReason,
        string? reasonsJson,
        string? runProgressJson,
        CancellationToken cancellationToken = default)
    {
        ValidateLiveHealthProgress(runProgressJson);
        return SaveAsync(new SetupSecretValues
        {
            RepairRequiredState = ready ? null : "true",
            RepairCheckedAtUtc = checkedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            SonarrRepairReason = sonarrReason,
            RadarrRepairReason = radarrReason,
            RunProgress = runProgressJson,
            RunProgressReasons = reasonsJson,
            ClearRepairRequired = ready,
            ClearRepairReasons = ready,
            ClearSonarrRepairReason = sonarrReason is null,
            ClearRadarrRepairReason = radarrReason is null,
        }, completed: false, cancellationToken);
    }

    internal Task SaveLiveHealthStateWithLeaseAsync(
        bool ready,
        DateTime checkedAtUtc,
        string? sonarrReason,
        string? radarrReason,
        string? reasonsJson,
        SetupRunLeaseHandle lease,
        CancellationToken cancellationToken = default)
        => SaveLiveHealthStateWithLeaseAsync(
            ready,
            checkedAtUtc,
            sonarrReason,
            radarrReason,
            reasonsJson,
            runProgressJson: null,
            lease,
            cancellationToken);

    /// <summary>
    /// Persists the complete live-health snapshot under one lease-fenced
    /// transaction. Run progress is deliberately a bounded, non-secret JSON
    /// document; keeping it in the same mutation as the latch and diagnostics
    /// prevents a restart from resurrecting a completed phase after drift.
    /// </summary>
    internal Task SaveLiveHealthStateWithLeaseAsync(
        bool ready,
        DateTime checkedAtUtc,
        string? sonarrReason,
        string? radarrReason,
        string? reasonsJson,
        string? runProgressJson,
        SetupRunLeaseHandle lease,
        CancellationToken cancellationToken = default)
    {
        ValidateLiveHealthProgress(runProgressJson);
        return SaveWithLeaseAsync(new SetupSecretValues
        {
            RepairRequiredState = ready ? null : "true",
            RepairCheckedAtUtc = checkedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            SonarrRepairReason = sonarrReason,
            RadarrRepairReason = radarrReason,
            RunProgress = runProgressJson,
            RunProgressReasons = reasonsJson,
            ClearRepairRequired = ready,
            ClearRepairReasons = ready,
            ClearSonarrRepairReason = sonarrReason is null,
            ClearRadarrRepairReason = radarrReason is null,
        }, completed: false, lease, cancellationToken);
    }

    private static void ValidateLiveHealthProgress(string? runProgressJson)
    {
        if (runProgressJson is null)
            return;
        if (string.IsNullOrWhiteSpace(runProgressJson))
            throw new ArgumentException("Live-health run progress is required when supplied.", nameof(runProgressJson));

        var bytes = Encoding.UTF8.GetByteCount(runProgressJson);
        if (bytes > MaxLiveHealthProgressBytes)
            throw new ArgumentException("Live-health run progress is too large.", nameof(runProgressJson));

        try
        {
            using var document = JsonDocument.Parse(runProgressJson, new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || document.RootElement.EnumerateObject().Count() != LiveHealthProgressSteps.Length)
                throw new ArgumentException("Live-health run progress must be a canonical JSON object.", nameof(runProgressJson));
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!LiveHealthProgressSteps.Contains(property.Name, StringComparer.Ordinal)
                    || property.Name.Length > 64 || property.Value.ValueKind != JsonValueKind.String)
                    throw new ArgumentException("Live-health run progress contains an invalid value.", nameof(runProgressJson));
                var state = property.Value.GetString();
                if (state is not ("pending" or "running" or "complete" or "warning" or "failed"))
                    throw new ArgumentException("Live-health run progress contains an invalid state.", nameof(runProgressJson));
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("Live-health run progress must be valid JSON.", nameof(runProgressJson), exception);
        }
    }

    internal Task SaveUnderMutationGateAsync(
        SetupSecretValues secrets,
        bool completed,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        return ExecuteWithRetriesAsync(
            _ => SaveAttemptAsync(
                secrets, completed, null, true, null, cancellationToken,
                runLease is null
                    ? null
                    : async (transaction, token) => await runLease.AssertCurrentWithinTransactionAsync(transaction, token).ConfigureAwait(false)),
            cancellationToken,
            onRetry: _dbContext.ChangeTracker.Clear);
    }

    internal Task SaveAuthenticationIntentUnderMutationGateAsync(
        string operationId,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        ArgumentNullException.ThrowIfNull(operationId);
        if (string.IsNullOrWhiteSpace(operationId))
            throw new ArgumentException("An authentication operation id is required.", nameof(operationId));

        return SaveAuthenticationIntentAttemptAsync(operationId, transaction, cancellationToken, runLease);
    }

    /// <summary>
    /// Persists setup state only when the supplied cross-process run lease is
    /// still current in the same fenced transaction. This closes the race
    /// between a standalone heartbeat and a progress/diagnostic write.
    /// </summary>
    internal Task SaveWithLeaseAsync(
        SetupSecretValues secrets,
        bool completed,
        SetupRunLeaseHandle lease,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        return _configManager.WithMutationGateAsync(
            () => ExecuteWithRetriesAsync(
                _ => SaveAttemptAsync(
                    secrets,
                    completed,
                    null,
                    true,
                    null,
                    cancellationToken,
                    async (transaction, token) =>
                        await lease.AssertCurrentWithinTransactionAsync(transaction, token).ConfigureAwait(false)),
                cancellationToken,
                onRetry: _dbContext.ChangeTracker.Clear),
            cancellationToken);
    }

    /// <summary>
    /// Writes only the candidate journal. Candidate persistence must not
    /// rewrite or publish the current setup snapshot: the journal is an
    /// internal crash-safety record, not a usable setup value.
    /// </summary>
    internal Task SaveCandidateUnderMutationGateAsync(
        string token,
        string operationId,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
        => ExecuteWithRetriesAsync(
            _ => SaveCandidateAttemptAsync(token, operationId, cancellationToken, runLease),
            cancellationToken,
            onRetry: _dbContext.ChangeTracker.Clear);

    private async Task SaveAuthenticationIntentAttemptAsync(
        string operationId,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease)
    {
        DetachTrackedSetupRows();
        if (runLease is not null
            && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");

        var intentRows = await _dbContext.ConfigItems
            .Where(row => row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionIntent)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (intentRows.Count > 1)
            throw new InvalidOperationException("Duplicate setup authentication intent rows detected.");

        var prepared = _configManager.PrepareSetupForStorage(new List<ConfigItem>
        {
            new() { ConfigName = SetupConfigKeys.CandidateSessionIntent, ConfigValue = operationId, IsEncrypted = false },
        }).Single();

        if (intentRows.Count == 0)
        {
            _dbContext.ConfigItems.Add(prepared);
        }
        else
        {
            var existing = intentRows[0];
            if (string.Equals(existing.ConfigName, prepared.ConfigName, StringComparison.Ordinal))
            {
                existing.ConfigValue = prepared.ConfigValue;
                existing.IsEncrypted = prepared.IsEncrypted;
            }
            else
            {
                _dbContext.ConfigItems.Remove(existing);
                _dbContext.ConfigItems.Add(prepared);
            }
        }

        if (_dbContext.ChangeTracker.HasChanges())
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a candidate only when both authenticated values still belong to
    /// the caller. The comparison and delete are one serializable transaction;
    /// a concurrent replacement is never removed by an older cleanup attempt.
    /// </summary>
    internal Task<bool> ClearCandidateUnderMutationGateAsync(
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
        => ExecuteWithRetriesAsync(
            _ => ClearCandidateAttemptAsync(expectedToken, expectedOperationId, cancellationToken, runLease),
            cancellationToken,
            onRetry: _dbContext.ChangeTracker.Clear);

    internal Task<CandidateJournalEntry?> ReadEmergencyCandidateAsync(
        CancellationToken cancellationToken = default)
        => SetupEmergencyCandidateJournal.ReadAsync(_configManager, cancellationToken);

    internal async Task<SetupStatusPersistenceSnapshot> ReadStatusSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            // This is deliberately a read-only transaction with no mutation
            // fence lock. All status dimensions are read from one database
            // snapshot, so a completion commit between individual reads cannot
            // produce a false "no pending" response.
            var rows = await _dbContext.ConfigItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                    || values.ContainsKey(canonical))
                    continue;
                values[canonical] = row.IsEncrypted
                    ? _configManager.DecryptSetupValue(canonical, row.ConfigValue).Plaintext
                    : row.ConfigValue;
            }

            var completion = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .AnyAsync(x => x.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var candidate = rows.Any(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                                            && (canonical == SetupConfigKeys.CandidateSessionToken
                                                || canonical == SetupConfigKeys.CandidateSessionOperation
                                                || canonical == SetupConfigKeys.CandidateSessionIntent
                                                || canonical == SetupConfigKeys.RevocationPendingToken));
            var revocation = string.Equals(values.GetValueOrDefault(SetupConfigKeys.RevocationPending), "true", StringComparison.OrdinalIgnoreCase);
            var reservation = await _dbContext.SetupMutationFences.AsNoTracking()
                .AnyAsync(x => x.Id == SetupMutationFence.SingletonId && x.ReservedCandidateOperationId != null, cancellationToken)
                .ConfigureAwait(false);
            var repair = await _dbContext.SetupGrants.AsNoTracking()
                .AnyAsync(x => x.Id == SetupGrant.SingletonId
                               && x.Purpose == SetupGrantConstants.RepairPurpose
                               && !string.IsNullOrWhiteSpace(x.RepairSessionCiphertext), cancellationToken)
                .ConfigureAwait(false);
            var setupRunBusy = await _dbContext.SetupRunLeases.AsNoTracking()
                .AnyAsync(x => x.LeaseUntilUtc > DateTime.UtcNow
                               && !x.Purpose.StartsWith("grant-"), cancellationToken)
                .ConfigureAwait(false);
            return new SetupStatusPersistenceSnapshot(values, completion || candidate || revocation || reservation || repair, setupRunBusy);
        }
        finally
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
        }
    }

    internal async Task SaveEmergencyCandidateUnderMutationGateAsync(
        string token,
        string operationId,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        await AssertLeaseAsync(runLease, cancellationToken).ConfigureAwait(false);
        await SetupEmergencyCandidateJournal.WriteAsync(_configManager, token, operationId, cancellationToken)
            .ConfigureAwait(false);
        // The journal is a durable handoff boundary. Do not let an owner that
        // was taken over while writing publish a candidate that the new owner
        // did not create.
        await AssertLeaseAsync(runLease, CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task<bool> TrySaveEmergencyCandidateIfEmptyUnderMutationGateAsync(
        string token,
        string operationId,
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        await AssertLeaseAsync(runLease, cancellationToken).ConfigureAwait(false);
        var written = await SetupEmergencyCandidateJournal.WriteIfEmptyAsync(
                _configManager, token, operationId, allowReplacement: false, cancellationToken)
            .ConfigureAwait(false);
        await AssertLeaseAsync(runLease, CancellationToken.None).ConfigureAwait(false);
        return written;
    }

    private static async Task AssertLeaseAsync(
        SetupRunLeaseHandle? runLease,
        CancellationToken cancellationToken)
    {
        if (runLease is not null
            && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
    }

    internal async Task<bool> ClearEmergencyCandidateUnderMutationGateAsync(
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken = default,
        bool protectCompletionOwnership = true,
        SetupRunLeaseHandle? runLease = null,
        IDbContextTransaction? ownerTransaction = null)
    {
        if (runLease is not null
            && !(ownerTransaction is not null
                ? await runLease.AssertCurrentWithinTransactionAsync(ownerTransaction, cancellationToken).ConfigureAwait(false)
                : await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false)))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        if (protectCompletionOwnership
            && await CompletionOwnsEmergencyCandidateAsync(expectedToken, expectedOperationId, cancellationToken)
                .ConfigureAwait(false))
            return false;

        if (runLease is not null
            && !(ownerTransaction is not null
                ? await runLease.AssertCurrentWithinTransactionAsync(ownerTransaction, cancellationToken).ConfigureAwait(false)
                : await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false)))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        var cleared = await SetupEmergencyCandidateJournal.ClearIfMatchesAsync(
                _configManager, expectedToken, expectedOperationId, cancellationToken)
            .ConfigureAwait(false);
        if (runLease is not null
            && !(ownerTransaction is not null
                ? await runLease.AssertCurrentWithinTransactionAsync(ownerTransaction, CancellationToken.None).ConfigureAwait(false)
                : await runLease.AssertCurrentAsync(CancellationToken.None).ConfigureAwait(false)))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        return cleared;
    }

    private async Task<bool> CompletionOwnsEmergencyCandidateAsync(
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            // Completion owns only the immutable emergency identity captured by
            // phase A. A later emergency candidate is deliberately foreign and
            // must be logged out by the failed issuer/restart recovery.
            return operation is not null
                   && TokensEqual(expectedToken, DecryptSnapshot("emergency", operation.EmergencySessionCiphertext))
                   && TokensEqual(expectedOperationId, DecryptSnapshot("emergency-operation", operation.EmergencyOperationCiphertext));
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Imports the secondary journal before ordinary candidate state is read.
    /// A different primary candidate is deliberately not overwritten: both
    /// values remain recoverable and the recovery state machine reconciles them.
    /// </summary>
    internal async Task ImportEmergencyCandidateUnderMutationGateAsync(
        CancellationToken cancellationToken = default,
        SetupRunLeaseHandle? runLease = null)
    {
        if (runLease is not null
            && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        var emergency = await ReadEmergencyCandidateAsync(cancellationToken).ConfigureAwait(false);
        if (emergency is null)
            return;

        var candidate = await ReadCandidateSessionTokenAsync(cancellationToken).ConfigureAwait(false);
        var operation = await ReadCandidateSessionOperationAsync(cancellationToken).ConfigureAwait(false);
        if (TokensEqual(candidate, emergency.Token)
            && TokensEqual(operation, emergency.OperationId))
        {
            _ = await ClearEmergencyCandidateUnderMutationGateAsync(
                    emergency.Token, emergency.OperationId, cancellationToken,
                    runLease: runLease)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentAsync(CancellationToken.None).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            return;
        }

        if (candidate is null && operation is null)
        {
            await SaveCandidateUnderMutationGateAsync(
                    emergency.Token, emergency.OperationId, cancellationToken, runLease)
                .ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            _ = await ClearEmergencyCandidateUnderMutationGateAsync(
                    emergency.Token, emergency.OperationId, cancellationToken,
                    runLease: runLease)
                .ConfigureAwait(false);
        }
    }

    private async Task SaveCandidateAttemptAsync(
        string token,
        string operationId,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease)
    {
        DetachTrackedSetupRows();
        var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        var fence = await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new SetupPersistenceTransaction(transaction);
        if (runLease is not null
            && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            _ = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            _ = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        }
        Exception? failure = null;
        Exception? rollbackFailure = null;
        try
        {
            var completion = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            if (completion is not null)
                throw new BadHttpRequestException("Setup completion cleanup is pending; retry completion recovery.");

            var durableIntent = await ReadAuthenticationIntentAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(fence.ReservedCandidateOperationId)
                && !string.Equals(fence.ReservedCandidateOperationId, operationId, StringComparison.Ordinal)
                && !string.Equals(durableIntent, operationId, StringComparison.Ordinal))
                throw new BadHttpRequestException("Setup session recovery is pending; recover cleanup before continuing.");

            var rows = await _dbContext.ConfigItems
                .Where(row => row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionToken
                           || row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionOperation)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            var values = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [SetupConfigKeys.CandidateSessionToken] = token,
                [SetupConfigKeys.CandidateSessionOperation] = operationId,
            };
            foreach (var value in values)
            {
                var matches = rows.Where(row => string.Equals(row.ConfigName, value.Key, StringComparison.OrdinalIgnoreCase)).ToList();
                if (matches.Count > 1)
                    throw new InvalidOperationException($"Duplicate setup key '{value.Key}' exists with multiple casings.");

                var prepared = _configManager.PrepareSetupForStorage(new List<ConfigItem>
                {
                    new() { ConfigName = value.Key, ConfigValue = value.Value, IsEncrypted = false },
                }).Single();
                if (matches.Count == 0)
                    _dbContext.ConfigItems.Add(prepared);
                else if (string.Equals(matches[0].ConfigName, value.Key, StringComparison.Ordinal))
                {
                    matches[0].ConfigValue = prepared.ConfigValue;
                    matches[0].IsEncrypted = prepared.IsEncrypted;
                }
                else
                {
                    _dbContext.ConfigItems.Remove(matches[0]);
                    _dbContext.ConfigItems.Add(prepared);
                }
            }

            // Keep the pre-auth reservation until the caller has reconciled
            // any possibly-created external session. The candidate operation
            // is the deterministic recovery identity during that interval;
            // promotion/recovery clears the reservation with its own CAS.
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!lifetime.CommitAttempted)
                rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            DetachTrackedSetupRows();
        }

        var commitOutcome = failure is not null && lifetime.CommitAttempted
            ? SetupTransactionOutcome.ClassifyCommitFailure(failure, lifetime.State)
            : SetupCommitOutcome.DefinitivelyAborted;
        if (failure is not null && commitOutcome == SetupCommitOutcome.Uncertain)
        {
            // A candidate commit is a handoff boundary. Even when a provider
            // throws after applying the commit, the candidate must remain
            // recoverable and no external logout/HTTP compensation may run.
            rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
        }

        var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        if (failure is not null)
        {
            var outcome = commitOutcome;
            if (commitOutcome == SetupCommitOutcome.Uncertain)
                throw new SetupTransactionRecoveryRequiredException(failure, rollbackFailure, disposeFailure);
            if (rollbackFailure is not null || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(failure, rollbackFailure, disposeFailure);
            if (outcome == SetupCommitOutcome.DefinitivelyAborted
                && SetupTransactionOutcome.IsRetryableWriteAbort(failure))
                throw new SetupRetryableTransactionException(failure);
            throw failure;
        }

        if (disposeFailure is not null)
            throw new SetupTransactionRecoveryRequiredException(
                disposeFailure, rollbackFailure: null, disposeFailure: disposeFailure);
    }

    private async Task<bool> ClearCandidateAttemptAsync(
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken,
        SetupRunLeaseHandle? runLease)
    {
        DetachTrackedSetupRows();
        var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
            .ConfigureAwait(false);
        var lifetime = new SetupPersistenceTransaction(transaction);
        if (runLease is not null
            && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
        {
            _ = await lifetime.TryRollbackAsync().ConfigureAwait(false);
            _ = await lifetime.TryDisposeAsync().ConfigureAwait(false);
            throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        }
        Exception? failure = null;
        Exception? rollbackFailure = null;
        var removed = false;
        try
        {
            var completion = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            var completionOwnsExactCandidate = completion is not null
                                              && ((TokensEqual(expectedToken, DecryptSnapshot("candidate", completion.CandidateSessionCiphertext))
                                                   && TokensEqual(expectedOperationId, DecryptSnapshot("candidate-operation", completion.CandidateOperationCiphertext)))
                                                  || (TokensEqual(expectedToken, DecryptSnapshot("emergency", completion.EmergencySessionCiphertext))
                                                      && TokensEqual(expectedOperationId, DecryptSnapshot("emergency-operation", completion.EmergencyOperationCiphertext))));
            if (!completionOwnsExactCandidate)
            {
                var rows = await _dbContext.ConfigItems
                    .Where(row => row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionToken
                               || row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionOperation)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var tokenRows = rows.Where(row => string.Equals(
                        row.ConfigName, SetupConfigKeys.CandidateSessionToken, StringComparison.OrdinalIgnoreCase)).ToList();
                var operationRows = rows.Where(row => string.Equals(
                        row.ConfigName, SetupConfigKeys.CandidateSessionOperation, StringComparison.OrdinalIgnoreCase)).ToList();
                if (tokenRows.Count > 1 || operationRows.Count > 1)
                    throw new InvalidOperationException("Duplicate setup candidate rows detected.");

                removed = tokenRows.Count == 1
                          && operationRows.Count == 1
                          && TokensEqual(expectedToken, DecryptCandidateRow(tokenRows[0]))
                          && TokensEqual(expectedOperationId, DecryptCandidateRow(operationRows[0]));
                if (removed)
                {
                    _dbContext.ConfigItems.Remove(tokenRows[0]);
                    _dbContext.ConfigItems.Remove(operationRows[0]);
                    await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            // A completion operation protects only its exact immutable
            // candidate identity. A late issuer is not allowed to delete a
            // newer candidate, but an exact plan-owned clear is harmless.
            if (runLease is not null
                && !await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await lifetime.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
            if (!lifetime.CommitAttempted)
                rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
        }
        finally
        {
            DetachTrackedSetupRows();
        }

        var clearOutcome = failure is not null && lifetime.CommitAttempted
            ? SetupTransactionOutcome.ClassifyCommitFailure(failure, lifetime.State)
            : SetupCommitOutcome.DefinitivelyAborted;
        if (failure is not null && clearOutcome == SetupCommitOutcome.Uncertain)
            rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
        var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
        _dbContext.ChangeTracker.Clear();
        if (failure is not null)
        {
            if (clearOutcome == SetupCommitOutcome.Uncertain
                || rollbackFailure is not null || disposeFailure is not null)
                throw new SetupTransactionRecoveryRequiredException(failure, rollbackFailure, disposeFailure);

            var outcome = clearOutcome;
            if (outcome == SetupCommitOutcome.DefinitivelyAborted
                && SetupTransactionOutcome.IsRetryableWriteAbort(failure))
                throw new SetupRetryableTransactionException(failure);
            throw failure;
        }

        if (disposeFailure is not null)
            throw new SetupTransactionRecoveryRequiredException(
                disposeFailure, rollbackFailure: null, disposeFailure: disposeFailure);
        return removed;
    }

    private string DecryptCandidateRow(ConfigItem row)
    {
        if (!row.IsEncrypted || !ConfigEncryptionService.IsEncryptedFormat(row.ConfigValue))
            throw new InvalidOperationException("Setup candidate rows must contain authenticated ciphertext.");
        return _configManager.DecryptSetupValue(
            SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                ? canonical
                : row.ConfigName,
            row.ConfigValue).Plaintext;
    }

    private static bool TokensEqual(string? left, string? right)
    {
        if (left is null || right is null)
            return left is null && right is null;
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    private async Task SaveAttemptAsync(
        SetupSecretValues secrets,
        bool requestedCompleted,
        IDbContextTransaction? existingTransaction,
        bool publishToCache,
        Action<IReadOnlyList<ConfigItem>>? onSetupValues,
        CancellationToken cancellationToken,
        Func<IDbContextTransaction, CancellationToken, Task<bool>>? transactionGuard = null)
    {
        // A long-lived context can still contain setup rows read earlier in this
        // service. Never let them participate in a new save attempt: the
        // transaction must read from the latest committed snapshot.
        DetachTrackedSetupRows();

        Dictionary<string, ExistingSetupRow>? existingRowsForCleanup = null;
        List<ConfigItem>? plaintextRows = null;
        try
        {
            var ownsTransaction = existingTransaction is null;
            var ownedTransaction = ownsTransaction
                ? await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
                    .ConfigureAwait(false)
                : null;
            var transaction = existingTransaction ?? ownedTransaction;
            if (transaction is null)
                throw new InvalidOperationException("Setup persistence transaction was not created.");
            var lifetime = ownsTransaction ? new SetupPersistenceTransaction(transaction) : null;
            await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken)
                .ConfigureAwait(false);
            var commitAttempted = false;
            var transactionState = SetupTransactionState.Active;

            try
            {
                if (transactionGuard is not null
                    && !await transactionGuard(transaction, cancellationToken).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");

                var allRows = await _dbContext.ConfigItems
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var managedRows = allRows
                    .Where(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                                  && PersistedKeySet.Contains(canonical))
                    .ToList();

                var existingRows = ReadExistingRows(managedRows);
                existingRowsForCleanup = existingRows;

                // A slower stale status request must not overwrite a newer
                // live result from another service/context. The fence and this
                // timestamp comparison make health persistence last-writer
                // ordered without using the process-local setup run gate.
                if (ownsTransaction
                    && DateTime.TryParse(
                        secrets.RepairCheckedAtUtc,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var requestedHealthCheck)
                    && existingRows.TryGetValue(SetupConfigKeys.RepairCheckedAtUtc, out var existingHealthCheck)
                    && DateTime.TryParse(
                        existingHealthCheck.Plaintext,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out var persistedHealthCheck)
                    && persistedHealthCheck > requestedHealthCheck)
                {
                    await lifetime!.CommitAsync(cancellationToken).ConfigureAwait(false);
                    var staleDisposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                    if (staleDisposeFailure is not null)
                        throw new SetupTransactionRecoveryRequiredException(staleDisposeFailure, null, staleDisposeFailure);
                    return;
                }

                var supplied = ReadSuppliedSecrets(secrets);
                var existingCompleted = existingRows.TryGetValue(SetupConfigKeys.Completed, out var marker)
                                        && ParseCompletionMarker(marker.Row);
                var shouldPersistSetupValues = !existingCompleted;
                var finalCompleted = existingCompleted || requestedCompleted;
                var merged = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var key in PersistedKeys)
                {
                    if (key == SetupConfigKeys.Completed)
                        continue;

                    if ((key == SetupConfigKeys.JellyfinApiKey
                         && (secrets.ClearJellyfinApiKey || finalCompleted))
                        || ((key == SetupConfigKeys.CandidateSessionToken
                             || key == SetupConfigKeys.CandidateSessionOperation
                             || key == SetupConfigKeys.CandidateSessionIntent)
                            && (secrets.ClearCandidateSession || finalCompleted)))
                        continue;

                    if ((key == SetupConfigKeys.RevocationPending
                         && (secrets.ClearRevocationPending || finalCompleted))
                        || (key == SetupConfigKeys.RevocationPendingToken
                            && (secrets.ClearRevocationPendingToken || secrets.ClearRevocationPending || finalCompleted))
                        || (key == SetupConfigKeys.RepairRequired && secrets.ClearRepairRequired)
                        || (key == SetupConfigKeys.SonarrRepairReason
                            && (secrets.ClearRepairReasons || secrets.ClearSonarrRepairReason))
                        || (key == SetupConfigKeys.RadarrRepairReason
                            && (secrets.ClearRepairReasons || secrets.ClearRadarrRepairReason))
                        || (key == SetupConfigKeys.RepairReasons && secrets.ClearRepairReasons))
                        continue;

                    if (supplied.TryGetValue(key, out var suppliedValue))
                    {
                        merged[key] = suppliedValue;
                    }
                    else if (existingRows.TryGetValue(key, out var existing))
                    {
                        merged[key] = existing.Plaintext;
                    }
                }

                if (finalCompleted && !existingCompleted)
                    ValidateRequiredSecrets(merged);

                // A completed installation may still need durable live-health
                // diagnostics. Setup secrets remain immutable after completion,
                // but repair rows are deliberately writable on every status
                // check so stale state survives restart and retry.
                var valuesToPersist = shouldPersistSetupValues
                    ? merged
                    : merged.Where(entry => IsRepairDiagnosticKey(entry.Key))
                        .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
                var generatedRows = CreatePlaintextRows(valuesToPersist, finalCompleted);
                var preparedRows = _configManager.PrepareSetupForStorage(CloneRows(generatedRows));

                if ((secrets.ClearJellyfinApiKey || finalCompleted)
                    && existingRows.TryGetValue(SetupConfigKeys.JellyfinApiKey, out var jellyfinRowToRemove))
                {
                    _dbContext.ConfigItems.Remove(jellyfinRowToRemove.Row);
                    existingRows.Remove(SetupConfigKeys.JellyfinApiKey);
                }

                if (secrets.ClearCandidateSession || finalCompleted)
                {
                    foreach (var candidateKey in new[]
                             { SetupConfigKeys.CandidateSessionToken, SetupConfigKeys.CandidateSessionOperation, SetupConfigKeys.CandidateSessionIntent })
                    {
                        if (existingRows.TryGetValue(candidateKey, out var candidateRowToRemove))
                        {
                            _dbContext.ConfigItems.Remove(candidateRowToRemove.Row);
                            existingRows.Remove(candidateKey);
                        }
                    }
                }

                if (secrets.ClearRepairRequired
                    && existingRows.TryGetValue(SetupConfigKeys.RepairRequired, out var repairRowToRemove))
                {
                    _dbContext.ConfigItems.Remove(repairRowToRemove.Row);
                    existingRows.Remove(SetupConfigKeys.RepairRequired);
                }

                if (secrets.ClearRepairReasons
                    || secrets.ClearSonarrRepairReason
                    || secrets.ClearRadarrRepairReason)
                {
                    var repairKeys = new List<string>();
                    if (secrets.ClearRepairReasons || secrets.ClearSonarrRepairReason)
                        repairKeys.Add(SetupConfigKeys.SonarrRepairReason);
                    if (secrets.ClearRepairReasons || secrets.ClearRadarrRepairReason)
                        repairKeys.Add(SetupConfigKeys.RadarrRepairReason);
                    if (secrets.ClearRepairReasons)
                        repairKeys.Add(SetupConfigKeys.RepairReasons);
                    foreach (var repairKey in repairKeys)
                    {
                        if (existingRows.TryGetValue(repairKey, out var repairReasonRow))
                        {
                            _dbContext.ConfigItems.Remove(repairReasonRow.Row);
                            existingRows.Remove(repairKey);
                        }
                    }
                }

                if ((secrets.ClearRevocationPending || finalCompleted)
                    && existingRows.TryGetValue(SetupConfigKeys.RevocationPending, out var pendingRowToRemove))
                {
                    _dbContext.ConfigItems.Remove(pendingRowToRemove.Row);
                    existingRows.Remove(SetupConfigKeys.RevocationPending);
                }

                if ((secrets.ClearRevocationPendingToken || secrets.ClearRevocationPending || finalCompleted)
                    && existingRows.TryGetValue(SetupConfigKeys.RevocationPendingToken, out var pendingTokenRowToRemove))
                {
                    _dbContext.ConfigItems.Remove(pendingTokenRowToRemove.Row);
                    existingRows.Remove(SetupConfigKeys.RevocationPendingToken);
                }

                // Canonicalize any legacy-cased rows before upserting the new
                // primary row names.
                var canonicalizedRows = new Dictionary<string, ExistingSetupRow>(StringComparer.Ordinal);
                foreach (var entry in existingRows
                             .Where(entry => !string.Equals(entry.Value.Row.ConfigName,
                                 entry.Key, StringComparison.Ordinal))
                             .ToList())
                {
                    _dbContext.ConfigItems.Remove(entry.Value.Row);
                    existingRows.Remove(entry.Key);
                    canonicalizedRows[entry.Key] = entry.Value;
                }

                if (_dbContext.ChangeTracker.HasChanges())
                {
                    await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    if (transactionGuard is not null
                        && !await transactionGuard(transaction, cancellationToken).ConfigureAwait(false))
                        throw new BadHttpRequestException("Another setup worker owns the active setup run.");
                }

                foreach (var prepared in preparedRows.Where(row => row.ConfigName != SetupConfigKeys.Completed))
                {
                    existingRows.TryGetValue(prepared.ConfigName, out var current);
                    canonicalizedRows.TryGetValue(prepared.ConfigName, out var legacy);
                    var source = current ?? legacy;
                    var destination = source?.Row;
                    var requiresRename = source is not null
                                       && !string.Equals(source.Row.ConfigName, prepared.ConfigName, StringComparison.Ordinal);

                    if (destination is null || requiresRename)
                    {
                        var replacement = CloneRow(prepared);
                        if (source is not null)
                        {
                            var replacementMergedValue = GetMergedValueOrCurrent(prepared.ConfigName, merged, replacement.ConfigValue);
                            replacement.ConfigValue = IsNonSecretDiagnosticKey(prepared.ConfigName)
                                ? replacementMergedValue
                                : PreserveAuthenticatedCiphertext(
                                    replacement.ConfigValue,
                                    source,
                                    replacementMergedValue);
                        }

                        _dbContext.ConfigItems.Add(replacement);
                        continue;
                    }

                    var mergedValue = GetMergedValueOrCurrent(prepared.ConfigName, merged, prepared.ConfigValue);
                    var authenticatedSource = source
                        ?? throw new InvalidOperationException("A setup destination row has no authenticated source.");
                    destination.ConfigValue = IsNonSecretDiagnosticKey(prepared.ConfigName)
                        ? mergedValue
                        : PreserveAuthenticatedCiphertext(
                            prepared.ConfigValue,
                            authenticatedSource,
                            mergedValue);
                    destination.IsEncrypted = IsNonSecretDiagnosticKey(prepared.ConfigName)
                        ? false
                        : prepared.IsEncrypted;
                }

                // Secret rows must flush before marker state. The marker is
                // written last so the DB never advertises setup completion while
                // secrets are only partially flushed.
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transactionGuard is not null
                    && !await transactionGuard(transaction, cancellationToken).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");

                var preparedMarker = preparedRows.Single(row => row.ConfigName == SetupConfigKeys.Completed);
                if (existingRows.TryGetValue(SetupConfigKeys.Completed, out var existingMarker))
                {
                    existingMarker.Row.ConfigValue = finalCompleted ? "true" : "false";
                    existingMarker.Row.IsEncrypted = false;
                    _dbContext.ConfigItems.Update(existingMarker.Row);
                }
                else
                {
                    _dbContext.ConfigItems.Add(CloneRow(preparedMarker));
                }

                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (transactionGuard is not null
                    && !await transactionGuard(transaction, cancellationToken).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");
                if (ownsTransaction)
                {
                    commitAttempted = true;
                    await lifetime!.CommitAsync(cancellationToken).ConfigureAwait(false);
                    transactionState = SetupTransactionState.Committed;
                }

                plaintextRows = generatedRows;
                var cacheRows = CloneRows(generatedRows)
                    .Where(row => !IsCandidateKey(row.ConfigName))
                    .ToList();
                if (secrets.ClearJellyfinApiKey || finalCompleted)
                {
                    // An empty cache value is intentionally not a credential;
                    // it makes stale in-process snapshots unable to renew/use
                    // the privileged session after its durable row is removed.
                    cacheRows.Add(new ConfigItem
                    {
                        ConfigName = SetupConfigKeys.JellyfinApiKey,
                        ConfigValue = string.Empty,
                        IsEncrypted = false,
                    });
                }

                if (secrets.ClearRevocationPending || finalCompleted)
                {
                    cacheRows.Add(new ConfigItem
                    {
                        ConfigName = SetupConfigKeys.RevocationPending,
                        ConfigValue = string.Empty,
                        IsEncrypted = false,
                    });
                }

                if (secrets.ClearRevocationPendingToken || secrets.ClearRevocationPending || finalCompleted)
                {
                    cacheRows.Add(new ConfigItem
                    {
                        ConfigName = SetupConfigKeys.RevocationPendingToken,
                        ConfigValue = string.Empty,
                        IsEncrypted = false,
                    });
                }

                if (secrets.ClearCandidateSession || finalCompleted)
                {
                    cacheRows.Add(new ConfigItem { ConfigName = SetupConfigKeys.CandidateSessionToken, ConfigValue = string.Empty, IsEncrypted = false });
                    cacheRows.Add(new ConfigItem { ConfigName = SetupConfigKeys.CandidateSessionOperation, ConfigValue = string.Empty, IsEncrypted = false });
                    cacheRows.Add(new ConfigItem { ConfigName = SetupConfigKeys.CandidateSessionIntent, ConfigValue = string.Empty, IsEncrypted = false });
                }

                if (publishToCache)
                    _configManager.ApplySetupValuesNoLock(cacheRows);
                else if (onSetupValues is not null)
                    onSetupValues(cacheRows);
            }
            catch (Exception exception)
            {
                if (ownsTransaction)
                {
                    var stateAtFailure = transactionState;
                    var outcome = commitAttempted
                        ? SetupTransactionOutcome.ClassifyCommitFailure(exception, stateAtFailure)
                        : SetupCommitOutcome.DefinitivelyAborted;
                    var rollbackFailure = await lifetime!.TryRollbackAsync().ConfigureAwait(false);
                    var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                    if (outcome == SetupCommitOutcome.Uncertain
                        || rollbackFailure is not null
                        || disposeFailure is not null)
                        throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
                    if (outcome == SetupCommitOutcome.DefinitivelyAborted
                        && SetupTransactionOutcome.IsRetryableWriteAbort(exception))
                        throw new SetupRetryableTransactionException(exception);
                }

                throw;
            }

            if (ownsTransaction)
            {
                var disposeFailure = await lifetime!.TryDisposeAsync().ConfigureAwait(false);
                if (disposeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(disposeFailure, null, disposeFailure);
            }
        }
        finally
        {
            if (existingRowsForCleanup is not null)
            {
                foreach (var row in existingRowsForCleanup.Values)
                    row.ClearPlaintext();
            }

            if (plaintextRows is not null)
            {
                foreach (var row in plaintextRows)
                    row.ConfigValue = string.Empty;
            }

            DetachTrackedSetupRows();
        }
    }


    /// <summary>
    /// Reads the current durable privileged-session token rather than relying
    /// on a possibly stale ConfigManager snapshot. The plaintext is returned
    /// only to the caller's bounded logout operation.
    /// </summary>
    internal Task<string?> ReadJellyfinApiKeyAsync(CancellationToken cancellationToken)
        => ReadSecretAsync(SetupConfigKeys.JellyfinApiKey, "Jellyfin session", cancellationToken);

    /// <summary>
    /// Reads the previous Jellyfin session retained by grant rotation. It is
    /// separate from the current session so recovery can never log out the
    /// newly issued, still-usable session.
    /// </summary>
    internal Task<string?> ReadRevocationPendingTokenAsync(CancellationToken cancellationToken)
        => ReadSecretAsync(SetupConfigKeys.RevocationPendingToken, "revocation pending session", cancellationToken);

    internal Task<string?> ReadCandidateSessionTokenAsync(CancellationToken cancellationToken)
        => ReadSecretAsync(SetupConfigKeys.CandidateSessionToken, "candidate session", cancellationToken);

    internal Task<string?> ReadCandidateSessionOperationAsync(CancellationToken cancellationToken)
        => ReadSecretAsync(SetupConfigKeys.CandidateSessionOperation, "candidate session operation", cancellationToken);

    internal Task<string?> ReadAuthenticationIntentAsync(CancellationToken cancellationToken)
        => ReadSecretAsync(SetupConfigKeys.CandidateSessionIntent, "authentication intent", cancellationToken);

    /// <summary>
    /// Transfers the exact recovery rows into the completion operation's
    /// encrypted snapshot while the caller holds the database fence. No
    /// cleanup path can observe a half-transfer: rows are removed by this
    /// transaction, not by phase C.
    /// </summary>
    internal async Task TransferRecoveryStateToCompletionAsync(
        SetupCompletionPlan plan,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(transaction);

        var rows = await _dbContext.ConfigItems
            .Where(row => row.ConfigName.ToLower() == SetupConfigKeys.JellyfinApiKey
                       || row.ConfigName.ToLower() == SetupConfigKeys.RevocationPending
                       || row.ConfigName.ToLower() == SetupConfigKeys.RevocationPendingToken
                       || row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionToken
                       || row.ConfigName.ToLower() == SetupConfigKeys.CandidateSessionOperation)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        RemoveExact(SetupConfigKeys.JellyfinApiKey, plan.ActiveSession, rows);
        RemoveExact(SetupConfigKeys.RevocationPendingToken, plan.RevocationSession, rows);
        RemoveExact(SetupConfigKeys.CandidateSessionToken, plan.CandidateSession, rows);
        RemoveExact(SetupConfigKeys.CandidateSessionOperation, plan.CandidateOperation, rows);

        // The marker has no bearer value after phase A. Its boolean meaning is
        // already immutable in the operation row, so remove the exact marker
        // (including an explicit false row) as part of the handoff.
        var markerRows = rows.Where(row => string.Equals(
                     row.ConfigName, SetupConfigKeys.RevocationPending,
                     StringComparison.OrdinalIgnoreCase)).ToList();
        if (markerRows.Count > 1)
            throw new InvalidOperationException("Duplicate setup revocation marker rows detected.");
        if (markerRows.Count == 1)
        {
            var markerValue = DecryptCandidateOrSetupRow(markerRows[0], SetupConfigKeys.RevocationPending);
            var markerPending = !string.Equals(markerValue, "false", StringComparison.OrdinalIgnoreCase)
                                && !string.IsNullOrWhiteSpace(markerValue);
            if (markerPending != plan.RevocationPending)
                throw new InvalidOperationException("Completion snapshot for revocation state is no longer current.");
            _dbContext.ConfigItems.Remove(markerRows[0]);
        }
        else if (plan.RevocationPending)
        {
            throw new InvalidOperationException("Completion snapshot for revocation state is no longer current.");
        }

        void RemoveExact(string key, string? expected, IReadOnlyList<ConfigItem> source)
        {
            var matches = source.Where(row => string.Equals(row.ConfigName, key, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException($"Duplicate setup key '{key}' exists with multiple casings.");
            if (matches.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(expected))
                    throw new InvalidOperationException($"Completion snapshot for '{key}' is no longer current.");
                return;
            }

            string? actual = DecryptCandidateOrSetupRow(matches[0], key);
            if (string.IsNullOrWhiteSpace(actual))
                actual = null;
            if (!TokensEqual(actual, expected))
                throw new InvalidOperationException($"Completion snapshot for '{key}' is no longer current.");
            _dbContext.ConfigItems.Remove(matches[0]);
        }
    }

    private string DecryptCandidateOrSetupRow(ConfigItem row, string key)
    {
        if (!row.IsEncrypted)
            return row.ConfigValue;
        if (!ConfigEncryptionService.IsEncryptedFormat(row.ConfigValue))
            throw new InvalidOperationException($"Encrypted setup key '{key}' does not contain valid ciphertext.");
        return _configManager.DecryptSetupValue(key, row.ConfigValue).Plaintext;
    }

    internal async Task<bool> CompletionOwnsCandidateAsync(
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
            .ConfigureAwait(false);
        await SetupMutationFenceLock.AcquireAsync(_dbContext, transaction, cancellationToken).ConfigureAwait(false);
        try
        {
            var operation = await _dbContext.SetupCompletionOperations.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == SetupCompletionOperation.SingletonId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            if (operation is null)
                return false;

            var candidateMatches = TokensEqual(expectedToken, DecryptSnapshot("candidate", operation.CandidateSessionCiphertext))
                                   && TokensEqual(expectedOperationId, DecryptSnapshot("candidate-operation", operation.CandidateOperationCiphertext));
            var emergencyMatches = TokensEqual(expectedToken, DecryptSnapshot("emergency", operation.EmergencySessionCiphertext))
                                   && TokensEqual(expectedOperationId, DecryptSnapshot("emergency-operation", operation.EmergencyOperationCiphertext));
            return candidateMatches || emergencyMatches;
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
            throw;
        }
    }

    private string? DecryptSnapshot(string key, string? ciphertext)
        => string.IsNullOrWhiteSpace(ciphertext)
            ? null
            : _configManager.DecryptSetupSnapshot($"setup.completion.{key}", ciphertext).Plaintext;

    private async Task<string?> ReadSecretAsync(
        string key,
        string displayName,
        CancellationToken cancellationToken)
    {
        var rows = (await _dbContext.ConfigItems
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false))
            .Where(row => SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonical)
                          && canonical == key)
            .ToList();

        if (rows.Count == 0)
            return null;
        if (rows.Count != 1)
            throw new InvalidOperationException($"Duplicate setup {displayName} rows detected.");

        var row = rows[0];
        if (!row.IsEncrypted)
            return string.IsNullOrWhiteSpace(row.ConfigValue) ? null : row.ConfigValue;
        if (!ConfigEncryptionService.IsEncryptedFormat(row.ConfigValue))
            throw new InvalidOperationException($"Encrypted setup {displayName} does not contain valid ciphertext.");

        var (plaintext, _, _) = _configManager.DecryptSetupValue(key, row.ConfigValue);
        return string.IsNullOrWhiteSpace(plaintext) ? null : plaintext;
    }

    private void DetachTrackedSetupRows()
    {
        foreach (var entry in _dbContext.ChangeTracker.Entries<ConfigItem>().ToList())
        {
            if (SensitiveConfigKeys.TryGetCanonicalSetupKey(entry.Entity.ConfigName, out var canonical)
                && PersistedKeySet.Contains(canonical))
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    private Dictionary<string, ExistingSetupRow> ReadExistingRows(IEnumerable<ConfigItem> rows)
    {
        var result = new Dictionary<string, ExistingSetupRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (!SensitiveConfigKeys.TryGetCanonicalSetupKey(row.ConfigName, out var canonicalName))
                throw new InvalidOperationException($"Unknown reserved setup key '{row.ConfigName}'.");

            if (!result.TryAdd(canonicalName, ReadExistingRow(canonicalName, row)))
                throw new InvalidOperationException(
                    $"Duplicate setup key '{canonicalName}' exists with multiple casings.");
        }

        return result;
    }

    private ExistingSetupRow ReadExistingRow(string canonicalName, ConfigItem row)
    {
        if (canonicalName == SetupConfigKeys.Completed)
        {
            if (row.IsEncrypted || !bool.TryParse(row.ConfigValue, out _))
                throw new InvalidOperationException("The setup completion marker must be an unencrypted boolean.");

            return new ExistingSetupRow(canonicalName, row, row.ConfigValue, false, false);
        }

        if (!row.IsEncrypted)
            return new ExistingSetupRow(canonicalName, row, row.ConfigValue, false, false);

        if (!ConfigEncryptionService.IsEncryptedFormat(row.ConfigValue))
            throw new InvalidOperationException($"Encrypted setup key '{canonicalName}' does not contain valid ciphertext.");

        var (plaintext, usedOldKey, requiresFormatUpgrade) =
            _configManager.DecryptSetupValue(canonicalName, row.ConfigValue);
        return new ExistingSetupRow(canonicalName, row, plaintext, usedOldKey, requiresFormatUpgrade);
    }

    private static Dictionary<string, string> ReadSuppliedSecrets(SetupSecretValues secrets)
    {
        var supplied = new Dictionary<string, string>(StringComparer.Ordinal);
        AddIfPresent(supplied, SetupConfigKeys.UsenetProviders, secrets.UsenetProviders);
        AddIfPresent(supplied, SetupConfigKeys.Indexers, secrets.Indexers);
        AddIfPresent(supplied, SetupConfigKeys.JellyfinApiKey, secrets.JellyfinApiKey);
        AddIfPresent(supplied, SetupConfigKeys.PluginApiKey, secrets.PluginApiKey);
        AddIfPresent(supplied, SetupConfigKeys.PluginApiKeyPrevious, secrets.PluginApiKeyPrevious);
        AddIfPresent(supplied, SetupConfigKeys.PluginApiKeyPreviousExpiresAt, secrets.PluginApiKeyPreviousExpiresAt);
        AddIfPresent(supplied, SetupConfigKeys.RunProgress, secrets.RunProgress);
        AddIfPresent(supplied, SetupConfigKeys.RunProgressReasons, secrets.RunProgressReasons);
        AddIfPresent(supplied, SetupConfigKeys.RepairRequired, secrets.RepairRequiredState);
        AddIfPresent(supplied, SetupConfigKeys.RepairCheckedAtUtc, secrets.RepairCheckedAtUtc);
        AddIfPresent(supplied, SetupConfigKeys.SonarrRepairReason, secrets.SonarrRepairReason);
        AddIfPresent(supplied, SetupConfigKeys.RadarrRepairReason, secrets.RadarrRepairReason);
        AddIfPresent(supplied, SetupConfigKeys.RepairReasons, secrets.RunProgressReasons);
        AddIfPresent(supplied, SetupConfigKeys.RevocationPendingToken, secrets.RevocationPendingToken);
        AddIfPresent(supplied, SetupConfigKeys.CandidateSessionToken, secrets.CandidateSessionToken);
        AddIfPresent(supplied, SetupConfigKeys.CandidateSessionOperation, secrets.CandidateSessionOperation);
        AddIfPresent(supplied, SetupConfigKeys.CandidateSessionIntent, secrets.CandidateSessionIntent);
        if (secrets.RevocationPending)
            supplied[SetupConfigKeys.RevocationPending] = "true";
        return supplied;
    }

    private static void AddIfPresent(IDictionary<string, string> values, string key, string? value)
    {
        if (value is not null)
            values[key] = value;
    }

    private static bool IsCandidateKey(string configName)
        => string.Equals(configName, SetupConfigKeys.CandidateSessionToken, StringComparison.Ordinal)
           || string.Equals(configName, SetupConfigKeys.CandidateSessionOperation, StringComparison.Ordinal)
           || string.Equals(configName, SetupConfigKeys.CandidateSessionIntent, StringComparison.Ordinal);

    private static bool IsRepairDiagnosticKey(string configName)
        => configName is SetupConfigKeys.RunProgress
            or SetupConfigKeys.RunProgressReasons
            or SetupConfigKeys.RepairRequired
            or SetupConfigKeys.RepairCheckedAtUtc
            or SetupConfigKeys.SonarrRepairReason
            or SetupConfigKeys.RadarrRepairReason
            or SetupConfigKeys.RepairReasons;

    private static bool IsNonSecretDiagnosticKey(string configName)
        => configName is SetupConfigKeys.RunProgress
            or SetupConfigKeys.RunProgressReasons
            or SetupConfigKeys.RepairRequired
            or SetupConfigKeys.RepairCheckedAtUtc
            or SetupConfigKeys.SonarrRepairReason
            or SetupConfigKeys.RadarrRepairReason
            or SetupConfigKeys.RepairReasons;

    private static bool ParseCompletionMarker(ConfigItem row)
        => bool.TryParse(row.ConfigValue, out var completed) && completed;

    private static void ValidateRequiredSecrets(IReadOnlyDictionary<string, string> secrets)
    {
        foreach (var key in RequiredCompletionKeys)
        {
            if (!secrets.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                throw new InvalidOperationException("Setup cannot be marked complete until all required secrets are persisted.");
        }

        try
        {
            using var json = JsonDocument.Parse(secrets[SetupConfigKeys.Indexers]);
            _ = json.RootElement;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Setup indexers must contain valid JSON.", ex);
        }
    }

    private static string PreserveAuthenticatedCiphertext(
        string preparedValue,
        ExistingSetupRow existing,
        string plaintext)
        => existing.Row.IsEncrypted
           && !existing.UsedOldKey
           && !existing.RequiresFormatUpgrade
           && existing.Plaintext == plaintext
            ? existing.Row.ConfigValue
            : preparedValue;

    private static string GetMergedValueOrCurrent(
        string key,
        IReadOnlyDictionary<string, string> merged,
        string currentValue)
        => merged.TryGetValue(key, out var mergedValue) ? mergedValue : currentValue;

    private static List<ConfigItem> CreatePlaintextRows(
        IReadOnlyDictionary<string, string> secrets,
        bool completed)
    {
        var rows = new List<ConfigItem>();
        foreach (var key in PersistedKeys)
        {
            if (key == SetupConfigKeys.Completed)
                continue;

            if (secrets.TryGetValue(key, out var value))
            {
                rows.Add(new ConfigItem
                {
                    ConfigName = key,
                    ConfigValue = value,
                    IsEncrypted = false,
                });
            }
        }

        rows.Add(new ConfigItem
        {
            ConfigName = SetupConfigKeys.Completed,
            ConfigValue = completed ? "true" : "false",
            IsEncrypted = false,
        });

        return rows;
    }

    private static List<ConfigItem> CloneRows(IEnumerable<ConfigItem> rows)
        => rows.Select(row => new ConfigItem
        {
            ConfigName = row.ConfigName,
            ConfigValue = row.ConfigValue,
            IsEncrypted = row.IsEncrypted,
        }).ToList();

    private static ConfigItem CloneRow(ConfigItem item)
        => new()
        {
            ConfigName = item.ConfigName,
            ConfigValue = item.ConfigValue,
            IsEncrypted = item.IsEncrypted,
        };

    private sealed class ExistingSetupRow(
        string canonicalName,
        ConfigItem row,
        string plaintext,
        bool usedOldKey,
        bool requiresFormatUpgrade)
    {
        public ExistingSetupRow(string canonicalName, ConfigItem row, string plaintext, bool requiresFormatUpgrade)
            : this(canonicalName, row, plaintext, usedOldKey: false, requiresFormatUpgrade)
        {
        }

        public string CanonicalName { get; } = canonicalName;
        public ConfigItem Row { get; } = row;
        private string _plaintext = plaintext;
        public string Plaintext => _plaintext;
        public bool UsedOldKey { get; } = usedOldKey;
        public bool RequiresFormatUpgrade { get; } = requiresFormatUpgrade;

        internal void ClearPlaintext()
        {
            _plaintext = string.Empty;
            if (!Row.IsEncrypted)
                Row.ConfigValue = string.Empty;
        }

        public override string ToString()
            => $"ExistingSetupRow(CanonicalName={CanonicalName}, IsEncrypted={Row.IsEncrypted}, UsedOldKey={UsedOldKey})";
    }

    private static async Task ExecuteWithRetriesAsync(
        Func<int, Task> operation,
        CancellationToken cancellationToken,
        Action? onRetry = null)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation(attempt).ConfigureAwait(false);
                return;
            }
            catch (SetupRetryableTransactionException) when (attempt < MaxWriteAttempts)
            {
                onRetry?.Invoke();
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

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

    /// <summary>
    /// Exercises the exact bounded retry loop without providing a persistence
    /// bypass. This internal seam is for SQLSTATE classification tests only.
    /// </summary>
    internal static Task ExecuteWithRetriesForTestsAsync(
        Func<int, Task> operation,
        CancellationToken cancellationToken = default)
        => ExecuteWithRetriesForTestsCoreAsync(operation, cancellationToken);

    private static async Task ExecuteWithRetriesForTestsCoreAsync(
        Func<int, Task> operation,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation(attempt).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < MaxWriteAttempts
                                               && SetupTransactionOutcome.IsRetryableWriteAbort(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

}
