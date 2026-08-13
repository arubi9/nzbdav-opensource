using System.Net;
using System.Net.Sockets;

namespace NzbWebDAV.Clients.Newznab;

/// <summary>One policy boundary for operator supplied Newznab URLs.</summary>
internal static class NewznabUrlPolicy
{
    public static bool IsAllowed(Uri uri, bool allowPrivateNetwork, IReadOnlyCollection<IPAddress>? resolvedAddresses = null)
    {
        if (uri is null || !uri.IsAbsoluteUri
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Fragment)
            || ContainsSecretQueryParameter(uri.Query)
            || LooksLikeAmbiguousNumericHost(uri.Host))
            return false;

        if (IPAddress.TryParse(uri.Host, out var literal))
        {
            // Plain HTTP is only an explicit opt-in for a literal LAN/VPN
            // endpoint. In particular, a DNS name can never opt into HTTP.
            return IsAllowedAddress(literal, allowPrivateNetwork)
                && (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                    || allowPrivateNetwork && IsPrivateAddress(literal));
        }

        // Hostnames always retain HTTPS and normal certificate validation. A
        // resolver is deliberately not part of this URL-only check.
        return uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            && (resolvedAddresses is null || resolvedAddresses.Count > 0
                && resolvedAddresses.All(address => IsAllowedAddress(address, allowPrivateNetwork)));
    }

    public static bool IsAllowedAddress(IPAddress address, bool allowPrivateNetwork)
    {
        ArgumentNullException.ThrowIfNull(address);
        // Mapped forms make the address family and textual host ambiguous;
        // callers must provide an unambiguous IPv4 or IPv6 literal.
        if (address.IsIPv4MappedToIPv6)
            return false;

        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal
            || IsMulticast(address)
            || IsMetadataAddress(address))
            return false;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            var privateV4 = b[0] == 10
                || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && b[1] == 168;
            var special = b[0] == 0 // this network
                || b[0] == 100 && b[1] is >= 64 and <= 127 // shared address space
                || b[0] == 169 && b[1] == 254 // link local
                || b[0] == 192 && b[1] == 0 && b[2] == 0 // IETF protocol assignments
                || b[0] == 192 && b[1] == 0 && b[2] == 2 // TEST-NET-1
                || b[0] == 198 && b[1] is 18 or 19 // benchmarking
                || b[0] == 198 && b[1] == 51 && b[2] == 100 // TEST-NET-2
                || b[0] == 192 && b[1] == 88 && b[2] == 99 // 6to4 anycast
                || b[0] == 203 && b[1] == 0 && b[2] == 113 // TEST-NET-3
                || b[0] >= 240; // reserved/future use
            return !special && (allowPrivateNetwork || !privateV4);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var b = address.GetAddressBytes();
            var ula = (b[0] & 0xfe) == 0xfc;
            var special = HasIpv6Prefix(b, [0x00], 8)
                || HasIpv6Prefix(b, [0x01], 8) // benchmarking
                || HasIpv6Prefix(b, [0x20, 0x01, 0x00, 0x02, 0x00, 0x00], 48) // benchmarking
                || HasIpv6Prefix(b, [0x20, 0x01, 0x00, 0x10], 28) // ORCHID
                || HasIpv6Prefix(b, [0x20, 0x01, 0x00, 0x20], 28) // ORCHIDv2
                || HasIpv6Prefix(b, [0x20, 0x01, 0x0d, 0xb8], 32) // documentation
                || HasIpv6Prefix(b, [0x3f, 0xff, 0xf0], 20) // documentation
                || b[0] == 0xfe && (b[1] & 0xc0) == 0xc0; // deprecated site-local
            return !special && (allowPrivateNetwork || !ula);
        }

        return false;
    }

    private static bool HasIpv6Prefix(byte[] address, byte[] prefix, int prefixLength)
    {
        var fullBytes = prefixLength / 8;
        for (var i = 0; i < fullBytes; i++)
            if (address[i] != prefix[i]) return false;
        var remainingBits = prefixLength % 8;
        return remainingBits == 0
            || (address[fullBytes] & (0xff << (8 - remainingBits)))
                == (prefix[fullBytes] & (0xff << (8 - remainingBits)));
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6 && (address.GetAddressBytes()[0] & 0xfe) == 0xfc;
    }

    private static bool IsMulticast(IPAddress address)
        => address.AddressFamily == AddressFamily.InterNetwork
            ? address.GetAddressBytes()[0] is >= 224 and <= 239
            : address.AddressFamily == AddressFamily.InterNetworkV6 && address.GetAddressBytes()[0] == 0xff;

    private static bool IsMetadataAddress(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 169 && b[1] == 254 && b[2] == 169 && b[3] == 254
                || b[0] == 168 && b[1] == 63 && b[2] == 129 && b[3] == 16;
        }
        return address.Equals(IPAddress.Parse("fd00:ec2::254"));
    }

    private static bool ContainsSecretQueryParameter(string query)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var key = pair.Split('=', 2)[0];
            if (string.Equals(Uri.UnescapeDataString(key), "apikey", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "api-key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "api_key", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "password", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Uri.UnescapeDataString(key), "token", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool LooksLikeAmbiguousNumericHost(string host)
    {
        if (host.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || host.All(char.IsDigit)) return true;
        var labels = host.Split('.');
        if (labels.Length == 0 || labels.Any(label => label.Length == 0 || !label.All(char.IsDigit))) return false;
        return labels.Length != 4 || labels.Any(label => label.Length > 1 && label[0] == '0');
    }
}
