using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class StreamTokenService
{
    private const int DefaultExpiryMinutes = 60;

    public static string GenerateToken(string path, ConfigManager configManager, int expiryMinutes = DefaultExpiryMinutes)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var payload = $"{expiry}:{path}";
        var hmac = ComputeHmac(payload, configManager.GetApiKey());
        return $"{expiry}.{hmac}";
    }

    public static bool ValidateToken(string token, string path, ConfigManager configManager)
    {
        try
        {
            var parts = token.Split('.', 2);
            if (parts.Length != 2) return false;

            if (!long.TryParse(parts[0], out var expiry)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;

            var payload = $"{expiry}:{path}";
            var expectedHmac = ComputeHmac(payload, configManager.GetApiKey());

            var expectedBytes = Encoding.UTF8.GetBytes(expectedHmac);
            var actualBytes = Encoding.UTF8.GetBytes(parts[1]);
            if (expectedBytes.Length != actualBytes.Length) return false;

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
