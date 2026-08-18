using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public sealed class ConfigEncryptionService : IDisposable
{
    private const string V1Prefix = "v1:";
    private const string V2Prefix = "v2:";
    private const string AadPrefix = "nzbdav-config:v2:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;
    private const int MaxPackedSize = 16 * 1024 * 1024;

    private readonly byte[]? _primaryKey = LoadKey("NZBDAV_MASTER_KEY");
    private readonly byte[]? _oldKey = LoadKey("NZBDAV_MASTER_KEY_OLD");
    private readonly byte[] _revisionKey;
    private bool _disposed;

    public ConfigEncryptionService()
    {
        _revisionKey = _primaryKey is not null
            ? DeriveRevisionKey(_primaryKey)
            : RandomNumberGenerator.GetBytes(KeySize);
    }

    /// <summary>Creates an opaque revision from the stored row, never from a
    /// plaintext secret. This is used for optimistic settings concurrency.</summary>
    public string CreateOpaqueRevision(string storedValue)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(storedValue);
        using var hmac = new HMACSHA256(_revisionKey);
        var input = Encoding.UTF8.GetBytes("usenet.providers\0" + storedValue);
        try
        {
            return Base64UrlEncode(hmac.ComputeHash(input));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    public bool IsKeyConfigured => _primaryKey is not null;

    public string Encrypt(string configName, string plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_primaryKey is null)
            throw new InvalidOperationException("Cannot encrypt without NZBDAV_MASTER_KEY.");

        var canonicalName = CanonicalizeConfigName(configName);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];
        var aad = Encoding.UTF8.GetBytes(AadPrefix + canonicalName);

        try
        {
            using (var aes = new AesGcm(_primaryKey, TagSize))
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, aad);

            var packed = new byte[NonceSize + ciphertext.Length + TagSize];
            try
            {
                Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
                Buffer.BlockCopy(ciphertext, 0, packed, NonceSize, ciphertext.Length);
                Buffer.BlockCopy(tag, 0, packed, NonceSize + ciphertext.Length, TagSize);
                return V2Prefix + Base64UrlEncode(packed);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(packed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    public (string plaintext, bool usedOldKey, bool requiresFormatUpgrade) Decrypt(
        string configName,
        string ciphertext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var canonicalName = CanonicalizeConfigName(configName);
        if (!TryDecode(ciphertext, out var version, out var packed))
            throw new InvalidOperationException("Ciphertext is not a supported encrypted config value.");

        var nonce = packed.AsSpan(0, NonceSize);
        var cipherBody = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plaintextBytes = new byte[cipherBody.Length];
        var aad = version == 2 ? Encoding.UTF8.GetBytes(AadPrefix + canonicalName) : null;

        try
        {
            if (_primaryKey is not null && TryDecrypt(_primaryKey, nonce, cipherBody, tag, plaintextBytes, aad))
                return (Encoding.UTF8.GetString(plaintextBytes), false, version == 1);

            if (_oldKey is not null && TryDecrypt(_oldKey, nonce, cipherBody, tag, plaintextBytes, aad))
                return (Encoding.UTF8.GetString(plaintextBytes), true, version == 1);

            throw new CryptographicException("Failed to authenticate encrypted config value.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintextBytes);
            if (aad is not null)
                CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(packed);
        }
    }

    public static bool IsEncryptedFormat(string? value)
        => TryDecode(value, out _, out var packed)
           && ClearAndTrue(packed);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_primaryKey is not null)
            CryptographicOperations.ZeroMemory(_primaryKey);
        if (_oldKey is not null)
            CryptographicOperations.ZeroMemory(_oldKey);
        CryptographicOperations.ZeroMemory(_revisionKey);
    }

    private static bool ClearAndTrue(byte[] packed)
    {
        CryptographicOperations.ZeroMemory(packed);
        return true;
    }

    private static bool TryDecode(string? value, out int version, out byte[] packed)
    {
        version = 0;
        packed = Array.Empty<byte>();
        if (value is null || value.Length < 4 || value.Length > MaxPackedSize * 2)
            return false;

        string prefix;
        if (value.StartsWith(V1Prefix, StringComparison.Ordinal))
        {
            version = 1;
            prefix = V1Prefix;
        }
        else if (value.StartsWith(V2Prefix, StringComparison.Ordinal))
        {
            version = 2;
            prefix = V2Prefix;
        }
        else
        {
            return false;
        }

        var encoded = value.AsSpan(prefix.Length);
        if (encoded.Length == 0 || encoded.Length % 4 == 1)
            return false;

        foreach (var character in encoded)
        {
            if (!(character is >= 'A' and <= 'Z')
                && !(character is >= 'a' and <= 'z')
                && !(character is >= '0' and <= '9')
                && character is not '-' and not '_')
                return false;
        }

        byte[]? decoded = null;
        try
        {
            var padded = encoded.ToString().Replace('-', '+').Replace('_', '/');
            var padding = (4 - padded.Length % 4) % 4;
            padded += padding switch
            {
                2 => "==",
                1 => "=",
                _ => string.Empty,
            };
            decoded = Convert.FromBase64String(padded);

            if (decoded.Length < NonceSize + TagSize || decoded.Length > MaxPackedSize)
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            if (!string.Equals(Base64UrlEncode(decoded), encoded.ToString(), StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(decoded);
                return false;
            }

            packed = decoded;
            return true;
        }
        catch (FormatException)
        {
            if (decoded is not null)
                CryptographicOperations.ZeroMemory(decoded);

            return false;
        }
    }

    private static string CanonicalizeConfigName(string configName)
    {
        if (SensitiveConfigKeys.TryGetCanonicalKey(configName, out var canonicalName))
            return canonicalName;

        throw new InvalidOperationException("Config encryption requires a managed config key.");
    }

    private static bool TryDecrypt(
        byte[] key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext,
        byte[]? aad)
    {
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, aad ?? Array.Empty<byte>());
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] DeriveRevisionKey(byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes("nzbdav-admin-settings-revision"));
    }

    private static byte[]? LoadKey(string envVarName)
    {
        var rawValue = Environment.GetEnvironmentVariable(envVarName);
        if (string.IsNullOrWhiteSpace(rawValue))
            return null;

        byte[] keyBytes;
        try
        {
            keyBytes = Convert.FromBase64String(rawValue);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{envVarName} must be valid base64.", ex);
        }

        if (keyBytes.Length != KeySize)
            throw new InvalidOperationException($"{envVarName} must decode to exactly 32 bytes.");

        return keyBytes;
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
