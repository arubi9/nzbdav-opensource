using System.Security.Cryptography;

namespace NzbWebDAV.Setup.Core;

public enum SetupGrantScope
{
    Normal,
    Repair,
}

public static class SetupGrantConstants
{
    public const string HeaderName = "x-setup-grant";
    public static readonly TimeSpan DefaultGrantLifetime = TimeSpan.FromMinutes(15);
    public const string GrantHashDomain = "nzbdav-setup-grant:v1";
    public const string NormalPurpose = "setup";
    public const string RepairPurpose = "repair";
}

public sealed record SetupGrantResult(string Grant, DateTime ExpiresAtUtc)
{
    public SetupGrantScope Scope { get; init; } = SetupGrantScope.Normal;
    /// <summary>
    /// Indicates that the newly issued grant is usable, but the previous
    /// Jellyfin session still needs bounded cleanup and recovery.
    /// </summary>
    public bool RevocationPending { get; init; }

    /// <summary>
    /// A display-safe warning. It never contains an upstream exception or
    /// credential.
    /// </summary>
    public string? Warning { get; init; }
}

public static class SetupGrantCrypto
{
    public static string GenerateToken(int byteCount = 32)
    {
        return Base64UrlEncode(RandomNumberGenerator.GetBytes(byteCount));
    }

    public static string Hash(string grant)
    {
        var material = System.Text.Encoding.UTF8.GetBytes($"{SetupGrantConstants.GrantHashDomain}\u0000{grant}");
        return Convert.ToHexString(SHA256.HashData(material)).ToLowerInvariant();
    }

    public static string ComputeIssuedTokenHash(string grant) => Hash(grant);

    public static byte[] DecodeHexHash(string hash)
        => Convert.FromHexString(hash);

    public static bool ConstantTimeEquals(string actualHash, string expectedHash)
    {
        try
        {
            var actual = Convert.FromHexString(actualHash);
            var expected = Convert.FromHexString(expectedHash);
            return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
