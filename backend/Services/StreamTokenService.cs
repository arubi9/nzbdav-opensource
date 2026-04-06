using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class StreamTokenService
{
    // 7-day expiry: .strm files persist on Jellyfin filesystem.
    // Tokens are refreshed by the sync task when older than 3 days.
    private const int DefaultExpiryMinutes = 10080; // 7 days

    // Grace period: accept tokens that expired within the last DefaultExpiryMinutes.
    // This allows old and new tokens to coexist during rotation,
    // preventing active stream interruption when .strm files are refreshed.
    private static readonly long GracePeriodSeconds = DefaultExpiryMinutes * 60L;

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

            if (!long.TryParse(parts[0], out var claimedExpiry)) return false;

            // Accept tokens within their validity window OR within one grace period.
            // Grace period is the server-side constant GracePeriodSeconds, NOT the
            // token's claimed expiry. The HMAC binds the claimed expiry to the
            // server's key, so an attacker cannot extend the window by manipulation.
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (now > claimedExpiry + GracePeriodSeconds) return false;

            var payload = $"{method}:{claimedExpiry}:{path}";
            var expectedHmac = ComputeHmac(payload, configManager.GetApiKey());

            // Compare HMAC hex strings — fixed length (SHA256 = 64 hex chars).
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
