using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class StreamTokenService
{
    // 24h expiry: Jellyfin caches MediaSourceInfo for the entire playback session.
    // A 60-minute token would break 3-hour movies on seek after the first hour.
    private const int DefaultExpiryMinutes = 1440;

    public static string GenerateToken(
        string path,
        ConfigManager configManager,
        string method = "GET",
        int expiryMinutes = DefaultExpiryMinutes)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var payload = $"{method}:{expiry}:{path}";
        var hmac = ComputeHmac(payload, configManager.GetApiKey());
        return $"{expiry}.{hmac}";
    }

    public static bool ValidateToken(string token, string path, ConfigManager configManager, string method = "GET")
    {
        try
        {
            var parts = token.Split('.', 2);
            if (parts.Length != 2) return false;

            if (!long.TryParse(parts[0], out var expiry)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;

            var payload = $"{method}:{expiry}:{path}";
            var expectedHmac = ComputeHmac(payload, configManager.GetApiKey());

            // Compare HMAC hex strings — fixed length (SHA256 = 64 hex chars)
            // so FixedTimeEquals always compares equal-length buffers.
            var expectedBytes = Encoding.UTF8.GetBytes(expectedHmac);
            var actualBytes = Encoding.UTF8.GetBytes(parts[1]);
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHmac(string payload, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
