namespace NzbWebDAV.Clients.Newznab;

/// <summary>Operator-supplied Newznab endpoint credentials. This input is never returned by the capability client.</summary>
public sealed class NewznabIndexerCredential
{
    public NewznabIndexerCredential(string displayName, string baseUrl, string apiKey, bool allowPrivateNetwork = false)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            throw new ArgumentException("An indexer display name is required.", nameof(displayName));
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(char.IsControl) || apiKey.Length > 512)
            throw new ArgumentException("An API key is required.", nameof(apiKey));
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !NewznabUrlPolicy.IsAllowed(uri, allowPrivateNetwork)
            || !string.IsNullOrEmpty(uri.Query) && ContainsSecretQueryParameter(uri.Query))
            throw new ArgumentException("The Newznab URL does not satisfy the network or credential policy.", nameof(baseUrl));

        DisplayName = displayName;
        BaseUrl = uri;
        ApiKey = apiKey;
        AllowPrivateNetwork = allowPrivateNetwork;
    }

    public NewznabIndexerCredential(string displayName, Uri baseUrl, string apiKey, bool allowPrivateNetwork = false)
        : this(displayName, baseUrl?.ToString() ?? throw new ArgumentNullException(nameof(baseUrl)), apiKey, allowPrivateNetwork) { }

    public string DisplayName { get; }
    public Uri BaseUrl { get; }
    public string ApiKey { get; }
    /// <summary>Explicit setup-only opt-in for RFC1918/ULA indexers.</summary>
    public bool AllowPrivateNetwork { get; }

    private static bool ContainsSecretQueryParameter(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=', 2)[0];
            if (string.Equals(Uri.UnescapeDataString(key), "apikey", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "api-key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

public enum NewznabCapabilityStatus
{
    Valid,
    InvalidCredentials,
    MalformedResponse,
    Unreachable,
    TimedOut,
    HttpError,
    UnsafeNetworkAddress
}

/// <summary>A redaction-safe capability outcome: it contains no endpoint, query, response, or credential.</summary>
public sealed record NewznabCapabilityResult(
    string DisplayName,
    NewznabCapabilityStatus Status,
    int? HttpStatusCode = null);
