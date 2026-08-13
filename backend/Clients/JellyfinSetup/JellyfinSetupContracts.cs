using System.Text.Json.Nodes;

namespace NzbWebDAV.Clients.JellyfinSetup;

public sealed class JellyfinSetupOptions
{
    public JellyfinSetupOptions(string baseUrl, string username, string password)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("The Jellyfin URL must be an absolute HTTP URL.", nameof(baseUrl));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("A username is required.", nameof(username));
        BaseUrl = uri.ToString().TrimEnd('/');
        Username = username;
        Password = password ?? throw new ArgumentNullException(nameof(password));
    }

    public string BaseUrl { get; }
    public string Username { get; }
    public string Password { get; }
    public string ApiKeyAppName { get; init; } = "NZBDAV";
    public string ClientName { get; init; } = "NzbDav Setup";
    public string DeviceName { get; init; } = "NzbDav";
    public string DeviceId { get; init; } = "nzbdav-setup";
    public string ClientVersion { get; init; } = "1.0";
    public string PluginId { get; init; } = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    public string PluginName { get; init; } = "NZBDAV";
    public string RequiredJellyfinVersionPrefix { get; init; } = "10.11.";
    public string SyncTaskKey { get; init; } = "NzbdavLibrarySync";
    /// <summary>The root path retained by the plugin; libraries use the two child paths below.</summary>
    public string LibraryPath { get; init; } = "/media/nzbdav";
    public string MoviesLibraryPath { get; init; } = "/media/nzbdav/movies";
    public string TvLibraryPath { get; init; } = "/media/nzbdav/tv";
    /// <summary>The URL retained by the plugin configuration contract.</summary>
    public string NzbdavUrl { get; init; } = "http://nzbdav:8080";
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public JsonObject PluginConfiguration { get; init; } = new();
    /// <summary>Optional orchestration lease fence checked around every modifying request.</summary>
    // Legacy callback remains source-compatible for non-orchestrated callers;
    // setup orchestration must use the result-bearing fence below.
    public Func<CancellationToken, Task>? AssertMutationLeaseAsync { get; init; }
    public Func<CancellationToken, Task<bool>>? AssertMutationLeaseResultAsync { get; init; }
}

public enum JellyfinSetupFailure
{
    NotReady,
    Unauthorized,
    IncompatibleVersion,
    PluginMissing,
    InvalidResponse,
    LibraryCollision,
    LibraryTypeMismatch,
    SyncTaskMissing,
    UpstreamFailure
}

/// <summary>Contains only a safe operation and HTTP status; response bodies and credentials are excluded.</summary>
public sealed class JellyfinSetupException : Exception
{
    public JellyfinSetupException(
        JellyfinSetupFailure failure,
        string operation,
        int? statusCode = null,
        Exception? inner = null,
        bool sessionCreated = false)
        : base(statusCode is null ? $"Jellyfin setup failed during {operation} ({failure})." : $"Jellyfin setup failed during {operation} ({failure}, HTTP {statusCode}).", inner)
    {
        Failure = failure;
        Operation = operation;
        StatusCode = statusCode;
        SessionCreated = sessionCreated;
    }

    public JellyfinSetupFailure Failure { get; }
    public string Operation { get; }
    public int? StatusCode { get; }
    public bool SessionCreated { get; }
}

public sealed record JellyfinAuthenticationResult(string AccessToken, bool IsAdministrator = false);
public sealed record JellyfinCompatibilityResult(string ServerVersion, bool PluginInstalled, string? PluginVersion);
public sealed record JellyfinSetupResult(string ApiKey, JellyfinCompatibilityResult Compatibility);
public sealed record JellyfinVerificationResult(bool Ready, bool ApiKeyActive, bool PluginConfigured, bool LibrariesPresent, bool SyncTaskPresent);
