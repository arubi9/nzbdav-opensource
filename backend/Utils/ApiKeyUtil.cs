using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Utils;

public static class ApiKeyUtil
{
    // Keep both comparisons on every call. In particular, do not let a match
    // against the user key skip evaluation of the independently rotated
    // internal key.
    public static bool MatchesConfiguredKeys(string providedKey, string userKey, string? internalKey)
    {
        var providedBytes = Encoding.UTF8.GetBytes(providedKey ?? string.Empty);
        var userBytes = Encoding.UTF8.GetBytes(userKey ?? string.Empty);
        var internalBytes = Encoding.UTF8.GetBytes(internalKey ?? string.Empty);
        var matchesUserKey = CryptographicOperations.FixedTimeEquals(providedBytes, userBytes);
        var matchesInternalKey = CryptographicOperations.FixedTimeEquals(providedBytes, internalBytes);
        // Still perform both comparisons when a key is absent, but an empty
        // request must never authenticate as an absent/empty configuration.
        return !string.IsNullOrEmpty(providedKey)
               & (matchesUserKey | (!string.IsNullOrEmpty(internalKey) & matchesInternalKey));
    }
}
