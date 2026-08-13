using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class StreamTokenService
{
    private const int DefaultExpiryMinutes = 7 * 24 * 60;
    private const int MaxTokenFutureSeconds = 7 * 24 * 60 * 60;
    private const int ExpiredTokenGraceSeconds = 7 * 24 * 60 * 60;
    private const int MaxLegacyMigrationDays = 14;
    private const int MaxPreviousKeyOverlapSeconds = 14 * 24 * 60 * 60;
    private const int HmacByteCount = 32;
    private const int HexSignatureLength = HmacByteCount * 2;
    private const int MaxDecimalExpiryLength = 19;
    private const int Base64UrlSignatureLength = 43;
    private const string ReadMethodToken = "GET";

    public static string GenerateToken(string path, ConfigManager configManager, string method = ReadMethodToken,
        int expiryMinutes = DefaultExpiryMinutes, TimeProvider? timeProvider = null)
    {
        var expiry = (timeProvider ?? TimeProvider.System).GetUtcNow().AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var canonicalPath = NormalizePath(path);
        var canonicalMethod = NormalizeMethod(method);
        var payload = $"{canonicalMethod}:{expiry}:{canonicalPath}";
        return $"{expiry}.{ToCanonicalBase64Url(ComputeHmacBytes(payload, configManager.GetStrmKey()))}";
    }

    public static bool ValidateToken(string token, string path, ConfigManager configManager, string method = ReadMethodToken,
        TimeProvider? timeProvider = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeSeconds();
        var state = configManager.GetStreamTokenStateSnapshot();
        var canonicalPath = NormalizePath(path);
        var canonicalMethod = NormalizeMethod(method);

        if (TryParseCurrentToken(token, out var expiry, out var signatureBytes)
            && IsTokenTimeValid(expiry, now))
        {
            var payload = $"{canonicalMethod}:{expiry}:{canonicalPath}";
            if (ConstantTimeSignatureMatch(state.Current, payload, signatureBytes))
                return true;

            foreach (var previous in state.PreviousKeys)
            {
                if (previous.ExpiresAt <= DateTimeOffset.FromUnixTimeSeconds(now)
                    || previous.ExpiresAt - DateTimeOffset.FromUnixTimeSeconds(now) > TimeSpan.FromSeconds(MaxPreviousKeyOverlapSeconds)
                    || DateTimeOffset.FromUnixTimeSeconds(expiry) > previous.ExpiresAt)
                    continue;

                if (ConstantTimeSignatureMatch(previous.Key, payload, signatureBytes))
                    return true;
            }
        }

        // The deployed pre-strm-key token has exactly two fields:
        // expiry.signatureHex. Its HMAC input is method:expiry:path, using the
        // old api.key. It is accepted only during the persisted migration
        // window; it is never treated as a current-token alternative.
        if (!TryParseLegacyToken(token, out var legacyExpiry, out var legacySignature)
            || !IsWithinLegacyWindow(state.LegacyMigrationStart, now)
            || !IsTokenTimeValid(legacyExpiry, now))
            return false;

        var legacyPayload = $"{canonicalMethod}:{legacyExpiry}:{canonicalPath}";
        return ConstantTimeSignatureMatch(configManager.GetApiKey(), legacyPayload, legacySignature);
    }

    private static bool IsTokenTimeValid(long expiry, long now)
        => expiry >= now - ExpiredTokenGraceSeconds && expiry <= now + MaxTokenFutureSeconds;

    private static bool IsWithinLegacyWindow(DateTimeOffset? start, long nowUnixSeconds)
    {
        if (start is null)
            return false;

        var now = DateTimeOffset.FromUnixTimeSeconds(nowUnixSeconds);
        return start.Value <= now && start.Value.AddDays(MaxLegacyMigrationDays) >= now;
    }

    private static bool ConstantTimeSignatureMatch(string key, string payload, byte[] signatureBytes)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                ComputeHmacBytes(payload, key), signatureBytes);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryParseCurrentToken(string token, out long expiry, out byte[] signatureBytes)
    {
        expiry = 0;
        signatureBytes = new byte[HmacByteCount];
        if (!TryParseTwoFields(token, out var expiryText, out var signatureText)
            || !TryParseStrictDecimal(expiryText, out expiry)
            || expiry <= 0
            || signatureText.Length != Base64UrlSignatureLength)
            return false;

        return TryParseCanonicalBase64UrlSignature(signatureText, signatureBytes);
    }

    private static bool TryParseLegacyToken(string token, out long expiry, out byte[] signatureBytes)
    {
        expiry = 0;
        signatureBytes = Array.Empty<byte>();
        if (!TryParseTwoFields(token, out var expiryText, out var signatureText)
            || !TryParseStrictDecimal(expiryText, out expiry)
            || expiry <= 0
            || signatureText.Length != HexSignatureLength)
            return false;

        try
        {
            signatureBytes = Convert.FromHexString(signatureText);
            return signatureBytes.Length == HmacByteCount;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryParseTwoFields(string token, out string expiry, out string signature)
    {
        expiry = string.Empty;
        signature = string.Empty;
        if (string.IsNullOrEmpty(token))
            return false;

        var separator = token.IndexOf('.');
        if (separator <= 0 || separator != token.LastIndexOf('.') || separator == token.Length - 1)
            return false;

        expiry = token[..separator];
        signature = token[(separator + 1)..];
        return true;
    }

    private static bool TryParseStrictDecimal(string value, out long result)
    {
        result = 0;
        if (value.Length == 0 || value.Length > MaxDecimalExpiryLength
            || (value.Length > 1 && value[0] == '0'))
            return false;

        foreach (var character in value)
            if (character is < '0' or > '9')
                return false;

        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    }

    private static byte[] ComputeHmacBytes(string payload, string key)
        => HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload));

    private static string ToCanonicalBase64Url(byte[] input)
        => Convert.ToBase64String(input).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryParseCanonicalBase64UrlSignature(string signature, byte[] output)
    {
        Span<int> decoded = stackalloc int[Base64UrlSignatureLength];
        for (var i = 0; i < decoded.Length; i++)
        {
            decoded[i] = FromBase64UrlChar(signature[i]);
            if (decoded[i] < 0)
                return false;
        }

        if ((decoded[^1] & 0b11) != 0)
            return false;

        var outputIndex = 0;
        for (var i = 0; i < 40; i += 4)
        {
            output[outputIndex++] = (byte)((decoded[i] << 2) | (decoded[i + 1] >> 4));
            output[outputIndex++] = (byte)(((decoded[i + 1] & 0x0F) << 4) | (decoded[i + 2] >> 2));
            output[outputIndex++] = (byte)(((decoded[i + 2] & 0x03) << 6) | decoded[i + 3]);
        }

        output[outputIndex++] = (byte)((decoded[40] << 2) | (decoded[41] >> 4));
        output[outputIndex++] = (byte)(((decoded[41] & 0x0F) << 4) | (decoded[42] >> 2));
        return outputIndex == HmacByteCount;
    }

    private static int FromBase64UrlChar(char value)
        => value is >= 'A' and <= 'Z' ? value - 'A'
            : value is >= 'a' and <= 'z' ? value - 'a' + 26
            : value is >= '0' and <= '9' ? value - '0' + 52
            : value is '-' ? 62
            : value is '_' ? 63
            : -1;

    private static string NormalizeMethod(string method)
        => method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
           || method.Equals(ReadMethodToken, StringComparison.OrdinalIgnoreCase)
            ? ReadMethodToken
            : method.ToUpperInvariant();

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var normalized = path.StartsWith('/') ? path : "/" + path;
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return "/";

        var normalizedPath = new StringBuilder(path.Length);
        foreach (var segment in segments)
            normalizedPath.Append('/').Append(Uri.EscapeDataString(Uri.UnescapeDataString(segment)));
        return normalizedPath.Length == 0 ? "/" : normalizedPath.ToString();
    }
}
