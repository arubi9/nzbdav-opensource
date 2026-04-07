using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Services;

public sealed class ConfigEncryptionService : IDisposable
{
    private const string FormatPrefix = "v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    private readonly byte[]? _primaryKey = LoadKey("NZBDAV_MASTER_KEY");
    private readonly byte[]? _oldKey = LoadKey("NZBDAV_MASTER_KEY_OLD");
    private bool _disposed;

    public bool IsKeyConfigured => _primaryKey is not null;

    public string Encrypt(string plaintext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_primaryKey is null)
            throw new InvalidOperationException("Cannot encrypt: NZBDAV_MASTER_KEY is not set.");

        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(_primaryKey, TagSize))
        {
            aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);
        }

        var packed = new byte[NonceSize + ciphertext.Length + TagSize];
        Buffer.BlockCopy(nonce, 0, packed, 0, NonceSize);
        Buffer.BlockCopy(ciphertext, 0, packed, NonceSize, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, packed, NonceSize + ciphertext.Length, TagSize);

        return FormatPrefix + Base64UrlEncode(packed);
    }

    public (string plaintext, bool usedOldKey) Decrypt(string ciphertext)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsEncryptedFormat(ciphertext))
            throw new InvalidOperationException("Cannot decrypt value without the v1: prefix.");

        var packed = Base64UrlDecode(ciphertext.AsSpan(FormatPrefix.Length));
        if (packed.Length < NonceSize + TagSize)
            throw new CryptographicException("Ciphertext too short to contain nonce and tag.");

        var nonce = packed.AsSpan(0, NonceSize);
        var cipherBody = packed.AsSpan(NonceSize, packed.Length - NonceSize - TagSize);
        var tag = packed.AsSpan(packed.Length - TagSize, TagSize);
        var plaintextBytes = new byte[cipherBody.Length];

        if (_primaryKey is not null && TryDecrypt(_primaryKey, nonce, cipherBody, tag, plaintextBytes))
            return (Encoding.UTF8.GetString(plaintextBytes), false);

        if (_oldKey is not null && TryDecrypt(_oldKey, nonce, cipherBody, tag, plaintextBytes))
            return (Encoding.UTF8.GetString(plaintextBytes), true);

        throw new CryptographicException(
            "Failed to decrypt config value with any configured master key. " +
            "If this row was encrypted with an older key, set NZBDAV_MASTER_KEY_OLD " +
            "alongside the current NZBDAV_MASTER_KEY and restart to rotate.");
    }

    public static bool IsEncryptedFormat(string value) => value.StartsWith(FormatPrefix, StringComparison.Ordinal);

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        if (_primaryKey is not null)
            Array.Clear(_primaryKey);

        if (_oldKey is not null)
            Array.Clear(_oldKey);
    }

    private static bool TryDecrypt(
        byte[] key,
        ReadOnlySpan<byte> nonce,
        ReadOnlySpan<byte> ciphertext,
        ReadOnlySpan<byte> tag,
        Span<byte> plaintext)
    {
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
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

    private static byte[] Base64UrlDecode(ReadOnlySpan<char> value)
    {
        var padded = new string(value).Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
