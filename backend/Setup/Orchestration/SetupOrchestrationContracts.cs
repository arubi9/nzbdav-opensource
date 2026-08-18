using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;
using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Clients.ProwlarrSetup;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Setup.Orchestration;

public sealed record SetupProviderRequest(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("useSsl")] bool UseSsl,
    [property: JsonPropertyName("user")] string User,
    [property: JsonPropertyName("pass")] string Pass,
    [property: JsonPropertyName("maxConnections")] int MaxConnections);

public sealed record SetupNewznabIndexerRequest(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("baseUrl")] string BaseUrl,
    [property: JsonPropertyName("apiKey")] string ApiKey,
    [property: JsonPropertyName("appProfileId")] int? AppProfileId = null,
    [property: JsonPropertyName("allowPrivateNetwork")] bool AllowPrivateNetwork = false);

public sealed record SetupConfigurationBody(
    [property: JsonPropertyName("providers")] IReadOnlyList<SetupProviderRequest> Providers,
    [property: JsonPropertyName("indexers")] IReadOnlyList<SetupNewznabIndexerRequest> Indexers);

public sealed class SetupConfigurationData
{
    private SetupConfigurationData(UsenetProviderConfig providers, IReadOnlyList<ProwlarrNewznabIndexer> indexers, string indexerJson)
    {
        Providers = providers;
        Indexers = indexers;
        IndexerJson = indexerJson;
    }

    public UsenetProviderConfig Providers { get; }
    public IReadOnlyList<ProwlarrNewznabIndexer> Indexers { get; }
    public string IndexerJson { get; }

    public static async Task<SetupConfigurationData> ParseAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var payload = await ReadJsonOrThrowAsync(context, cancellationToken).ConfigureAwait(false);
        return ConvertFromBody(payload, SerializeIndexers(payload.Indexers));
    }

    public static string SerializeStoredProviders(UsenetProviderConfig providers)
        => JsonSerializer.Serialize(providers, StoredSetupOptions);

    public static SetupConfigurationData FromStoredValues(string? indexerJson, string? providerJson)
    {
        if (string.IsNullOrWhiteSpace(indexerJson))
            throw new BadHttpRequestException("Setup indexers are missing.");
        if (string.IsNullOrWhiteSpace(providerJson))
            throw new BadHttpRequestException("Usenet providers are missing.");

        IReadOnlyList<SetupProviderRequest> providers;
        IReadOnlyList<SetupNewznabIndexerRequest> indexers;

        try
        {
            providers = ReadStoredProviders(providerJson);
            indexers = ReadStoredIndexers(indexerJson);
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Stored setup configuration is invalid.");
        }

        return ConvertFromBody(new SetupConfigurationBody(providers, indexers), indexerJson);
    }

    private static SetupConfigurationData ConvertFromBody(SetupConfigurationBody body, string indexerJson)
    {
        if (body is null || body.Providers is null || body.Indexers is null)
            throw new BadHttpRequestException("Setup providers and indexers are required.");
        ValidateCount("provider", body.Providers.Count, MaxProviders);
        ValidateCount("indexer", body.Indexers.Count, MaxIndexers);

        var providers = new UsenetProviderConfig { Providers = [] };
        foreach (var provider in body.Providers)
        {
            if (provider is null) throw new BadHttpRequestException("Setup provider is invalid.");
            providers.Providers.Add(ParseProvider(provider));
        }

        var indexers = new List<ProwlarrNewznabIndexer>(body.Indexers.Count);
        foreach (var indexer in body.Indexers)
        {
            if (indexer is null) throw new BadHttpRequestException("Setup indexer is invalid.");
            indexers.Add(ParseIndexer(indexer));
        }

        if (providers.Providers.Count == 0)
            throw new BadHttpRequestException("No usenet providers were configured.");
        if (indexers.Count == 0)
            throw new BadHttpRequestException("No indexers were configured.");

        return new SetupConfigurationData(providers, indexers, indexerJson);
    }

    public static ProwlarrNewznabIndexer ParseIndexer(SetupNewznabIndexerRequest request)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        var baseUrl = request.BaseUrl?.Trim() ?? string.Empty;
        var apiKey = request.ApiKey?.Trim() ?? string.Empty;

        ValidateRequiredText("indexer name", name, maxLength: 128);
        ValidateRequiredText("indexer api key", apiKey, maxLength: 512);
        if (string.IsNullOrWhiteSpace(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment) || ContainsSecretQueryParameter(parsed.Query)
            || !NzbWebDAV.Clients.Newznab.NewznabUrlPolicy.IsAllowed(parsed, request.AllowPrivateNetwork))
            throw new BadHttpRequestException("Indexer URL is invalid.");

        return new ProwlarrNewznabIndexer(
            name,
            parsed.ToString(),
            apiKey,
            appProfileId: request.AppProfileId,
            allowPrivateNetwork: request.AllowPrivateNetwork);
    }

    private static bool ContainsSecretQueryParameter(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = Uri.UnescapeDataString(pair.Split('=', 2)[0]);
            if (key.Equals("apikey", StringComparison.OrdinalIgnoreCase)
                || key.Equals("api-key", StringComparison.OrdinalIgnoreCase)
                || key.Equals("password", StringComparison.OrdinalIgnoreCase)
                || key.Equals("token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static UsenetProviderConfig.ConnectionDetails ParseProvider(SetupProviderRequest request)
    {
        var host = request.Host?.Trim() ?? string.Empty;
        var user = request.User?.Trim() ?? string.Empty;
        var pass = request.Pass ?? string.Empty;

        ValidateRequiredText("provider host", host, maxLength: 255);
        ValidateRequiredText("provider username", user, maxLength: 128);
        ValidateRequiredText("provider password", pass, maxLength: 256);

        if (request.Port is < 1 or > 65535)
            throw new BadHttpRequestException("Provider port is invalid.");
        if (request.MaxConnections is < 1 or > 10_000)
            throw new BadHttpRequestException("Provider max-connections is invalid.");

        if (!Enum.TryParse(request.Type, true, out ProviderType type) || type == ProviderType.Disabled)
            throw new BadHttpRequestException("Provider type is invalid.");

        return new UsenetProviderConfig.ConnectionDetails
        {
            Type = type,
            Host = host,
            Port = request.Port,
            UseSsl = request.UseSsl,
            User = user,
            Pass = pass,
            MaxConnections = request.MaxConnections,
        };
    }

    private static async Task<SetupConfigurationBody> ReadJsonOrThrowAsync(HttpContext context, CancellationToken cancellationToken)
    {
        if (!IsJsonContentType(context.Request.ContentType))
            throw new BadHttpRequestException("Setup configuration requires application/json.");
        if (context.Request.Body.CanSeek && context.Request.Body.Length > MaxPayloadBytes)
            throw new BadHttpRequestException("Setup request payload is too large.");

        var json = await ReadRequestBodyAsync(context.Request.Body, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
            throw new BadHttpRequestException("Setup request payload is required.");

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                MaxDepth = MaxPayloadJsonDepth,
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new BadHttpRequestException("Setup request payload must be an object.");
            ValidateFieldNames(document.RootElement, new HashSet<string>(["providers", "indexers"], StringComparer.Ordinal));
            if (!document.RootElement.TryGetProperty("providers", out var providers)
                || !document.RootElement.TryGetProperty("indexers", out var indexers)
                || providers.ValueKind != JsonValueKind.Array
                || indexers.ValueKind != JsonValueKind.Array)
                throw new BadHttpRequestException("Setup providers and indexers are required.");
            foreach (var provider in providers.EnumerateArray())
            {
                if (provider.ValueKind != JsonValueKind.Object)
                    throw new BadHttpRequestException("Setup provider is invalid.");
                ValidateFieldNames(provider, new HashSet<string>(["type", "host", "port", "useSsl", "user", "pass", "maxConnections"], StringComparer.Ordinal));
            }
            foreach (var indexer in indexers.EnumerateArray())
            {
                if (indexer.ValueKind != JsonValueKind.Object)
                    throw new BadHttpRequestException("Setup indexer is invalid.");
                ValidateFieldNames(indexer, new HashSet<string>(["name", "baseUrl", "apiKey", "appProfileId", "allowPrivateNetwork"], StringComparer.Ordinal));
            }
            return JsonSerializer.Deserialize<SetupConfigurationBody>(json, DefaultOptions)
                ?? throw new BadHttpRequestException("Setup request payload is invalid.");
        }
        catch (JsonException)
        {
            throw new BadHttpRequestException("Setup request payload must be valid JSON.");
        }
    }

    private static async Task<string> ReadRequestBodyAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream(Math.Min(4096, MaxPayloadBytes));
        var buffer = new byte[8192];

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            if (output.Length + read > MaxPayloadBytes)
                throw new BadHttpRequestException("Setup request payload is too large.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private static IReadOnlyList<SetupProviderRequest> ReadStoredProviders(string providerJson)
    {
        var stored = JsonSerializer.Deserialize<UsenetProviderConfig>(providerJson, StoredSetupOptions);
        if (stored is not null && stored.Providers is { Count: > 0 })
            return [.. stored.Providers.Select(ConvertStoredProvider)];

        var body = JsonSerializer.Deserialize<SetupConfigurationBody>(providerJson, DefaultOptions);
        if (body is { Providers.Count: > 0 })
            return body.Providers;

        var providersOnly = TryReadProvidersOnly(providerJson)
            ?? throw new BadHttpRequestException("Stored provider configuration is invalid.");

        if (providersOnly.Count == 0)
            throw new BadHttpRequestException("No usenet providers were configured.");

        return providersOnly;
    }

    private static IReadOnlyList<SetupProviderRequest> TryReadProvidersOnly(string providerJson)
    {
        try
        {
            using var document = JsonDocument.Parse(providerJson, new JsonDocumentOptions { MaxDepth = MaxPayloadJsonDepth });
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("providers", out var providersNode))
                return JsonSerializer.Deserialize<List<SetupProviderRequest>>(providersNode, DefaultOptions) ?? [];

            if (root.ValueKind == JsonValueKind.Array)
                return JsonSerializer.Deserialize<List<SetupProviderRequest>>(providerJson, DefaultOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }

        return [];
    }

    private static IReadOnlyList<SetupNewznabIndexerRequest> ReadStoredIndexers(string indexerJson)
    {
        using var document = JsonDocument.Parse(indexerJson, new JsonDocumentOptions { MaxDepth = MaxPayloadJsonDepth });
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            return JsonSerializer.Deserialize<List<SetupNewznabIndexerRequest>>(indexerJson, DefaultOptions)
                   ?? [];
        }

        var wrapped = JsonSerializer.Deserialize<SetupConfigurationBody>(indexerJson, DefaultOptions);
        return wrapped?.Indexers ?? [];
    }

    private static void ValidateFieldNames(JsonElement objectElement, IReadOnlySet<string> allowed)
    {
        foreach (var property in objectElement.EnumerateObject())
            if (!allowed.Contains(property.Name))
                throw new BadHttpRequestException("Setup request contains an unknown property.");
    }

    private static bool IsJsonContentType(string? contentType)
        => !string.IsNullOrWhiteSpace(contentType)
           && contentType.Split(';', 2)[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase)
           && (!contentType.Contains(';') || contentType[(contentType.IndexOf(';') + 1)..].Trim().Equals("charset=utf-8", StringComparison.OrdinalIgnoreCase));

    private static string SerializeIndexers(IReadOnlyList<SetupNewznabIndexerRequest> indexers)
        => JsonSerializer.Serialize(indexers, DefaultOptions);

    private static SetupProviderRequest ConvertStoredProvider(UsenetProviderConfig.ConnectionDetails provider)
    {
        return new SetupProviderRequest(provider.Type.ToString(), provider.Host, provider.Port, provider.UseSsl, provider.User,
            provider.Pass, provider.MaxConnections);
    }

    private static void ValidateCount(string kind, int count, int maxCount)
    {
        if (count < 1 || count > maxCount)
            throw new BadHttpRequestException($"{kind} count must be between 1 and {maxCount}.");
    }

    private static void ValidateRequiredText(string field, string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new BadHttpRequestException($"{field} is invalid.");
    }

    public const int MaxProviders = 32;
    public const int MaxIndexers = 100;
    private const int MaxPayloadBytes = 256 * 1024;
    private const int MaxPayloadJsonDepth = 64;

    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = MaxPayloadJsonDepth,
    };

    private static readonly JsonSerializerOptions StoredSetupOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        MaxDepth = MaxPayloadJsonDepth,
        Converters =
        {
            new JsonStringEnumConverter(),
        },
    };
}
