using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.UsenetSettings;

[ApiController]
[Route("api/admin-settings/usenet")]
public sealed class UsenetSettingsController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    ConfigEncryptionService encryption,
    SetupConfigPersistence setupPersistence,
    Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>>? beginTransaction = null,
    Func<DavDatabaseContext>? freshContextFactory = null) : BaseApiController
{
    private readonly Func<IsolationLevel, CancellationToken, Task<IDbContextTransaction>> _beginTransaction =
        beginTransaction ?? ((isolation, cancellationToken) => dbClient.Ctx.Database.BeginTransactionAsync(isolation, cancellationToken));
    private readonly Func<DavDatabaseContext> _freshContextFactory = freshContextFactory ?? (() => new DavDatabaseContext());
    private const int MaxBodyBytes = 64 * 1024;
    private const int MaxProviders = 32;
    private const int MaxHostLength = 255;
    private const int MaxUserLength = 256;
    private const int MaxPasswordLength = 512;
    private const int MaxIdLength = 128;

    private static readonly JsonSerializerOptions StoredJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record DurableProviderSnapshot(
        string StoredValue,
        UsenetProviderConfig Config,
        string Revision,
        string Plaintext);

    protected override async Task<IActionResult> HandleRequest()
    {
        if (HttpContext.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase))
            return Ok(await ReadCurrentAsync(HttpContext.RequestAborted).ConfigureAwait(false));

        if (!HttpContext.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("Invalid request payload.");

        var request = await ReadRequestAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        try
        {
            return Ok(await UpdateAsync(request, HttpContext.RequestAborted).ConfigureAwait(false));
        }
        catch (SetupTransactionRecoveryRequiredException)
        {
            // Commit outcome is unknown: never publish or echo the request.
            // The next request re-reads the durable row/revision.
            return Conflict(new BaseApiResponse
            {
                Status = false,
                Error = "Settings persistence is recovering; retry with the current settings."
            });
        }
    }

    private async Task<UsenetSettingsResponse> ReadCurrentAsync(CancellationToken cancellationToken)
    {
        var row = await ReadProviderRowAsync(cancellationToken).ConfigureAwait(false);
        var storedValue = row?.ConfigValue ?? string.Empty;
        var providers = ParseStoredProviders(row is null
            ? ""
            : row.IsEncrypted
                ? encryption.Decrypt(SetupConfigKeys.UsenetProviders, row.ConfigValue).plaintext
                : row.ConfigValue);
        return ToResponse(providers, encryption.CreateOpaqueRevision(storedValue));
    }

    private async Task<UsenetSettingsResponse> UpdateAsync(
        UsenetSettingsRequest request,
        CancellationToken cancellationToken)
    {
        UsenetSettingsResponse? response = null;
        await configManager.WithMutationGateAsync(async () =>
        {
            var transaction = await _beginTransaction(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var lifetime = new SetupPersistenceTransaction(transaction);
            string? desiredStoredValue = null;
            Exception? transactionDisposeFailure = null;
            try
            {
                await SetupMutationFenceLock.AcquireAsync(dbClient.Ctx, transaction, cancellationToken)
                    .ConfigureAwait(false);
                var rows = await dbClient.Ctx.ConfigItems
                    .AsNoTracking()
                    .Where(row => row.ConfigName == SetupConfigKeys.UsenetProviders
                               || row.ConfigName.ToLower() == SetupConfigKeys.UsenetProviders)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (rows.Count(row => row.ConfigName != SetupConfigKeys.UsenetProviders) > 0
                    || rows.Count(row => row.ConfigName == SetupConfigKeys.UsenetProviders) > 1)
                    throw new InvalidOperationException("Provider settings are ambiguous.");

                var row = rows.SingleOrDefault(row => row.ConfigName == SetupConfigKeys.UsenetProviders);
                var storedValue = row?.ConfigValue ?? string.Empty;
                var actualRevision = encryption.CreateOpaqueRevision(storedValue);
                if (!FixedTimeEquals(request.Revision, actualRevision))
                    throw new ConcurrencyConflictException();

                var current = ParseStoredProviders(row is null
                    ? ""
                    : row.IsEncrypted
                        ? encryption.Decrypt(SetupConfigKeys.UsenetProviders, row.ConfigValue).plaintext
                        : row.ConfigValue);
                var merged = MergeAndValidate(current, request.Providers);
                var canonicalJson = JsonSerializer.Serialize(
                    new UsenetProviderConfig { Providers = merged });
                desiredStoredValue = await setupPersistence.SaveUsenetProvidersInTransactionAsync(
                    canonicalJson, transaction, cancellationToken).ConfigureAwait(false);

                // Cancellation is intentionally not allowed to turn a commit
                // into a rollback. A provider may have committed before its
                // driver reports an exception, so the catch path disposes and
                // settles the durable outcome from a new context.
                await lifetime.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                transactionDisposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                if (transactionDisposeFailure is not null)
                    throw new InvalidOperationException("The settings transaction could not be closed.", transactionDisposeFailure);

                // Publish only after the transaction is durable and while the
                // shared mutation gate is still held. Events are redacted by
                // ConfigManager; neither this response nor the event contains a password.
                configManager.ApplySetupValuesNoLock(new[]
                {
                    new ConfigItem
                    {
                        ConfigName = SetupConfigKeys.UsenetProviders,
                        ConfigValue = canonicalJson,
                        IsEncrypted = false,
                    }
                });
                response = ToResponse(
                    new UsenetProviderConfig { Providers = merged },
                    encryption.CreateOpaqueRevision(desiredStoredValue));
            }
            catch (Exception exception)
            {
                if (lifetime.CommitAttempted)
                {
                    // A commit exception is not evidence of rollback. The
                    // transaction is disposed before classifying the outcome,
                    // and a fresh no-tracking context is the source of truth.
                    response = await ResolveCommitAttemptAsync(
                        lifetime,
                        exception,
                        desiredStoredValue,
                        transactionDisposeFailure).ConfigureAwait(false);
                    return;
                }

                // Before CommitAsync there is no durable commit ambiguity. Roll
                // back, dispose, and only then classify retryable provider
                // failures; no cache/event is published on this path.
                var rollbackFailure = await lifetime.TryRollbackAsync().ConfigureAwait(false);
                var disposeFailure = await lifetime.TryDisposeAsync().ConfigureAwait(false);
                dbClient.Ctx.ChangeTracker.Clear();
                if (rollbackFailure is not null || disposeFailure is not null)
                    throw new SetupTransactionRecoveryRequiredException(exception, rollbackFailure, disposeFailure);
                if (SetupTransactionOutcome.IsRetryableWriteAbort(exception))
                    throw new ConcurrencyConflictException();
                throw;
            }
        }, cancellationToken).ConfigureAwait(false);

        return response!;
    }

    private async Task<UsenetSettingsResponse> ResolveCommitAttemptAsync(
        SetupPersistenceTransaction lifetime,
        Exception operationFailure,
        string? desiredStoredValue,
        Exception? priorDisposeFailure)
    {
        // Never pass the request cancellation token to settlement. We must be
        // able to learn what committed even after the caller disconnected.
        var disposeFailure = priorDisposeFailure ?? await lifetime.TryDisposeAsync().ConfigureAwait(false);
        // Classification is deliberately after disposal. No transaction
        // operation, especially rollback, is attempted for an unknown commit.
        var classifiedOutcome = SetupTransactionOutcome.ClassifyCommitFailure(operationFailure, lifetime.State);

        DurableProviderSnapshot? actual = null;
        Exception? readFailure = null;
        try
        {
            actual = await ReadDurableSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            readFailure = exception;
        }

        if (actual is not null)
        {
            // Reconcile the in-memory snapshot even when the commit was
            // rejected or the outcome remains unknown.
            configManager.ApplySetupValuesNoLock(new[]
            {
                new ConfigItem
                {
                    ConfigName = SetupConfigKeys.UsenetProviders,
                    ConfigValue = actual.Plaintext,
                    IsEncrypted = false,
                }
            });

            var cancellationDuringCommit = operationFailure is OperationCanceledException;
            var durableDesired = desiredStoredValue is not null
                && string.Equals(actual.StoredValue, desiredStoredValue, StringComparison.Ordinal);
            if (durableDesired
                && disposeFailure is null
                && !cancellationDuringCommit
                && classifiedOutcome == SetupCommitOutcome.Uncertain)
            {
                // Commit-before-throw: publish exactly once from the fresh
                // durable row and return a normal success response.
                return ToResponse(actual.Config, actual.Revision);
            }
        }

        throw new SetupTransactionRecoveryRequiredException(
            operationFailure,
            readFailure,
            disposeFailure);
    }

    private async Task<DurableProviderSnapshot> ReadDurableSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var context = _freshContextFactory();
        var rows = await context.ConfigItems
            .AsNoTracking()
            .Where(row => row.ConfigName == SetupConfigKeys.UsenetProviders
                       || row.ConfigName.ToLower() == SetupConfigKeys.UsenetProviders)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > 1)
            throw new InvalidOperationException("Provider settings are ambiguous.");

        var row = rows.SingleOrDefault();
        var storedValue = row?.ConfigValue ?? string.Empty;
        var plaintext = row is null
            ? string.Empty
            : row.IsEncrypted
                ? encryption.Decrypt(SetupConfigKeys.UsenetProviders, row.ConfigValue).plaintext
                : row.ConfigValue;
        return new DurableProviderSnapshot(
            storedValue,
            ParseStoredProviders(plaintext),
            encryption.CreateOpaqueRevision(storedValue),
            plaintext);
    }

    private async Task<ConfigItem?> ReadProviderRowAsync(CancellationToken cancellationToken)
    {
        var rows = await dbClient.Ctx.ConfigItems
            .AsNoTracking()
            .Where(row => row.ConfigName == SetupConfigKeys.UsenetProviders
                       || row.ConfigName.ToLower() == SetupConfigKeys.UsenetProviders)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count > 1)
            throw new InvalidOperationException("Provider settings are ambiguous.");
        return rows.SingleOrDefault();
    }

    private static UsenetProviderConfig ParseStoredProviders(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new UsenetProviderConfig();
        try
        {
            return JsonSerializer.Deserialize<UsenetProviderConfig>(json, StoredJsonOptions)
                ?? new UsenetProviderConfig();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Stored provider settings are invalid.", exception);
        }
    }

    private static List<UsenetProviderConfig.ConnectionDetails> MergeAndValidate(
        UsenetProviderConfig current,
        IReadOnlyList<UsenetProviderInput> inputs)
    {
        if (inputs.Count > MaxProviders)
            throw new BadHttpRequestException("Invalid provider settings.");

        var existing = current.Providers.Select((provider, index) =>
        {
            provider.Id ??= "p-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return provider;
        }).ToDictionary(provider => provider.Id!, StringComparer.Ordinal);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<UsenetProviderConfig.ConnectionDetails>(inputs.Count);

        foreach (var input in inputs)
        {
            ValidateInput(input);
            var isExisting = !string.IsNullOrEmpty(input.Id);
            UsenetProviderConfig.ConnectionDetails? old = null;
            if (isExisting)
            {
                if (!existing.TryGetValue(input.Id!, out old) || !refs.Add(input.Id!))
                    throw new BadHttpRequestException("Invalid provider settings.");
            }

            var password = string.IsNullOrEmpty(input.Password) ? old?.Pass ?? "" : input.Password;
            if (input.Type != ProviderType.Disabled && string.IsNullOrEmpty(password))
                throw new BadHttpRequestException("Enabled providers require a password.");

            result.Add(new UsenetProviderConfig.ConnectionDetails
            {
                Id = old?.Id ?? "p-" + Guid.NewGuid().ToString("N"),
                Type = input.Type!.Value,
                Host = input.Host!,
                Port = input.Port!.Value,
                UseSsl = input.Ssl!.Value,
                User = input.User!,
                Pass = password,
                MaxConnections = input.Max!.Value,
            });
        }
        return result;
    }

    private static void ValidateInput(UsenetProviderInput input)
    {
        if (string.IsNullOrEmpty(input.Host) || input.Host.Length > MaxHostLength
            || input.Host.Any(char.IsWhiteSpace) || input.Host.Any(char.IsControl)
            || Uri.CheckHostName(input.Host) == UriHostNameType.Unknown
            || input.Port is < 1 or > 65535
            || string.IsNullOrEmpty(input.User) || input.User.Length > MaxUserLength
            || input.User.Any(char.IsControl)
            || input.Max is < 1 or > 1000
            || input.Ssl is null || input.Type is null)
            throw new BadHttpRequestException("Invalid provider settings.");
        if (input.Id is not null && (input.Id.Length == 0 || input.Id.Length > MaxIdLength
            || input.Id.Any(char.IsControl) || input.Id.Any(char.IsWhiteSpace)))
            throw new BadHttpRequestException("Invalid provider settings.");
        if (input.Password is not null && (input.Password.Length > MaxPasswordLength
            || input.Password.Any(char.IsControl)))
            throw new BadHttpRequestException("Invalid provider settings.");
        if (!Enum.IsDefined(input.Type.Value))
            throw new BadHttpRequestException("Invalid provider settings.");
    }

    private static UsenetSettingsResponse ToResponse(UsenetProviderConfig config, string revision)
        => new()
        {
            Revision = revision,
            Providers = config.Providers.Select((provider, index) => new UsenetProviderResponse
            {
                Id = provider.Id ?? "p-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Host = provider.Host,
                Port = provider.Port,
                Ssl = provider.UseSsl,
                User = provider.User,
                Max = provider.MaxConnections,
                Type = provider.Type,
                HasPassword = !string.IsNullOrEmpty(provider.Pass),
            }).ToList(),
        };

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        var leftFixed = new byte[256];
        var rightFixed = new byte[256];
        try
        {
            if (leftBytes.Length > leftFixed.Length || rightBytes.Length > rightFixed.Length)
                return false;
            leftBytes.CopyTo(leftFixed, 0);
            rightBytes.CopyTo(rightFixed, 0);
            return leftBytes.Length == rightBytes.Length
                && CryptographicOperations.FixedTimeEquals(leftFixed, rightFixed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
            CryptographicOperations.ZeroMemory(leftFixed);
            CryptographicOperations.ZeroMemory(rightFixed);
        }
    }

    private async Task<UsenetSettingsRequest> ReadRequestAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Request.ContentLength is > MaxBodyBytes)
            throw new BadHttpRequestException("Invalid request payload.");
        await using var body = new MemoryStream();
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await HttpContext.Request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
            if (total > MaxBodyBytes)
                throw new BadHttpRequestException("Invalid request payload.");
            await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            using var document = JsonDocument.Parse(body.ToArray(), new JsonDocumentOptions
            {
                MaxDepth = 8,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new BadHttpRequestException("Invalid request payload.");
            ValidateFieldNames(root, new HashSet<string>(["revision", "providers"], StringComparer.Ordinal));
            if (!root.TryGetProperty("revision", out var revisionElement)
                || !root.TryGetProperty("providers", out var providersElement)
                || revisionElement.ValueKind != JsonValueKind.String
                || providersElement.ValueKind != JsonValueKind.Array)
                throw new BadHttpRequestException("Invalid request payload.");

            var revision = revisionElement.GetString()!;
            if (revision.Length == 0 || revision.Length > 256)
                throw new BadHttpRequestException("Invalid request payload.");
            var providers = new List<UsenetProviderInput>();
            foreach (var provider in providersElement.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object)
                    throw new BadHttpRequestException("Invalid request payload.");
                ValidateFieldNames(provider, new HashSet<string>(
                    ["id", "host", "port", "ssl", "user", "max", "type", "password"],
                    StringComparer.Ordinal));
                providers.Add(new UsenetProviderInput
                {
                    Id = ReadString(provider, "id"),
                    Host = ReadString(provider, "host"),
                    Port = ReadInt(provider, "port"),
                    Ssl = ReadBool(provider, "ssl"),
                    User = ReadString(provider, "user"),
                    Max = ReadInt(provider, "max"),
                    Type = ReadEnum(provider, "type"),
                    Password = ReadString(provider, "password"),
                });
            }
            if (providers.Count > MaxProviders)
                throw new BadHttpRequestException("Invalid provider settings.");
            return new UsenetSettingsRequest { Revision = revision, Providers = providers };
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Invalid request payload.");
        }
    }

    private static string? ReadString(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new BadHttpRequestException("Invalid request payload.");
        return value.GetString();
    }

    private static int? ReadInt(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new BadHttpRequestException("Invalid request payload.");
        return number;
    }

    private static bool? ReadBool(JsonElement objectElement, string name)
    {
        if (!objectElement.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BadHttpRequestException("Invalid request payload.");
        return value.GetBoolean();
    }

    private static ProviderType? ReadEnum(JsonElement objectElement, string name)
    {
        var value = ReadInt(objectElement, name);
        return value is null ? null : (ProviderType)value.Value;
    }

    private static void ValidateFieldNames(JsonElement objectElement, IReadOnlySet<string> allowed)
    {
        var fields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in objectElement.EnumerateObject())
            if (!allowed.Contains(property.Name) || !fields.Add(property.Name))
                throw new BadHttpRequestException("Invalid request payload.");
    }
}
