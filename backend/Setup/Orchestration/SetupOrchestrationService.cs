using System.Data;
using System.Globalization;
using System.Net;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Serilog;
using NzbWebDAV.Clients.ArrSetup;
using NzbWebDAV.Clients.JellyfinSetup;
using NzbWebDAV.Clients.Newznab;
using NzbWebDAV.Clients.ProwlarrSetup;
using NzbWebDAV.Clients;
using NzbWebDAV.Clients.RadarrSonarr;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Setup.Discovery;

namespace NzbWebDAV.Setup.Orchestration;

public sealed class SetupOrchestrationService
{
    private sealed class SetupValidationFailureException(string reasonCode)
        : Exception("Setup validation failed.")
    {
        public string ReasonCode { get; } = SetupStepStatus.SafeToken(reasonCode, SetupReasonCodes.Unknown);
    }

    private static readonly string[] StepNames =
    [
        "provider-indexers",
        "arr-key-discovery",
        "prowlarr",
        "arr-clients",
        "jellyfin",
        "health-check",
    ];

    private static readonly TimeSpan PluginApiKeyPreviousWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan GrantLeaseReleaseRetryDelay = TimeSpan.FromMilliseconds(50);
    private const int GrantLeaseReleaseRetryCount = 2;
    private const string VerificationLeasePurpose = "status-verification";
    // This endpoint performs finite validation plus finite sequential service
    // setup. It is intentionally setup-specific; ordinary backend requests
    // retain their normal timeout.
    public static readonly TimeSpan SetupOperationTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions RunProgressOptions = new()
    {
        Converters = { new SetupStepStateJsonConverter() },
    };

    private readonly ConfigManager _configManager;
    private readonly SetupConfigPersistence _persistence;
    private readonly SetupGrantService _setupGrantService;
    private readonly DavDatabaseClient _dbClient;
    private readonly Func<HttpClient> _httpClientFactory;
    private readonly SetupEnvironmentOptions _environment;
    private readonly ArrApiKeyDiscovery _discovery;
    private readonly TimeProvider _timeProvider;
    private readonly IUsenetCredentialValidator _usenetCredentialValidator;
    private readonly Func<NewznabIndexerCredential, CancellationToken, Task<NewznabCapabilityResult>> _indexerCapabilityValidator;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _capabilityHostResolver;
    private readonly SetupRunLeaseService _runLeaseService;
    private SetupRunLeaseHandle? _activeRunLease;
    private string _leaseGrant = string.Empty;
    private string _leasePurpose = "setup";
    private Dictionary<string, string> _progressReasons = new(StringComparer.Ordinal);

    public SetupOrchestrationService(
        ConfigManager configManager,
        SetupConfigPersistence persistence,
        SetupGrantService setupGrantService,
        DavDatabaseClient dbClient,
        Func<HttpClient>? httpClientFactory = null,
        TimeProvider? timeProvider = null,
        IUsenetCredentialValidator? usenetCredentialValidator = null,
        Func<string, CancellationToken, Task<IPAddress[]>>? capabilityHostResolver = null,
        Func<NewznabIndexerCredential, CancellationToken, Task<NewznabCapabilityResult>>? indexerCapabilityValidator = null)
    {
        _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
        _persistence = persistence ?? throw new ArgumentNullException(nameof(persistence));
        _setupGrantService = setupGrantService ?? throw new ArgumentNullException(nameof(setupGrantService));
        _dbClient = dbClient ?? throw new ArgumentNullException(nameof(dbClient));
        _httpClientFactory = httpClientFactory ?? SetupHttpClientFactory.Create;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _usenetCredentialValidator = usenetCredentialValidator ?? new UsenetCredentialValidator();
        _indexerCapabilityValidator = indexerCapabilityValidator ?? ValidateIndexerCapabilityAsync;
        _capabilityHostResolver = capabilityHostResolver ?? ((host, token) => Dns.GetHostAddressesAsync(host, token));
        _runLeaseService = new SetupRunLeaseService(dbClient.Ctx, _timeProvider);

        _environment = SetupEnvironmentOptions.FromEnvironment();
        _discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions(
            new ArrConfigDiscoveryPaths(
                _environment.SonarrConfigPath,
                _environment.RadarrConfigPath,
                _environment.ProwlarrConfigPath),
            ArrConfigDiscoveryOptions.DefaultMaxFileBytes));
    }

    public SetupStatus GetStatus()
    {
        var enabled = _environment.FullStackEnabled;
        var completed = _configManager.IsSetupCompleted();

        var progress = LoadProgress();
        var reasons = LoadProgressReasons();
        var repairRequired = completed && _configManager.IsSetupRepairRequired();
        var checkedAt = _configManager.GetSetupRepairCheckedAtUtc();
        var finalHealthReady = completed && !repairRequired && checkedAt is not null
            && _timeProvider.GetUtcNow().UtcDateTime - checkedAt.Value.ToUniversalTime() <= TimeSpan.FromMinutes(5);
        if (completed && !finalHealthReady)
        {
            // A marker without a recent exact persisted verification is only a
            // Checking snapshot. The POST verify command owns external checks.
            MarkUnverifiedCompletedPhases(progress, reasons);
            progress["health-check"] = SetupStepState.Warning;
            reasons["health-check"] = SetupReasonCodes.VerificationStale;
        }
        return new SetupStatus(
            enabled,
            completed,
            StepNames.Select(step => CreateStepStatus(step, progress[step], reasons.TryGetValue(step, out var reason) ? reason : null, finalHealthReady)).ToList(),
            Services: CreateServiceStatuses(progress, finalHealthReady, reasons, repairRequired),
            RepairRequired: repairRequired);
    }

    public async Task<SetupStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        // GET is a pure read. Use one database snapshot for the setup marker,
        // diagnostics, completion operation, candidate, revocation,
        // reservation, and repair state; never combine separate cache/query
        // observations that can straddle a completion commit.
        var snapshot = await _persistence.ReadStatusSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var values = snapshot.Values;
        var completed = bool.TryParse(values.GetValueOrDefault(SetupConfigKeys.Completed), out var marker) && marker;
        var progress = LoadProgress(values.GetValueOrDefault(SetupConfigKeys.RunProgress));
        var reasons = LoadProgressReasons(values.GetValueOrDefault(SetupConfigKeys.RunProgressReasons));
        var repairRequired = completed && string.Equals(
            values.GetValueOrDefault(SetupConfigKeys.RepairRequired), "true", StringComparison.OrdinalIgnoreCase);
        var checkedAt = ParseUtc(values.GetValueOrDefault(SetupConfigKeys.RepairCheckedAtUtc));
        var finalHealthReady = completed && !repairRequired && checkedAt is not null
            && _timeProvider.GetUtcNow().UtcDateTime - checkedAt.Value.ToUniversalTime() <= TimeSpan.FromMinutes(5);
        if (completed && !finalHealthReady)
        {
            MarkUnverifiedCompletedPhases(progress, reasons);
            progress["health-check"] = SetupStepState.Warning;
            reasons["health-check"] = SetupReasonCodes.VerificationStale;
        }

        var steps = StepNames.Select(step => CreateStepStatus(step, progress[step], reasons.TryGetValue(step, out var reason) ? reason : null, finalHealthReady)).ToList();
        var services = snapshot.SetupRunBusy
            ? CreateBusyLiveServices().Values.ToList()
            : CreateServiceStatuses(progress, finalHealthReady, reasons, repairRequired);
        if (snapshot.SetupRunBusy)
        {
            steps = steps.Select(step => new SetupStepStatus(
                step.Name,
                step.State,
                SetupReasonCodes.SetupRunBusy,
                SetupReasonCodes.SetupRunBusy,
                step.ServiceReady)).ToList();
        }

        return new SetupStatus(
            _environment.FullStackEnabled,
            completed,
            steps,
            snapshot.RecoveryPending,
            services,
            repairRequired);
    }

    private static DateTime? ParseUtc(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;

    /// <summary>Explicit fenced live verification command used by POST.</summary>
    public async Task<SetupStatus> VerifyStatusAsync(CancellationToken cancellationToken = default)
    {
        var status = GetStatus();
        var recoveryPending = await _setupGrantService.HasRecoveryPendingAsync(cancellationToken).ConfigureAwait(false);
        var markerCompleted = _configManager.IsSetupCompleted();
        if (!markerCompleted)
            return new SetupStatus(status.Enabled, false, status.Steps, recoveryPending, status.Services, status.RepairRequired);

        // Prowlarr's persisted testall endpoint is a POST even though this is
        // a status read. Fence the complete live verification as a short setup
        // run so its callback cannot issue an unfenced mutation or overlap
        // repair/setup work. The owner id is process-random and the empty
        // grant hashes to a non-secret value; no bearer is minted for status.
        var verificationLease = await _runLeaseService.TryAcquireAsync(
                string.Empty,
                VerificationLeasePurpose,
                cancellationToken)
            .ConfigureAwait(false);
        if (verificationLease is null)
        {
            return await BuildLiveStatusAsync(
                    status,
                    recoveryPending,
                    markerCompleted,
                    CreateBusyLiveServices(),
                    _timeProvider.GetUtcNow().UtcDateTime,
                    lease: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }

        var previousLease = _activeRunLease;
        var previousGrant = _leaseGrant;
        var previousPurpose = _leasePurpose;
        _activeRunLease = verificationLease;
        _leaseGrant = string.Empty;
        _leasePurpose = VerificationLeasePurpose;
        try
        {
            var healthCheckStartedAt = _timeProvider.GetUtcNow().UtcDateTime;
            IReadOnlyDictionary<string, SetupServiceStatus> live;
            try
            {
                live = await ExecuteExternalMutationAsync(
                        () => VerifyLiveServicesAsync(cancellationToken),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BadHttpRequestException)
            {
                // A takeover can happen between a live-check callback and its
                // upstream POST. Do not publish a stale result or turn the
                // completed marker into a request error; report the fenced run
                // as busy and let the finally block CAS-release our old lease.
                return await BuildLiveStatusAsync(
                        status,
                        recoveryPending,
                        markerCompleted,
                        CreateBusyLiveServices(),
                        healthCheckStartedAt,
                        lease: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            try
            {
                return await BuildLiveStatusAsync(
                        status,
                        recoveryPending,
                        markerCompleted,
                        live,
                        healthCheckStartedAt,
                        verificationLease,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (BadHttpRequestException)
            {
                return await BuildLiveStatusAsync(
                        status,
                        recoveryPending,
                        markerCompleted,
                        CreateBusyLiveServices(),
                        healthCheckStartedAt,
                        lease: null,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            if (ReferenceEquals(_activeRunLease, verificationLease))
                _activeRunLease = previousLease;
            _leaseGrant = previousGrant;
            _leasePurpose = previousPurpose;
            await verificationLease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task<SetupStatus> BuildLiveStatusAsync(
        SetupStatus status,
        bool recoveryPending,
        bool markerCompleted,
        IReadOnlyDictionary<string, SetupServiceStatus> live,
        DateTime healthCheckStartedAt,
        SetupRunLeaseHandle? lease,
        CancellationToken cancellationToken)
    {
        var progress = LoadProgress();
        var reasons = LoadProgressReasons();

        // Sonarr and Radarr share one durable phase. Resolve both outcomes
        // before touching that phase so a ready service can never overwrite a
        // failure reported by the other service. The phase reason is a stable,
        // bounded service code; the individual live service reasons below keep
        // their more specific diagnostics.
        var sonarrReady = live.TryGetValue("sonarr", out var sonarrLive) && sonarrLive.Ready;
        var radarrReady = live.TryGetValue("radarr", out var radarrLive) && radarrLive.Ready;
        var arrReady = sonarrReady && radarrReady;
        var ready = live.Count > 0 && live.Values.All(service => service.Ready) && arrReady;
        var sonarrFailed = !sonarrReady && !string.Equals(sonarrLive?.Reason, SetupReasonCodes.SetupRunBusy, StringComparison.Ordinal);
        var radarrFailed = !radarrReady && !string.Equals(radarrLive?.Reason, SetupReasonCodes.SetupRunBusy, StringComparison.Ordinal);
        var arrPhaseReason = arrReady
            ? null
            : sonarrFailed
                ? SetupReasonCodes.SonarrFailed
                : radarrFailed
                    ? SetupReasonCodes.RadarrFailed
                    : SetupReasonCodes.SetupRunBusy;

        string? firstFailureReason = arrPhaseReason;
        foreach (var service in live.Values)
        {
            // arr-clients is assigned exactly once below, after both Arr
            // outcomes have been evaluated.
            if (service.Name is "sonarr" or "radarr")
                continue;

            var step = service.Name switch
            {
                "nzbdav" => "provider-indexers",
                "prowlarr" => "prowlarr",
                "jellyfin" => "jellyfin",
                _ => "health-check",
            };
            if (service.Ready)
            {
                progress[step] = SetupStepState.Complete;
                reasons.Remove(step);
            }
            else
            {
                var reason = service.Reason ?? SetupReasonCodes.BackendUnavailable;
                progress[step] = SetupStepState.Warning;
                reasons[step] = reason;
                firstFailureReason ??= reason;
            }
        }

        if (arrReady)
        {
            progress["arr-clients"] = SetupStepState.Complete;
            reasons.Remove("arr-clients");
        }
        else
        {
            progress["arr-clients"] = SetupStepState.Warning;
            reasons["arr-clients"] = arrPhaseReason ?? SetupReasonCodes.ArrFailed;
        }

        if (ready)
        {
            foreach (var step in StepNames)
                progress[step] = SetupStepState.Complete;
            reasons.Clear();
        }
        else
        {
            // Health verification itself is a durable phase. Do not leave a
            // historical Complete value here when a managed service drifted;
            // otherwise a fresh reader would suppress the diagnostic reason.
            progress["health-check"] = SetupStepState.Warning;
            reasons["health-check"] = firstFailureReason ?? SetupReasonCodes.BackendUnavailable;
        }

        var sonarrReason = live.TryGetValue("sonarr", out var sonarr) && !sonarr.Ready
            ? sonarr.Reason ?? SetupReasonCodes.SonarrFailed : null;
        var radarrReason = live.TryGetValue("radarr", out var radarr) && !radarr.Ready
            ? radarr.Reason ?? SetupReasonCodes.RadarrFailed : null;
        var serializedProgress = JsonSerializer.Serialize(
            StepNames.ToDictionary(step => step, step => progress[step], StringComparer.Ordinal),
            RunProgressOptions);
        var serializedReasons = JsonSerializer.Serialize(reasons);
        if (lease is not null)
        {
            await _persistence.SaveLiveHealthStateWithLeaseAsync(
                    ready,
                    healthCheckStartedAt,
                    sonarrReason,
                    radarrReason,
                    serializedReasons,
                    serializedProgress,
                    lease,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var steps = StepNames.Select(step => CreateStepStatus(
            step,
            progress[step],
            reasons.TryGetValue(step, out var reason) ? reason : null,
            ready)).ToList();
        var services = live.Values.ToList();
        // A busy verification never performed a live check and must not turn
        // a completed installation into a repair-required state. A fenced
        // check may publish the latch from its actual result; otherwise the
        // durable latch is the only safe value to report.
        var repairRequired = lease is null
            ? _configManager.IsSetupRepairRequired()
            : !ready;
        // The canonical completion marker remains true while repair is
        // required. Ready is derived only from the live check; callers must
        // branch on RepairRequired rather than reopening six-step setup.
        return new SetupStatus(status.Enabled, markerCompleted, steps, recoveryPending, services, repairRequired);
    }

    private static IReadOnlyDictionary<string, SetupServiceStatus> CreateBusyLiveServices()
        => new Dictionary<string, SetupServiceStatus>(StringComparer.Ordinal)
        {
            ["nzbdav"] = new SetupServiceStatus("nzbdav", false, SetupReasonCodes.SetupRunBusy, SetupReasonCodes.SetupRunBusy),
            ["jellyfin"] = new SetupServiceStatus("jellyfin", false, SetupReasonCodes.SetupRunBusy, SetupReasonCodes.SetupRunBusy),
            ["sonarr"] = new SetupServiceStatus("sonarr", false, SetupReasonCodes.SetupRunBusy, SetupReasonCodes.SetupRunBusy),
            ["radarr"] = new SetupServiceStatus("radarr", false, SetupReasonCodes.SetupRunBusy, SetupReasonCodes.SetupRunBusy),
            ["prowlarr"] = new SetupServiceStatus("prowlarr", false, SetupReasonCodes.SetupRunBusy, SetupReasonCodes.SetupRunBusy),
        };

    private async Task<IReadOnlyDictionary<string, SetupServiceStatus>> VerifyLiveServicesAsync(CancellationToken cancellationToken)
    {
        var results = new Dictionary<string, SetupServiceStatus>(StringComparer.Ordinal)
        {
            ["nzbdav"] = new SetupServiceStatus("nzbdav", false, SetupReasonCodes.NzbdavFailed, SetupReasonCodes.NzbdavFailed),
            ["jellyfin"] = new SetupServiceStatus("jellyfin", false, SetupReasonCodes.JellyfinFailed, SetupReasonCodes.JellyfinFailed),
            ["sonarr"] = new SetupServiceStatus("sonarr", false, SetupReasonCodes.SonarrFailed, SetupReasonCodes.SonarrFailed),
            ["radarr"] = new SetupServiceStatus("radarr", false, SetupReasonCodes.RadarrFailed, SetupReasonCodes.RadarrFailed),
            ["prowlarr"] = new SetupServiceStatus("prowlarr", false, SetupReasonCodes.ProwlarrFailed, SetupReasonCodes.ProwlarrFailed),
        };

        // NZBDAV readiness is a bounded real local check, not two non-empty
        // strings. It exercises the database/manifest backing store, version
        // identity, and both distinct credentials used by the managed clients.
        var pluginKey = _configManager.GetSetupPluginApiKey();
        var nzbdavKey = _configManager.GetApiKey();
        try
        {
            var storedConfiguration = SetupConfigurationData.FromStoredValues(
                _configManager.GetSetupIndexers(), _configManager.GetSetupUsenetProviders());
            var providerResults = await RunBoundedAsync(
                    storedConfiguration.Providers.Providers,
                    provider => _usenetCredentialValidator.ValidateAsync(provider, cancellationToken),
                    maxConcurrency: 4,
                    cancellationToken)
                .ConfigureAwait(false);
            var providerFailure = providerResults.FirstOrDefault(result => !result.Valid);
            if (providerFailure is not null)
            {
                var reason = providerFailure.Code == UsenetCredentialValidationCode.AuthenticationFailed
                    ? SetupReasonCodes.ProviderAuthFailed
                    : SetupReasonCodes.ProviderFailed;
                results["nzbdav"] = new SetupServiceStatus("nzbdav", false, reason, reason);
            }
            else if (await VerifyNzbdavReadinessAsync(pluginKey, nzbdavKey, cancellationToken).ConfigureAwait(false))
            {
                results["nzbdav"] = new SetupServiceStatus("nzbdav", true);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            results["nzbdav"] = new SetupServiceStatus("nzbdav", false, SetupReasonCodes.ProviderFailed, SetupReasonCodes.ProviderFailed);
        }

        ArrApiKeys? discovered = null;
        try { discovered = await DiscoverArrKeysAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (discovered is not null)
        {
            try
            {
                using var httpClient = _httpClientFactory();
                var arr = new ArrSetupClient(httpClient);
                var verified = await arr.VerifyManagedResourcesDetailedAsync(new ArrSetupOptions(
                    _environment.SonarrUrl, discovered.Sonarr,
                    _environment.RadarrUrl, discovered.Radarr,
                    _environment.NzbdavUrl, nzbdavKey ?? string.Empty), cancellationToken).ConfigureAwait(false);
                if (verified.SonarrReady)
                    results["sonarr"] = new SetupServiceStatus("sonarr", true);
                else
                    results["sonarr"] = new SetupServiceStatus("sonarr", false, SetupReasonCodes.SonarrFailed, SetupReasonCodes.SonarrFailed);
                if (verified.RadarrReady)
                    results["radarr"] = new SetupServiceStatus("radarr", true);
                else
                    results["radarr"] = new SetupServiceStatus("radarr", false, SetupReasonCodes.RadarrFailed, SetupReasonCodes.RadarrFailed);
            }
            catch (OperationCanceledException) { throw; }
            catch { }

            try
            {
                using var prowlarr = new ProwlarrSetupClient(_environment.ProwlarrUrl, discovered.Prowlarr);
                var configuration = SetupConfigurationData.FromStoredValues(
                    _configManager.GetSetupIndexers(), _configManager.GetSetupUsenetProviders());
                var prowlarrReady = await prowlarr.VerifyManagedResourcesAsync(new ProwlarrSetupOptions(
                    _environment.SonarrUrl, discovered.Sonarr,
                    _environment.RadarrUrl, discovered.Radarr,
                    configuration.Indexers), cancellationToken).ConfigureAwait(false);
                if (prowlarrReady)
                    results["prowlarr"] = new SetupServiceStatus("prowlarr", true);
                else
                    Log.Warning("Setup live verification failed: {Service} {Operation} {ErrorKind}",
                        "prowlarr", "managed-resources", "not-ready");
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                Log.Warning("Setup live verification failed: {Service} {Operation} {ErrorKind}",
                    "prowlarr", "managed-resources", "exception");
            }
        }

        // Completion removes the temporary admin session. The plugin key is
        // retained and is used as the bounded verification credential; a
        // missing key therefore fails closed instead of claiming Jellyfin is
        // ready from the marker alone.
        if (!string.IsNullOrWhiteSpace(pluginKey))
        {
            try
            {
                using var httpClient = _httpClientFactory();
                var jellyfin = new JellyfinSetupClient(
                    httpClient,
                    new JellyfinSetupOptions(_environment.JellyfinUrl, SetupManagedNames.Nzbdav, "setup")
                    {
                        LibraryPath = _environment.LibraryPath,
                        MoviesLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/movies",
                        TvLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/tv",
                        NzbdavUrl = _environment.NzbdavUrl,
                        PluginConfiguration = BuildJellyfinPluginConfiguration(pluginKey),
                        AssertMutationLeaseResultAsync = AssertJellyfinLeaseAsync,
                    });
                jellyfin.UseAccessToken(pluginKey);
                var verification = await jellyfin.VerifySetupAsync(pluginKey, cancellationToken).ConfigureAwait(false);
                if (verification.Ready && verification.ApiKeyActive && verification.PluginConfigured
                    && verification.LibrariesPresent && verification.SyncTaskPresent)
                    results["jellyfin"] = new SetupServiceStatus("jellyfin", true);
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return results;
    }

    public Task<SetupStatus> ConfigureAsync(SetupConfigurationData request, CancellationToken cancellationToken)
        => ConfigureAndRunAsync(request, string.Empty, "setup", cancellationToken);

    public Task<SetupStatus> ConfigureAndRunAsync(SetupConfigurationData request, CancellationToken cancellationToken)
        => ConfigureAndRunAsync(request, string.Empty, "setup", cancellationToken);

    public Task<SetupStatus> ConfigureAndRunAsync(
        SetupConfigurationData request,
        string grant,
        string purpose,
        CancellationToken cancellationToken)
        => RunWithSetupBudgetAsync(
            token =>
            {
                _leaseGrant = grant;
                _leasePurpose = purpose;
                return ConfigureAndRunCoreAsync(request, token);
            },
            cancellationToken);

    private async Task<SetupStatus> ConfigureAndRunCoreAsync(SetupConfigurationData request, CancellationToken cancellationToken)
    {
        EnsureFullStackEnabled();
        EnsureNotCompleted();
        await EnsureNoRevocationPendingAsync(cancellationToken).ConfigureAwait(false);

        await using var slot = await AcquireRunSlotAsync(cancellationToken).ConfigureAwait(false);
        if (!slot.Entered)
            throw new SetupRunBusyException();

        var steps = CreateInitialProgress();
        steps["provider-indexers"] = SetupStepState.Running;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        // Validation is deliberately before the first durable write. Invalid
        // Usenet credentials can therefore never become persisted or Ready.
        string? indexerWarning;
        try
        {
            indexerWarning = await ExecuteExternalMutationAsync(
                    () => ValidateSetupInputsAsync(request, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SetupValidationFailureException failure)
        {
            _progressReasons["provider-indexers"] = failure.ReasonCode;
            steps["provider-indexers"] = SetupStepState.Failed;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            return GetStatus();
        }
        catch (OperationCanceledException)
        {
            _progressReasons["provider-indexers"] = SetupReasonCodes.ValidationCanceled;
            steps["provider-indexers"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            _progressReasons["provider-indexers"] = SetupReasonCodes.ProviderFailed;
            steps["provider-indexers"] = SetupStepState.Failed;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            return GetStatus();
        }

        var runLease = _activeRunLease
                         ?? throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        await _persistence.SaveWithLeaseAsync(new SetupSecretValues
        {
            UsenetProviders = SetupConfigurationData.SerializeStoredProviders(request.Providers),
            Indexers = request.IndexerJson,
        }, completed: false, runLease, cancellationToken).ConfigureAwait(false);
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);

        if (indexerWarning is not null)
        {
            _progressReasons["provider-indexers"] = indexerWarning;
            steps["provider-indexers"] = SetupStepState.Warning;
        }
        else
        {
            steps["provider-indexers"] = SetupStepState.Complete;
        }
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        return await RunSetupAsync(request, steps, cancellationToken).ConfigureAwait(false);
    }

    public Task<SetupStatus> RunAsync(CancellationToken cancellationToken)
        => RunAsync(string.Empty, "setup", cancellationToken);

    public Task<SetupStatus> RunAsync(string grant, string purpose, CancellationToken cancellationToken)
        => RunWithSetupBudgetAsync(
            token =>
            {
                _leaseGrant = grant;
                _leasePurpose = purpose;
                return RunCoreAsync(token);
            },
            cancellationToken);

    public Task<SetupStatus> RunRepairAsync(string grant, CancellationToken cancellationToken)
        => RunWithSetupBudgetAsync(
            token =>
            {
                _leaseGrant = grant;
                _leasePurpose = SetupGrantConstants.RepairPurpose;
                return RunRepairCoreAsync(grant, token);
            },
            cancellationToken);

    private async Task<SetupStatus> RunRepairCoreAsync(string grant, CancellationToken cancellationToken)
    {
        EnsureFullStackEnabled();
        if (!_configManager.IsSetupCompleted() || !_configManager.IsSetupRepairRequired())
            throw new BadHttpRequestException("Setup repair is not currently required.");

        await using var slot = await AcquireRunSlotAsync(cancellationToken).ConfigureAwait(false);
        if (!slot.Entered) throw new SetupRunBusyException();
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
        var session = await _setupGrantService.ReadRepairSessionAsync(grant, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(session))
            throw new BadHttpRequestException("Repair grant is invalid.");

        try
        {
            var configuration = LoadStoredConfiguration();
            var discovered = await DiscoverArrKeysAsync(cancellationToken).ConfigureAwait(false);
            if (discovered is null) return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
            var prowlarrState = await ConfigureProwlarrAsync(discovered, configuration.Indexers, cancellationToken).ConfigureAwait(false);
            if (prowlarrState != SetupStepState.Complete)
                return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
            var arrState = await ConfigureArrClientsAsync(discovered, configuration.IndexerJson, cancellationToken).ConfigureAwait(false);
            if (arrState != SetupStepState.Complete)
                return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
            var jellyfinState = await ConfigureJellyfinAsync(cancellationToken, session).ConfigureAwait(false);
            if (jellyfinState != SetupStepState.Complete)
                return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
            var health = await ExecuteExternalMutationAsync(
                    () => VerifySetupAsync(
                        discovered,
                        _configManager.GetSetupPluginApiKey(),
                        configuration.IndexerJson,
                        cancellationToken,
                        session),
                    cancellationToken)
                .ConfigureAwait(false);
            if (health != SetupStepState.Complete)
                return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);

            // Keep the repair latch until the authenticated repair session has
            // been successfully logged out and its grant has been revoked. If
            // either cleanup step fails, recovery must still see repair state.
            await _setupGrantService.CompleteRepairAsync(
                    grant,
                    cancellationToken,
                    _activeRunLease)
                .ConfigureAwait(false);
            var lease = _activeRunLease
                ?? throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            await _persistence.SaveLiveHealthStateWithLeaseAsync(
                    true,
                    _timeProvider.GetUtcNow().UtcDateTime,
                    null,
                    null,
                    "{}",
                    SerializeCompleteProgress(),
                    lease,
                    cancellationToken)
                .ConfigureAwait(false);
            await _configManager.LoadConfig().ConfigureAwait(false);
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await _setupGrantService.RecoverRepairAsync(CancellationToken.None, _activeRunLease).ConfigureAwait(false);
            throw;
        }
        catch
        {
            return await RepairFailureAsync(grant, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SetupStatus> RepairFailureAsync(string grant, CancellationToken cancellationToken)
    {
        try { await _setupGrantService.RecoverRepairAsync(CancellationToken.None, _activeRunLease).ConfigureAwait(false); }
        catch { }
        await _configManager.LoadConfig().ConfigureAwait(false);
        // Do not perform a fresh health read on a failed repair. A coincidental
        // live response must not clear the repair latch or report Ready before
        // the scoped repair run has completed all managed-resource checks.
        return GetStatus();
    }

    private async Task<SetupStatus> RunCoreAsync(CancellationToken cancellationToken)
    {
        EnsureFullStackEnabled();
        EnsureNotCompleted();

        await using var slot = await AcquireRunSlotAsync(cancellationToken).ConfigureAwait(false);
        if (!slot.Entered)
            throw new SetupRunBusyException();

        var steps = LoadProgress();
        // Reasons are durable retry state, not an in-memory diagnostic cache.
        // Load them before the first progress write so Running cannot erase a
        // previous subsystem-specific warning/failure.
        _progressReasons = LoadProgressReasons();

        SetupConfigurationData configuration;
        try
        {
            configuration = LoadStoredConfiguration();
        }
        catch (BadHttpRequestException)
        {
            steps["provider-indexers"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
            throw;
        }

        string? indexerWarning;
        try
        {
            // Retry/recovery runs validate stored credentials again before any
            // step can become complete. This protects older or manually
            // altered rows from reaching Ready.
            indexerWarning = await ExecuteExternalMutationAsync(
                    () => ValidateSetupInputsAsync(configuration, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SetupValidationFailureException failure)
        {
            _progressReasons["provider-indexers"] = failure.ReasonCode;
            steps["provider-indexers"] = SetupStepState.Failed;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            return GetStatus();
        }
        catch (OperationCanceledException)
        {
            _progressReasons["provider-indexers"] = SetupReasonCodes.ValidationCanceled;
            steps["provider-indexers"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            _progressReasons["provider-indexers"] = SetupReasonCodes.ProviderFailed;
            steps["provider-indexers"] = SetupStepState.Failed;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            return GetStatus();
        }

        await EnsureNoRevocationPendingAsync(cancellationToken).ConfigureAwait(false);

        if (indexerWarning is not null)
        {
            _progressReasons["provider-indexers"] = indexerWarning;
            steps["provider-indexers"] = SetupStepState.Warning;
        }
        else
        {
            steps["provider-indexers"] = SetupStepState.Complete;
        }
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        return await RunSetupAsync(configuration, steps, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SetupStatus> RunSetupAsync(
        SetupConfigurationData configuration,
        Dictionary<string, SetupStepState> steps,
        CancellationToken cancellationToken)
    {
        // A complete progress document is only a resume hint. Every run and
        // retry re-discovers the upstream keys before it can complete.
        ArrApiKeys? discovered;
        try
        {
            discovered = await DiscoverArrKeysAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _progressReasons["arr-key-discovery"] = SetupReasonCodes.ValidationCanceled;
            steps["arr-key-discovery"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (discovered is null)
        {
            steps["arr-key-discovery"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
            return GetStatus();
        }

        steps["arr-key-discovery"] = SetupStepState.Complete;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        // A durable Complete state is only a resume hint. The managed
        // resources can drift after their phase was committed but before the
        // overall completion marker is written. Reconcile every external
        // service phase on every retry; each phase first performs a
        // non-resource-mutating persisted-resource verification so an already
        // healthy retry does not PUT/POST resources or rotate secrets
        // unnecessarily.
        var prowlarrWasComplete = steps["prowlarr"] == SetupStepState.Complete;
        steps["prowlarr"] = SetupStepState.Running;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
        SetupStepState prowlarrState;
        try
        {
            prowlarrState = await ReconcileProwlarrAsync(
                    discovered,
                    configuration.Indexers,
                    prowlarrWasComplete,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _progressReasons["prowlarr"] = SetupReasonCodes.ValidationCanceled;
            steps["prowlarr"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        steps["prowlarr"] = prowlarrState;
        if (prowlarrState != SetupStepState.Complete)
            _progressReasons["prowlarr"] = SetupReasonCodes.ProwlarrFailed;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        if (prowlarrState == SetupStepState.Failed)
            return GetStatus();

        var arrWasComplete = steps["arr-clients"] == SetupStepState.Complete;
        steps["arr-clients"] = SetupStepState.Running;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
        SetupStepState arrState;
        try
        {
            arrState = await ReconcileArrClientsAsync(
                    discovered,
                    configuration.IndexerJson,
                    arrWasComplete,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _progressReasons["arr-clients"] = SetupReasonCodes.ValidationCanceled;
            steps["arr-clients"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        steps["arr-clients"] = arrState;
        if (arrState != SetupStepState.Complete)
            _progressReasons["arr-clients"] = SetupReasonCodes.ArrFailed;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        if (arrState == SetupStepState.Failed)
            return GetStatus();

        var jellyfinWasComplete = steps["jellyfin"] == SetupStepState.Complete;
        steps["jellyfin"] = SetupStepState.Running;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
        SetupStepState jellyfinState;
        try
        {
            jellyfinState = await ReconcileJellyfinAsync(
                    jellyfinWasComplete,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _progressReasons["jellyfin"] = SetupReasonCodes.ValidationCanceled;
            steps["jellyfin"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        steps["jellyfin"] = jellyfinState;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        if (jellyfinState == SetupStepState.Failed)
            return GetStatus();

        // Health is never trusted from progress. This verifies the live Arr,
        // Prowlarr, Jellyfin/plugin, library and task state after restart even
        // when every prior step was marked complete.
        steps["health-check"] = SetupStepState.Running;
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);
        SetupStepState healthState;
        try
        {
            healthState = await ExecuteExternalMutationAsync(
                    () => VerifySetupAsync(
                        discovered,
                        _configManager.GetSetupPluginApiKey(),
                        configuration.IndexerJson,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _progressReasons["health-check"] = SetupReasonCodes.ValidationCanceled;
            steps["health-check"] = SetupStepState.Warning;
            await UpdateProgressAsync(steps, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        steps["health-check"] = healthState;
        if (healthState != SetupStepState.Complete
            && _progressReasons.TryGetValue("health-check", out var healthReason)
            && TryGetManagedPhaseForHealthReason(healthReason, out var affectedPhase))
        {
            // Keep the durable diagnostic on both the health check and the
            // phase whose managed resource was observed drifting. This makes
            // the first warning actionable and also survives a process restart
            // before the retry reconciles the phase.
            steps[affectedPhase] = SetupStepState.Warning;
            _progressReasons[affectedPhase] = healthReason;
        }
        await UpdateProgressAsync(steps, cancellationToken).ConfigureAwait(false);

        var allComplete = StepNames.All(step => steps[step] == SetupStepState.Complete);
        return allComplete
            ? await CompleteSetupAsync(configuration, cancellationToken).ConfigureAwait(false)
            : GetStatus();
    }

    private async Task<SetupStatus> CompleteSetupAsync(
        SetupConfigurationData configuration,
        CancellationToken cancellationToken)
    {
        // Retire the privileged Jellyfin session and write the completion
        // marker in one process-wide setup mutation domain. Renewal cannot
        // acquire a new grant between cleanup and the marker, and the marker
        // is never claimed if durable cleanup fails.
        try
        {
            await _configManager.WithMutationGateAsync(async () =>
            {
                await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
                // Phase A is durable before any external logout. If HTTP or
                // phase C fails, the exact operation remains retryable after a
                // completely fresh process restart.
                var plan = await _setupGrantService.PrepareCompletionLogoutUnderMutationGateAsync(
                        cancellationToken,
                        _activeRunLease)
                    .ConfigureAwait(false);
                await _setupGrantService.LogoutCompletionSessionsAsync(
                        plan,
                        SetupEnvironmentOptions.FromEnvironment().JellyfinUrl,
                        cancellationToken,
                        _activeRunLease)
                    .ConfigureAwait(false);
                await RequireRunLeaseAsync(CancellationToken.None).ConfigureAwait(false);
                await _setupGrantService.CompleteAfterExternalLogoutUnderMutationGateAsync(
                        plan,
                        configuration.IndexerJson,
                        _configManager.GetSetupPluginApiKey(),
                        _configManager.GetSetupPluginApiKeyPrevious(),
                        _configManager.GetSetupPluginApiKeyPreviousExpiresAtUtc()
                            ?.ToString("O", CultureInfo.InvariantCulture),
                        cancellationToken,
                        _activeRunLease)
                    .ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ReloadSetupCacheAfterCompletionAttemptAsync().ConfigureAwait(false);
            throw;
        }
        catch (SetupTransactionRecoveryRequiredException)
        {
            // The persistence boundary has already rolled back/disposed and
            // classified the outcome. Do not turn an uncertain completion into
            // a fabricated status or run compensating logout.
            await ReloadSetupCacheAfterCompletionAttemptAsync().ConfigureAwait(false);
            throw;
        }
        catch (JellyfinSetupException)
        {
            return await ReloadSetupStatusAfterCompletionAttemptAsync().ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            return await ReloadSetupStatusAfterCompletionAttemptAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await ReloadSetupStatusAfterCompletionAttemptAsync().ConfigureAwait(false);
        }

        return await ReloadSetupStatusAfterCompletionAttemptAsync().ConfigureAwait(false);
    }

    private async Task<SetupStatus> ReloadSetupStatusAfterCompletionAttemptAsync()
    {
        await ReloadSetupCacheAfterCompletionAttemptAsync().ConfigureAwait(false);
        return await GetStatusAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ReloadSetupCacheAfterCompletionAttemptAsync()
    {
        try
        {
            await _configManager.LoadConfig().ConfigureAwait(false);
        }
        catch
        {
            // The durable transaction outcome remains authoritative. If the
            // reload itself is unavailable, return the bounded in-memory state
            // rather than replacing a setup result with diagnostics.
        }
    }

    private async Task<bool> VerifyNzbdavReadinessAsync(
        string? pluginKey,
        string? apiKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pluginKey)
            || string.IsNullOrWhiteSpace(apiKey)
            || string.Equals(pluginKey, apiKey, StringComparison.Ordinal))
            return false;

        try
        {
            if (!await _dbClient.Ctx.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false))
                return false;

            // This is the same bounded store used by /api/manifest. The query
            // proves the live context can access manifest data while the
            // explicit version and plugin-key reads prove the local setup
            // contract is available without echoing either secret.
            _ = await _dbClient.Ctx.Items
                .AsNoTracking()
                .Where(item => item.Path.StartsWith("/content/"))
                .Select(item => item.Id)
                .Take(1)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(ConfigManager.AppVersion))
                return false;

            var persisted = await _dbClient.Ctx.ConfigItems
                .AsNoTracking()
                .Where(row => row.ConfigName == "api.key"
                           || row.ConfigName == SetupConfigKeys.PluginApiKey)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
            string? ReadPersisted(string key)
            {
                var row = persisted.SingleOrDefault(item => string.Equals(item.ConfigName, key, StringComparison.OrdinalIgnoreCase));
                if (row is null) return null;
                return row.IsEncrypted
                    ? _configManager.DecryptSetupValue(key, row.ConfigValue).Plaintext
                    : row.ConfigValue;
            }

            return string.Equals(ReadPersisted("api.key"), apiKey, StringComparison.Ordinal)
                   && string.Equals(ReadPersisted(SetupConfigKeys.PluginApiKey), pluginKey, StringComparison.Ordinal)
                   && !string.Equals(pluginKey, apiKey, StringComparison.Ordinal);
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private async Task<ArrApiKeys?> DiscoverArrKeysAsync(CancellationToken cancellationToken)
    {
        try
        {
            var sonarr = await _discovery.DiscoverAsync(ArrService.Sonarr, cancellationToken).ConfigureAwait(false);
            var radarr = await _discovery.DiscoverAsync(ArrService.Radarr, cancellationToken).ConfigureAwait(false);
            var prowlarr = await _discovery.DiscoverAsync(ArrService.Prowlarr, cancellationToken).ConfigureAwait(false);
            return new ArrApiKeys(sonarr.ApiKey, radarr.ApiKey, prowlarr.ApiKey);
        }
        catch (ArrConfigDiscoveryException)
        {
            return null;
        }
    }

    private async Task<SetupStepState> ReconcileProwlarrAsync(
        ArrApiKeys discovered,
        IReadOnlyList<ProwlarrNewznabIndexer> indexers,
        bool wasComplete,
        CancellationToken cancellationToken)
    {
        if (wasComplete)
        {
            var healthy = await ExecuteExternalMutationAsync(
                    () => VerifyProwlarrManagedResourcesAsync(discovered, indexers, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (healthy)
                return SetupStepState.Complete;
        }

        return await ExecuteExternalMutationAsync(
                () => ConfigureProwlarrAsync(discovered, indexers, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> VerifyProwlarrManagedResourcesAsync(
        ArrApiKeys discovered,
        IReadOnlyList<ProwlarrNewznabIndexer> indexers,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new ProwlarrSetupClient(
                _environment.ProwlarrUrl,
                discovered.Prowlarr);
            return await client.VerifyManagedResourcesAsync(
                    new ProwlarrSetupOptions(
                        _environment.SonarrUrl,
                        discovered.Sonarr,
                        _environment.RadarrUrl,
                        discovered.Radarr,
                        indexers,
                        prowlarrUrl: null,
                        forceSecretUpdate: false,
                        assertMutationLeaseAsync: RequireRunLeaseAsync),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SetupStepState> ReconcileArrClientsAsync(
        ArrApiKeys discovered,
        string indexerJson,
        bool wasComplete,
        CancellationToken cancellationToken)
    {
        if (wasComplete)
        {
            var healthy = await ExecuteExternalMutationAsync(
                    () => VerifyArrManagedResourcesAsync(discovered, cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (healthy)
                return SetupStepState.Complete;
        }

        return await ExecuteExternalMutationAsync(
                () => ConfigureArrClientsAsync(discovered, indexerJson, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> VerifyArrManagedResourcesAsync(
        ArrApiKeys discovered,
        CancellationToken cancellationToken)
    {
        var nzbdavApiKey = _configManager.GetApiKey();
        if (string.IsNullOrWhiteSpace(nzbdavApiKey))
            return false;

        try
        {
            using var httpClient = _httpClientFactory();
            var client = new ArrSetupClient(httpClient);
            var result = await client.VerifyManagedResourcesDetailedAsync(
                    new ArrSetupOptions(
                        _environment.SonarrUrl,
                        discovered.Sonarr,
                        _environment.RadarrUrl,
                        discovered.Radarr,
                        _environment.NzbdavUrl,
                        nzbdavApiKey,
                        AssertMutationLeaseAsync: RequireRunLeaseAsync),
                    cancellationToken)
                .ConfigureAwait(false);
            return result.SonarrReady && result.RadarrReady;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private async Task<SetupStepState> ReconcileJellyfinAsync(
        bool wasComplete,
        CancellationToken cancellationToken)
    {
        if (wasComplete && !string.IsNullOrWhiteSpace(_configManager.GetSetupPluginApiKey()))
        {
            var healthy = await ExecuteExternalMutationAsync(
                    () => VerifyJellyfinManagedResourcesAsync(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
            if (healthy)
                return SetupStepState.Complete;
        }

        return await ExecuteExternalMutationAsync(
                () => ConfigureJellyfinAsync(cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<bool> VerifyJellyfinManagedResourcesAsync(CancellationToken cancellationToken)
    {
        var pluginApiKey = _configManager.GetSetupPluginApiKey();
        if (string.IsNullOrWhiteSpace(pluginApiKey))
            return false;
        var jellyfinCredential = GetJellyfinSetupCredential();
        if (string.IsNullOrWhiteSpace(jellyfinCredential))
            return false;

        try
        {
            using var httpClient = _httpClientFactory();
            var jellyfin = new JellyfinSetupClient(
                httpClient,
                new JellyfinSetupOptions(_environment.JellyfinUrl, SetupManagedNames.Nzbdav, "setup")
                {
                    LibraryPath = _environment.LibraryPath,
                    MoviesLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/movies",
                    TvLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/tv",
                    NzbdavUrl = _environment.NzbdavUrl,
                    PluginConfiguration = BuildJellyfinPluginConfiguration(pluginApiKey),
                    AssertMutationLeaseResultAsync = AssertJellyfinLeaseAsync,
                });
            jellyfin.UseAccessToken(jellyfinCredential);
            var result = await jellyfin.VerifySetupAsync(pluginApiKey, cancellationToken).ConfigureAwait(false);
            return result.Ready
                && result.ApiKeyActive
                && result.PluginConfigured
                && result.LibrariesPresent
                && result.SyncTaskPresent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private string? GetJellyfinSetupCredential()
        // The temporary administrator session is preferred while it exists.
        // Once it is gone, the durable NZBDAV API key is the revocable setup
        // credential for retries; no Jellyfin password is retained or needed.
        => _configManager.GetSetupJellyfinApiKey()
           ?? _configManager.GetSetupPluginApiKey();

    private async Task<string?> GetJellyfinSetupCredentialAsync(CancellationToken cancellationToken)
        => await _persistence.ReadJellyfinApiKeyAsync(cancellationToken).ConfigureAwait(false)
           ?? GetJellyfinSetupCredential();

    private async Task<SetupStepState> ConfigureProwlarrAsync(
        ArrApiKeys discovered,
        IReadOnlyList<ProwlarrNewznabIndexer> indexers,
        CancellationToken cancellationToken)
    {
        try
        {
            using var client = new ProwlarrSetupClient(
                _environment.ProwlarrUrl,
                discovered.Prowlarr);
            await client.SetupAsync(
                    new ProwlarrSetupOptions(
                        _environment.SonarrUrl,
                        discovered.Sonarr,
                        _environment.RadarrUrl,
                        discovered.Radarr,
                        indexers,
                        prowlarrUrl: null,
                        forceSecretUpdate: true,
                        assertMutationLeaseAsync: RequireRunLeaseAsync),
                    cancellationToken)
                .ConfigureAwait(false);

            return SetupStepState.Complete;
        }
        catch (ProwlarrSetupConflictException)
        {
            // Known reconciliation collisions are non-fatal warnings.
            return SetupStepState.Warning;
        }
        catch (ProwlarrSetupProtocolException)
        {
            return SetupStepState.Warning;
        }
        catch (ProwlarrSetupTransportException)
        {
            return SetupStepState.Warning;
        }
        catch (ProwlarrSetupHttpException)
        {
            return SetupStepState.Warning;
        }
        catch (InvalidOperationException)
        {
            return SetupStepState.Warning;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return SetupStepState.Warning;
        }
    }

    private async Task<SetupStepState> ConfigureArrClientsAsync(
        ArrApiKeys discovered,
        string indexerJson,
        CancellationToken cancellationToken)
    {
        var nzbdavApiKey = _configManager.GetApiKey();
        if (string.IsNullOrWhiteSpace(nzbdavApiKey))
            return SetupStepState.Failed;

        var operation = ArrSetupOperation.ConfigureClients;
        try
        {
            using var httpClient = _httpClientFactory();
            var client = new ArrSetupClient(httpClient);
            await client.SetupAsync(
                    new ArrSetupOptions(
                        _environment.SonarrUrl,
                        discovered.Sonarr,
                        _environment.RadarrUrl,
                        discovered.Radarr,
                        _environment.NzbdavUrl,
                        nzbdavApiKey,
                        AssertMutationLeaseAsync: RequireRunLeaseAsync),
                    cancellationToken)
                .ConfigureAwait(false);

            var runLease = _activeRunLease
                             ?? throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            operation = ArrSetupOperation.PersistArrConfig;
            await PersistArrConfigAsync(discovered, runLease, cancellationToken).ConfigureAwait(false);
            operation = ArrSetupOperation.PersistIndexers;
            await _persistence.SaveWithLeaseAsync(new SetupSecretValues { Indexers = indexerJson }, completed: false, runLease, cancellationToken)
                .ConfigureAwait(false);
            operation = ArrSetupOperation.AssertLease;
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);

            return SetupStepState.Complete;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SetupTransactionRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception exception)
        {
            LogArrSetupFailure(operation, exception);
            return SetupStepState.Warning;
        }
    }

    private enum ArrSetupOperation
    {
        ConfigureClients,
        PersistArrConfig,
        PersistIndexers,
        AssertLease,
    }

    // Setup requests include credentials in headers and payloads. Diagnostics
    // deliberately keep only a stable operation, service identity, HTTP status
    // (when typed), and a bounded error category.
    private static void LogArrSetupFailure(ArrSetupOperation operation, Exception exception)
    {
        var statusCode = exception is ArrSetupHttpException httpException
            ? (int?)httpException.StatusCode
            : null;
        var errorKind = exception switch
        {
            ArrSetupConflictException => "conflict",
            ArrSetupHttpException => "http",
            HttpRequestException => "transport",
            TimeoutException => "timeout",
            InvalidOperationException => "invalid-operation",
            BadHttpRequestException => "lease",
            _ => "exception",
        };
        foreach (var service in new[] { "sonarr", "radarr" })
        {
            Log.Warning("Arr setup failed: {Service} {Operation} {HttpStatus} {ErrorKind}",
                service, operation, statusCode, errorKind);
        }
    }

    private async Task<SetupStepState> ConfigureJellyfinAsync(
        CancellationToken cancellationToken,
        string? repairSession = null)
    {
        // A handoff stores the temporary Jellyfin session in the durable
        // database before it updates ConfigManager's process cache. Resolve
        // the authoritative persisted value for this first setup run; falling
        // back to the cache preserves restart/idempotency behavior.
        var adminApiKey = repairSession ?? await GetJellyfinSetupCredentialAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(adminApiKey))
            return SetupStepState.Failed;

        using var httpClient = _httpClientFactory();
        var client = new JellyfinSetupClient(
            httpClient,
            new JellyfinSetupOptions(_environment.JellyfinUrl, SetupManagedNames.Nzbdav, "setup")
            {
                LibraryPath = _environment.LibraryPath,
                MoviesLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/movies",
                TvLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/tv",
                NzbdavUrl = _environment.NzbdavUrl,
                AssertMutationLeaseResultAsync = AssertJellyfinLeaseAsync,
            });
        client.UseAccessToken(adminApiKey);

        try
        {
            var runLease = _activeRunLease
                             ?? throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            // No Jellyfin write is allowed before this compatibility and
            // plugin-presence check succeeds.
            await client.CheckCompatibilityAsync(cancellationToken).ConfigureAwait(false);
            var pluginApiKey = await client.EnsureApiKeyAsync(cancellationToken).ConfigureAwait(false);
            var currentPluginApiKey = _configManager.GetSetupPluginApiKey();
            string? previousPluginApiKey = null;
            string? previousExpiresAt = null;

            if (!string.IsNullOrWhiteSpace(currentPluginApiKey)
                && !string.Equals(currentPluginApiKey, pluginApiKey, StringComparison.Ordinal))
            {
                previousPluginApiKey = currentPluginApiKey;
                previousExpiresAt = (_timeProvider.GetUtcNow().UtcDateTime + PluginApiKeyPreviousWindow)
                    .ToString("O", CultureInfo.InvariantCulture);
            }

            await _persistence.SaveWithLeaseAsync(new SetupSecretValues
            {
                PluginApiKey = pluginApiKey,
                PluginApiKeyPrevious = previousPluginApiKey,
                PluginApiKeyPreviousExpiresAt = previousExpiresAt,
            }, completed: false, runLease, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);

            await client.ConfigurePluginAsync(
                    BuildJellyfinPluginConfiguration(pluginApiKey),
                    cancellationToken)
                .ConfigureAwait(false);
            await client.UpsertLibrariesAsync(cancellationToken).ConfigureAwait(false);
            await client.TriggerSyncAsync(cancellationToken).ConfigureAwait(false);

            return SetupStepState.Complete;
        }
        catch (JellyfinSetupException exception)
        {
            Log.Warning("Jellyfin setup failed: {Failure} during {Operation} (HTTP {StatusCode})", exception.Failure, exception.Operation, exception.StatusCode);
            _progressReasons["jellyfin"] = exception.Failure switch
            {
                JellyfinSetupFailure.IncompatibleVersion => SetupReasonCodes.JellyfinVersionFailed,
                JellyfinSetupFailure.PluginMissing => SetupReasonCodes.PluginMismatch,
                JellyfinSetupFailure.LibraryCollision or JellyfinSetupFailure.LibraryTypeMismatch => SetupReasonCodes.LibraryCollision,
                JellyfinSetupFailure.SyncTaskMissing => SetupReasonCodes.TaskFailed,
                _ => SetupReasonCodes.PluginMismatch,
            };
            return SetupStepState.Failed;
        }
        catch (ArgumentException)
        {
            _progressReasons["jellyfin"] = SetupReasonCodes.JellyfinVersionFailed;
            return SetupStepState.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SetupTransactionRecoveryRequiredException)
        {
            throw;
        }
        catch (Exception)
        {
            _progressReasons["jellyfin"] = SetupReasonCodes.PluginMismatch;
            return SetupStepState.Failed;
        }
    }

    private async Task<SetupStepState> VerifySetupAsync(
        ArrApiKeys discovered,
        string? pluginApiKey,
        string indexerJson,
        CancellationToken cancellationToken,
        string? repairSession = null)
    {
        if (string.IsNullOrWhiteSpace(pluginApiKey))
        {
            _progressReasons["health-check"] = SetupReasonCodes.PluginMismatch;
            return SetupStepState.Warning;
        }

        try
        {
            try
            {
                using var arrHttpClient = _httpClientFactory();
                var arrSetup = new ArrSetupClient(arrHttpClient);
                var arrReady = await arrSetup.VerifyManagedResourcesDetailedAsync(new ArrSetupOptions(
                    _environment.SonarrUrl, discovered.Sonarr,
                    _environment.RadarrUrl, discovered.Radarr,
                    _environment.NzbdavUrl, _configManager.GetApiKey() ?? string.Empty,
                    AssertMutationLeaseAsync: RequireRunLeaseAsync), cancellationToken)
                    .ConfigureAwait(false);
                if (!arrReady.SonarrReady || !arrReady.RadarrReady)
                {
                    _progressReasons["health-check"] = !arrReady.SonarrReady
                        ? SetupReasonCodes.SonarrFailed
                        : SetupReasonCodes.RadarrFailed;
                    return SetupStepState.Warning;
                }
            }
            catch (HttpRequestException)
            {
                _progressReasons["health-check"] = SetupReasonCodes.ArrFailed;
                return SetupStepState.Warning;
            }

            var configuration = SetupConfigurationData.FromStoredValues(indexerJson, _configManager.GetSetupUsenetProviders());
            using (var prowlarr = new ProwlarrSetupClient(
                       _environment.ProwlarrUrl,
                       discovered.Prowlarr))
            {
                var prowlarrHealthy = await prowlarr.VerifyManagedResourcesAsync(
                    new ProwlarrSetupOptions(
                        _environment.SonarrUrl, discovered.Sonarr,
                        _environment.RadarrUrl, discovered.Radarr,
                        configuration.Indexers,
                        prowlarrUrl: null,
                        forceSecretUpdate: false,
                        assertMutationLeaseAsync: RequireRunLeaseAsync),
                    cancellationToken).ConfigureAwait(false);
                if (!prowlarrHealthy)
                {
                    _progressReasons["health-check"] = SetupReasonCodes.ProwlarrFailed;
                    return SetupStepState.Warning;
                }
            }

            var adminApiKey = repairSession ?? GetJellyfinSetupCredential();
            if (string.IsNullOrWhiteSpace(adminApiKey))
            {
                _progressReasons["health-check"] = SetupReasonCodes.JellyfinVersionFailed;
                return SetupStepState.Warning;
            }

            using var httpClient = _httpClientFactory();
            var jellyfin = new JellyfinSetupClient(
                httpClient,
                new JellyfinSetupOptions(_environment.JellyfinUrl, SetupManagedNames.Nzbdav, "setup")
                {
                    LibraryPath = _environment.LibraryPath,
                    MoviesLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/movies",
                    TvLibraryPath = _environment.LibraryPath.TrimEnd('/') + "/tv",
                    NzbdavUrl = _environment.NzbdavUrl,
                    PluginConfiguration = BuildJellyfinPluginConfiguration(pluginApiKey),
                    AssertMutationLeaseResultAsync = AssertJellyfinLeaseAsync,
                });
            jellyfin.UseAccessToken(adminApiKey);

            var result = await jellyfin.VerifySetupAsync(pluginApiKey, cancellationToken).ConfigureAwait(false);
            if (result.Ready && result.ApiKeyActive && result.PluginConfigured
                && result.LibrariesPresent && result.SyncTaskPresent)
                return SetupStepState.Complete;

            _progressReasons["health-check"] = !result.PluginConfigured || !result.ApiKeyActive
                ? SetupReasonCodes.PluginMismatch
                : !result.LibrariesPresent
                    ? SetupReasonCodes.LibraryFailed
                    : !result.SyncTaskPresent
                        ? SetupReasonCodes.TaskFailed
                        : SetupReasonCodes.BackendUnavailable;
            return SetupStepState.Warning;
        }
        catch (HttpRequestException)
        {
            _progressReasons["health-check"] = SetupReasonCodes.BackendUnavailable;
            return SetupStepState.Warning;
        }
        catch (InvalidOperationException)
        {
            _progressReasons["health-check"] = SetupReasonCodes.BackendUnavailable;
            return SetupStepState.Warning;
        }
        catch (JellyfinSetupException exception)
        {
            _progressReasons["health-check"] = exception.Failure switch
            {
                JellyfinSetupFailure.IncompatibleVersion => SetupReasonCodes.JellyfinVersionFailed,
                JellyfinSetupFailure.PluginMissing => SetupReasonCodes.PluginMismatch,
                JellyfinSetupFailure.LibraryCollision or JellyfinSetupFailure.LibraryTypeMismatch => SetupReasonCodes.LibraryCollision,
                JellyfinSetupFailure.SyncTaskMissing => SetupReasonCodes.TaskFailed,
                _ => SetupReasonCodes.BackendUnavailable,
            };
            return SetupStepState.Warning;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            _progressReasons["health-check"] = SetupReasonCodes.BackendUnavailable;
            return SetupStepState.Warning;
        }
    }

    private async Task<bool> CheckProwlarrHealthAsync(string apiKey, CancellationToken cancellationToken)
    {
        using var httpClient = _httpClientFactory();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            _environment.ProwlarrUrl.TrimEnd('/') + "/api/v1/system/status");
        request.Headers.Add("X-Api-Key", apiKey);

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Validates setup inputs. Usenet provider failures are fatal because nothing
    /// works without them. Indexer capability failures are only advisory: Prowlarr
    /// owns indexer management and runs its own test, so a bad Newznab URL or key
    /// must not block the rest of setup. Returns a warning reason code, or null.
    /// </summary>
    private async Task<string?> ValidateSetupInputsAsync(SetupConfigurationData request, CancellationToken cancellationToken)
    {
        var providerValidation = RunBoundedAsync(
            request.Providers.Providers,
            provider => _usenetCredentialValidator.ValidateAsync(provider, cancellationToken),
            maxConcurrency: 4,
            cancellationToken);
        var indexerValidation = RunBoundedAsync(
            request.Indexers,
            indexer => _indexerCapabilityValidator(
                new NewznabIndexerCredential(indexer.Name, indexer.BaseUrl, indexer.ApiKey, indexer.AllowPrivateNetwork),
                cancellationToken),
            maxConcurrency: 4,
            cancellationToken);
        await Task.WhenAll(providerValidation, indexerValidation).ConfigureAwait(false);
        var providerResults = await providerValidation.ConfigureAwait(false);
        var indexerResults = await indexerValidation.ConfigureAwait(false);

        var providerFailure = providerResults.FirstOrDefault(result => !result.Valid);
        if (providerFailure is not null)
        {
            var reason = providerFailure.Code == UsenetCredentialValidationCode.AuthenticationFailed
                ? SetupReasonCodes.ProviderAuthFailed
                : SetupReasonCodes.ProviderFailed;
            throw new SetupValidationFailureException(reason);
        }

        var indexerFailures = indexerResults.Where(result => result.Status != NewznabCapabilityStatus.Valid).ToArray();
        if (indexerFailures.Length == 0)
            return null;

        foreach (var failure in indexerFailures)
            Log.Warning("Indexer capability check failed: {Indexer} {Status} (HTTP {StatusCode}). " +
                "Setup continues; Prowlarr manages this indexer.",
                failure.DisplayName, failure.Status, failure.HttpStatusCode);
        return SetupReasonCodes.IndexerCapabilityFailed;
    }

    private async Task<NewznabCapabilityResult> ValidateIndexerCapabilityAsync(
        NewznabIndexerCredential credential,
        CancellationToken cancellationToken)
    {
        // NewznabCapabilityClient owns the DNS-pinned transport boundary. The
        // factory client is intentionally only the orchestration-owned lifetime
        // hook; the capability client creates its own pinned connection after
        // resolving and approving the endpoint.
        using var httpClient = _httpClientFactory();
        var client = new NewznabCapabilityClient(
            httpClient,
            TimeSpan.FromSeconds(10),
            hostResolver: _capabilityHostResolver,
            pinResolvedConnections: true);
        return await client.CheckAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<TResult>> RunBoundedAsync<TItem, TResult>(
        IReadOnlyList<TItem> items,
        Func<TItem, Task<TResult>> operation,
        int maxConcurrency,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(maxConcurrency, maxConcurrency);
        var tasks = items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await operation(item).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task PersistArrConfigAsync(
        ArrApiKeys discovered,
        SetupRunLeaseHandle runLease,
        CancellationToken cancellationToken)
    {
        await _configManager.WithMutationGateAsync(async () =>
        {
            // Read the operator-owned Arr document only after entering the
            // shared gate. An administrator update that commits while setup is
            // validating must therefore be the source for this merge.
            var existingConfig = _configManager.GetArrConfig();
            existingConfig.RadarrInstances = MergeArrInstances(existingConfig.RadarrInstances, _environment.RadarrUrl, discovered.Radarr);
            existingConfig.SonarrInstances = MergeArrInstances(existingConfig.SonarrInstances, _environment.SonarrUrl, discovered.Sonarr);
            // Queue rules are operator-owned. Setup changes only its exact
            // managed instances and preserves the collection/order verbatim.
            // PrepareForStorage returns encrypted storage copies. Retain the
            // plaintext source separately because UpdateValuesNoLock is the
            // cache publication boundary and encrypts sensitive inputs itself.
            var cacheValues = new List<ConfigItem>
            {
                new()
                {
                    ConfigName = "arr.instances",
                    ConfigValue = JsonSerializer.Serialize(existingConfig),
                },
            };
            var prepared = _configManager.PrepareForStorage(cacheValues);

            var transaction = await _dbClient.Ctx.Database
                .BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                .ConfigureAwait(false);
            var lifetime = new SetupPersistenceTransaction(transaction);

            try
            {
                await SetupMutationFenceLock.AcquireAsync(_dbClient.Ctx, transaction, cancellationToken)
                    .ConfigureAwait(false);
                if (!await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
                    throw new BadHttpRequestException("Another setup worker owns the active setup run.");
                var rows = await _dbClient.Ctx.ConfigItems
                    .AsNoTracking()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);

                var requestedName = GetLogicalName(prepared[0].ConfigName);
                var duplicates = new Dictionary<string, ConfigItem>(StringComparer.Ordinal);
                foreach (var row in rows)
                {
                    var logical = GetLogicalName(row.ConfigName);
                    if (!string.Equals(logical, requestedName, StringComparison.Ordinal))
                        continue;

                    if (!duplicates.TryAdd(logical, row))
                        throw new InvalidOperationException($"Duplicate managed config key '{requestedName}' exists with multiple casings.");
                }

                var current = duplicates.TryGetValue(requestedName, out var currentRow)
                    ? currentRow
                    : null;
                var requested = prepared[0];

                if (current is null)
                {
                    _dbClient.Ctx.ConfigItems.Add(CloneConfigItem(requested));
                }
                else if (!string.Equals(current.ConfigName, requested.ConfigName, StringComparison.Ordinal))
                {
                    _dbClient.Ctx.ConfigItems.Remove(current);
                    _dbClient.Ctx.ConfigItems.Add(CloneConfigItem(requested));
                }
                else
                {
                    _dbClient.Ctx.ConfigItems.Update(current);
                    current.ConfigValue = requested.ConfigValue;
                    current.IsEncrypted = requested.IsEncrypted;
                }

                await _dbClient.Ctx.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                if (!await runLease.AssertCurrentWithinTransactionAsync(transaction, cancellationToken).ConfigureAwait(false))
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
            finally
            {
                foreach (var entry in _dbClient.Ctx.ChangeTracker.Entries<ConfigItem>().ToList())
                    entry.State = EntityState.Detached;
            }

            if (!await runLease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false))
                throw new BadHttpRequestException("Another setup worker owns the active setup run.");
            _configManager.UpdateValuesNoLock(cacheValues);
        }, cancellationToken).ConfigureAwait(false);
    }

    private SetupConfigurationData LoadStoredConfiguration()
    {
        return SetupConfigurationData.FromStoredValues(
            _configManager.GetSetupIndexers(),
            _configManager.GetSetupUsenetProviders());
    }

    private Dictionary<string, SetupStepState> LoadProgress()
        => LoadProgress(_configManager.GetSetupRunProgress());

    private Dictionary<string, SetupStepState> LoadProgress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return CreateInitialProgress();

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SetupStepState>>(raw, RunProgressOptions)
                         ?? CreateInitialProgress();
            foreach (var step in StepNames.Where(step => !parsed.ContainsKey(step)))
                parsed[step] = SetupStepState.Pending;

            foreach (var key in parsed.Keys.ToArray())
            {
                if (!StepNames.Contains(key, StringComparer.Ordinal))
                    parsed.Remove(key);
            }

            return parsed;
        }
        catch (JsonException)
        {
            return CreateInitialProgress();
        }
    }

    private Dictionary<string, string> LoadProgressReasons()
        => LoadProgressReasons(_configManager.GetSetupRunProgressReasons());

    private Dictionary<string, string> LoadProgressReasons(string? raw)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(raw) ?? [];
            foreach (var entry in parsed)
            {
                if (StepNames.Contains(entry.Key, StringComparer.Ordinal)
                    && IsSafeReason(entry.Value))
                    result[entry.Key] = entry.Value;
            }
        }
        catch (JsonException)
        {
            // A corrupt diagnostic document must never block setup. State is
            // loaded independently and receives bounded fallback codes.
        }

        return result;
    }

    private async Task UpdateProgressAsync(Dictionary<string, SetupStepState> steps, CancellationToken cancellationToken)
    {
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
        foreach (var step in StepNames)
            steps.TryAdd(step, SetupStepState.Pending);

        foreach (var step in StepNames)
        {
            if (steps[step] == SetupStepState.Complete)
                _progressReasons.Remove(step);
            else if (steps[step] is SetupStepState.Warning or SetupStepState.Failed
                     && !_progressReasons.ContainsKey(step))
                _progressReasons[step] = DefaultReasonFor(step);
            // Pending/running deliberately retain a loaded reason until the
            // step succeeds. This makes the first retry write lossless.
        }

        var ordered = StepNames.ToDictionary(step => step, step => steps[step], StringComparer.Ordinal);
        var serialized = JsonSerializer.Serialize(ordered, RunProgressOptions);
        var reasons = JsonSerializer.Serialize(_progressReasons);
        var lease = _activeRunLease
            ?? throw new BadHttpRequestException("Another setup worker owns the active setup run.");
        await _persistence.SaveWithLeaseAsync(new SetupSecretValues
        {
            RunProgress = serialized,
            RunProgressReasons = reasons,
        }, completed: false, lease, cancellationToken).ConfigureAwait(false);
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, SetupStepState> CreateInitialProgress()
        => StepNames.ToDictionary(name => name, _ => SetupStepState.Pending, StringComparer.Ordinal);

    private static string SerializeCompleteProgress()
        => JsonSerializer.Serialize(
            StepNames.ToDictionary(name => name, _ => SetupStepState.Complete, StringComparer.Ordinal),
            RunProgressOptions);

    private static List<ArrConfig.ConnectionDetails> MergeArrInstances(
        List<ArrConfig.ConnectionDetails> current,
        string url,
        string apiKey)
    {
        var normalized = NormalizeUrl(url);
        var withoutCurrent = current.Where(item => !string.Equals(item.Host, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        withoutCurrent.Add(new ArrConfig.ConnectionDetails
        {
            Host = normalized,
            ApiKey = apiKey,
        });

        return withoutCurrent;
    }

    private static string NormalizeUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        return (uri.GetLeftPart(UriPartial.Authority) + uri.AbsolutePath).TrimEnd('/') ;
    }

    private static string GetLogicalName(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            return canonicalName;

        return configName;
    }

    private JsonObject BuildJellyfinPluginConfiguration(string pluginApiKey)
        => new JsonObject
        {
            ["NzbdavUrl"] = _environment.NzbdavUrl,
            // Jellyfin plugin builds before the URL-contract rename deserialize
            // NzbdavBaseUrl. Keep this wire alias until every deployed plugin
            // accepts NzbdavUrl; both values target the internal backend port.
            ["NzbdavBaseUrl"] = _environment.NzbdavUrl,
            ["ApiKey"] = pluginApiKey,
            ["LibraryPath"] = _environment.LibraryPath,
        };

    private void EnsureFullStackEnabled()
    {
        if (!_environment.FullStackEnabled)
            throw new BadHttpRequestException(_environment.ValidationError ?? "Full-stack setup is disabled.");
    }

    private void EnsureNotCompleted()
    {
        if (_configManager.IsSetupCompleted())
            throw new BadHttpRequestException("Setup is already completed.");
    }

    private async Task EnsureNoRevocationPendingAsync(CancellationToken cancellationToken)
    {
        if (await _setupGrantService.IsRevocationPendingAsync(cancellationToken).ConfigureAwait(false))
            throw new BadHttpRequestException("Setup session revocation is pending; recover cleanup before continuing.");
    }

    private async Task<SetupStatus> RunWithSetupBudgetAsync(
        Func<CancellationToken, Task<SetupStatus>> operation,
        CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var timer = _timeProvider.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            budget,
            SetupOperationTimeout,
            Timeout.InfiniteTimeSpan);
        return await operation(budget.Token).ConfigureAwait(false);
    }

    private static SetupStepStatus CreateStepStatus(
        string name,
        SetupStepState state,
        string? reasonCode,
        bool finalHealthReady)
    {
        var reason = state is SetupStepState.Warning or SetupStepState.Failed
            ? reasonCode ?? DefaultReasonFor(name)
            : null;
        bool? serviceReady = name switch
        {
            "provider-indexers" or "prowlarr" or "arr-clients" or "jellyfin" or "health-check"
                => state == SetupStepState.Complete && finalHealthReady,
            _ => null,
        };
        return new SetupStepStatus(name, state, reason, reason, serviceReady);
    }

    private static string DefaultReasonFor(string name)
        => name switch
        {
            "provider-indexers" => SetupReasonCodes.ProviderFailed,
            "arr-key-discovery" => SetupReasonCodes.ArrFailed,
            "prowlarr" => SetupReasonCodes.ProwlarrFailed,
            "arr-clients" => SetupReasonCodes.ArrFailed,
            "jellyfin" => SetupReasonCodes.PluginMismatch,
            "health-check" => SetupReasonCodes.BackendUnavailable,
            _ => SetupReasonCodes.Unknown,
        };

    private static bool TryGetManagedPhaseForHealthReason(string reason, out string phase)
    {
        phase = reason switch
        {
            SetupReasonCodes.ProwlarrFailed => "prowlarr",
            SetupReasonCodes.SonarrFailed or SetupReasonCodes.RadarrFailed or SetupReasonCodes.ArrFailed => "arr-clients",
            SetupReasonCodes.JellyfinVersionFailed
                or SetupReasonCodes.PluginMismatch
                or SetupReasonCodes.LibraryCollision
                or SetupReasonCodes.LibraryFailed
                or SetupReasonCodes.TaskFailed
                or SetupReasonCodes.JellyfinFailed => "jellyfin",
            _ => string.Empty,
        };
        return phase.Length != 0;
    }

    private static void MarkUnverifiedCompletedPhases(
        Dictionary<string, SetupStepState> progress,
        Dictionary<string, string> reasons)
    {
        // A durable completion marker means these phases were attempted. A
        // missing progress document must not turn that historical attempt back
        // into a fabricated Pending state after restart. Preserve any durable
        // failure/warning and only classify absent or interrupted phase state as
        // an unverified warning, leaving the explicit live verification command
        // responsible for upgrading it to Complete.
        foreach (var step in StepNames.Where(step => step != "health-check"))
        {
            if (progress[step] is not (SetupStepState.Pending or SetupStepState.Running))
                continue;

            progress[step] = SetupStepState.Warning;
            if (!reasons.ContainsKey(step))
                reasons[step] = DefaultCompletedPhaseReasonFor(step);
        }
    }

    private static string DefaultCompletedPhaseReasonFor(string name)
        => name switch
        {
            "provider-indexers" => SetupReasonCodes.ProviderFailed,
            "arr-key-discovery" => SetupReasonCodes.ArrFailed,
            "prowlarr" => SetupReasonCodes.ProwlarrFailed,
            "arr-clients" => SetupReasonCodes.ArrFailed,
            "jellyfin" => SetupReasonCodes.JellyfinFailed,
            _ => SetupReasonCodes.Unknown,
        };

    private static bool IsSafeReason(string value)
        => value.Length <= 64
           && value.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    private IReadOnlyList<SetupServiceStatus> CreateServiceStatuses(
        IReadOnlyDictionary<string, SetupStepState> progress,
        bool ready = false,
        IReadOnlyDictionary<string, string>? persistedReasons = null,
        bool repairRequired = false)
    {
        // Ready here means only a recent exact persisted POST verification;
        // stale completion markers remain Checking until that command runs.
        // Use durable phase diagnostics for a repair snapshot so a restart does
        // not replace a live drift reason with a generic service-failed token.
        var reasons = repairRequired
            ? persistedReasons ?? LoadProgressReasons()
            : new Dictionary<string, string>(StringComparer.Ordinal);
        var sonarrReason = _configManager.GetSetupSonarrRepairReason()
                           ?? reasons.GetValueOrDefault("arr-clients")
                           ?? SetupReasonCodes.SonarrFailed;
        var radarrReason = _configManager.GetSetupRadarrRepairReason()
                           ?? reasons.GetValueOrDefault("arr-clients")
                           ?? SetupReasonCodes.RadarrFailed;
        var nzbdavReason = reasons.GetValueOrDefault("provider-indexers") ?? SetupReasonCodes.NzbdavFailed;
        var jellyfinReason = reasons.GetValueOrDefault("jellyfin") ?? SetupReasonCodes.JellyfinFailed;
        var prowlarrReason = reasons.GetValueOrDefault("prowlarr") ?? SetupReasonCodes.ProwlarrFailed;
        return
        [
            new SetupServiceStatus("nzbdav", ready, ready ? null : nzbdavReason, ready ? null : nzbdavReason),
            new SetupServiceStatus("jellyfin", ready, ready ? null : jellyfinReason, ready ? null : jellyfinReason),
            new SetupServiceStatus("sonarr", ready, ready ? null : sonarrReason, ready ? null : sonarrReason),
            new SetupServiceStatus("radarr", ready, ready ? null : radarrReason, ready ? null : radarrReason),
            new SetupServiceStatus("prowlarr", ready, ready ? null : prowlarrReason, ready ? null : prowlarrReason),
        ];
    }

    private static ConfigItem CloneConfigItem(ConfigItem source)
        => new()
        {
            ConfigName = source.ConfigName,
            ConfigValue = source.ConfigValue,
            IsEncrypted = source.IsEncrypted,
        };

    private async Task<RunSlot> AcquireRunSlotAsync(CancellationToken cancellationToken)
    {
        // The database lease is the sole ownership decision. In particular,
        // independent services in this process must exercise the same path as
        // services in different processes; a static semaphore cannot prove
        // generation ownership. The only wait is a tiny, cancellable handoff
        // boundary retry while a grant issuance/renewal lease is still live.
        // It never waits for a normal setup owner and never takes an
        // unexpired lease.
        for (var attempt = 0; ; attempt++)
        {
            var lease = await _runLeaseService.TryAcquireAsync(
                    _leaseGrant,
                    _leasePurpose,
                    cancellationToken)
                .ConfigureAwait(false);
            if (lease is not null)
            {
                _activeRunLease = lease;
                return new RunSlot(true, lease, this);
            }

            if (attempt >= GrantLeaseReleaseRetryCount
                || !await _runLeaseService.IsActiveGrantOperationAsync(cancellationToken).ConfigureAwait(false))
                return new RunSlot(false, null);

            await Task.Delay(GrantLeaseReleaseRetryDelay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequireRunLeaseAsync(CancellationToken cancellationToken)
    {
        var lease = _activeRunLease;
        if (lease is null || !await lease.HeartbeatAsync(cancellationToken).ConfigureAwait(false))
            throw new SetupRunBusyException();
    }

    /// <summary>
    /// Keeps the database lease alive across bounded upstream work and checks
    /// the exact owner/generation immediately on both sides of the mutation.
    /// The process-local run gate is intentionally not used as an ownership
    /// proof; the heartbeat is backed by the shared lease row.
    /// </summary>
    private async Task<bool> AssertJellyfinLeaseAsync(CancellationToken cancellationToken)
    {
        var lease = _activeRunLease;
        return lease is not null && await lease.AssertCurrentAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ExecuteExternalMutationAsync(
        Func<Task> mutation,
        CancellationToken cancellationToken)
    {
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatWhileExternalWorkAsync(heartbeatStop.Token);
        Exception? failure = null;
        try
        {
            await mutation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal way to stop the heartbeat.
            }
            catch (Exception exception) when (failure is null)
            {
                failure = exception;
            }

            // This post-check is deliberately in finally. It covers upstream
            // errors, provider commit-then-throw responses, and normal
            // completion; a stale worker cannot continue with a later write.
            try
            {
                await RequireRunLeaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (failure is null)
            {
                failure = exception;
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private async Task<T> ExecuteExternalMutationAsync<T>(
        Func<Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatWhileExternalWorkAsync(heartbeatStop.Token);
        Exception? failure = null;
        T? result = default;
        try
        {
            result = await mutation().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            heartbeatStop.Cancel();
            try
            {
                await heartbeat.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is the normal way to stop the heartbeat.
            }
            catch (Exception exception) when (failure is null)
            {
                failure = exception;
            }

            try
            {
                await RequireRunLeaseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (failure is null)
            {
                failure = exception;
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return result!;
    }

    private async Task HeartbeatWhileExternalWorkAsync(CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(5);
        while (true)
        {
            await Task.Delay(interval, _timeProvider, cancellationToken).ConfigureAwait(false);
            await RequireRunLeaseAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record ArrApiKeys(string Sonarr, string Radarr, string Prowlarr);

    private sealed class RunSlot(bool entered, SetupRunLeaseHandle? lease, SetupOrchestrationService? owner = null) : IAsyncDisposable, IDisposable
    {
        public bool Entered { get; } = entered;
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (!Entered) return;
            if (lease is not null) _ = lease.ReleaseAsync(CancellationToken.None);
            if (owner is not null && ReferenceEquals(owner._activeRunLease, lease))
                owner._activeRunLease = null;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            if (!Entered) return;
            if (lease is not null)
            {
                try { await lease.ReleaseAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            }
            if (owner is not null && ReferenceEquals(owner._activeRunLease, lease))
                owner._activeRunLease = null;
        }
    }
}
