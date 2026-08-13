using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Setup.Core;

/// <summary>
/// Stable, non-secret Jellyfin setup identity derived from the durable
/// operation intent. It is reused for same-device reauthentication and never
/// contains a bearer token, password, timestamp, or process randomness.
/// </summary>
internal static class SetupAuthenticationIdentity
{
    internal const string ClientName = "NzbDav Setup";
    internal const string DeviceName = "NzbDav Setup";

    internal static string DeviceId(string operationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationId);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(operationId));
        return "nzbdav-setup-" + Convert.ToHexString(digest)[..24].ToLowerInvariant();
    }
}
