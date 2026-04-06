using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public sealed class StreamTokenService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultTokenLifetime = TimeSpan.FromMinutes(5);

    private readonly ConfigManager _configManager;
    private readonly Func<DateTimeOffset> _utcNow;

    public StreamTokenService(ConfigManager configManager, Func<DateTimeOffset>? utcNow = null)
    {
        _configManager = configManager;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public string GenerateToken(string path, TimeSpan? lifetime = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is required.", nameof(path));

        var tokenLifetime = lifetime ?? DefaultTokenLifetime;
        if (tokenLifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Token lifetime must be positive.");

        var payload = new TokenPayload
        {
            Path = path,
            ExpiresAtUtcTicks = _utcNow().Add(tokenLifetime).UtcTicks
        };

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        var signatureBytes = Sign(payloadBytes);
        return $"{Base64UrlEncode(payloadBytes)}.{Base64UrlEncode(signatureBytes)}";
    }

    public bool ValidateToken(string? token, string path)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(path))
            return false;

        var separatorIndex = token.IndexOf('.');
        if (separatorIndex <= 0 || separatorIndex >= token.Length - 1 || token.IndexOf('.', separatorIndex + 1) >= 0)
            return false;

        var payloadPart = token[..separatorIndex];
        var signaturePart = token[(separatorIndex + 1)..];

        if (!Base64UrlTryDecode(payloadPart, out var payloadBytes) || !Base64UrlTryDecode(signaturePart, out var signatureBytes))
            return false;

        var expectedSignature = Sign(payloadBytes);
        if (expectedSignature.Length != signatureBytes.Length || !CryptographicOperations.FixedTimeEquals(expectedSignature, signatureBytes))
            return false;

        TokenPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<TokenPayload>(payloadBytes, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (payload == null)
            return false;

        var now = _utcNow();
        return string.Equals(payload.Path, path, StringComparison.Ordinal)
               && payload.ExpiresAtUtcTicks >= now.UtcTicks;
    }

    private byte[] Sign(byte[] payloadBytes)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_configManager.GetApiKey()));
        return hmac.ComputeHash(payloadBytes);
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static bool Base64UrlTryDecode(string input, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var padded = input.Replace('-', '+').Replace('_', '/');
        var padding = padded.Length % 4;
        if (padding > 0)
            padded = padded.PadRight(padded.Length + (4 - padding), '=');

        try
        {
            bytes = Convert.FromBase64String(padded);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private sealed record TokenPayload
    {
        public required string Path { get; init; }
        public required long ExpiresAtUtcTicks { get; init; }
    }
}
