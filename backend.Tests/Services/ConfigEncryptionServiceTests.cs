using System.Security.Cryptography;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class ConfigEncryptionServiceTests : IDisposable
{
    private readonly string? _previousMasterKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY");
    private readonly string? _previousOldKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD");

    [Fact]
    public void Encrypt_AndDecrypt_RoundTripsPlaintext()
    {
        SetKeys(masterKey: CreateKey(), oldKey: null);

        using var service = new ConfigEncryptionService();

        var ciphertext = service.Encrypt("api.key", "super-secret");
        var (plaintext, usedOldKey, requiresFormatUpgrade) = service.Decrypt("api.key", ciphertext);

        Assert.StartsWith("v2:", ciphertext);
        Assert.Equal("super-secret", plaintext);
        Assert.False(usedOldKey);
        Assert.False(requiresFormatUpgrade);
    }

    [Fact]
    public void Decrypt_FallsBackToOldKey_WhenPrimaryKeyDoesNotMatch()
    {
        var oldKey = CreateKey();
        SetKeys(masterKey: oldKey, oldKey: null);
        using var writer = new ConfigEncryptionService();
        var ciphertext = writer.Encrypt("api.key", "rotate-me");

        SetKeys(masterKey: CreateKey(), oldKey: oldKey);
        using var reader = new ConfigEncryptionService();
        var (plaintext, usedOldKey, requiresFormatUpgrade) = reader.Decrypt("api.key", ciphertext);

        Assert.Equal("rotate-me", plaintext);
        Assert.True(usedOldKey);
    }

    [Fact]
    public void Encrypt_WithoutPrimaryKey_Throws()
    {
        SetKeys(masterKey: null, oldKey: null);

        using var service = new ConfigEncryptionService();

        Assert.Throws<InvalidOperationException>(() => service.Encrypt("api.key", "x"));
    }

    [Fact]
    public void IsEncryptedFormat_RejectsNonCanonicalBase64WithUnusedBits()
    {
        SetKeys(masterKey: CreateKey(), oldKey: null);

        using var service = new ConfigEncryptionService();
        var v2Ciphertext = service.Encrypt("api.key", "super-secret");
        var base64Payload = v2Ciphertext[3..];

        Assert.True(ConfigEncryptionService.IsEncryptedFormat(v2Ciphertext));

        var nonCanonicalPayload = MutateBase64WithNonCanonicalTrailingBits(base64Payload);
        Assert.False(ConfigEncryptionService.IsEncryptedFormat($"v2:{nonCanonicalPayload}"));
        Assert.False(ConfigEncryptionService.IsEncryptedFormat($"v1:{nonCanonicalPayload}"));

        var malformedV2 = Assert.Throws<InvalidOperationException>(() =>
            service.Decrypt("api.key", $"v2:{nonCanonicalPayload}"));
        var malformedV1 = Assert.Throws<InvalidOperationException>(() =>
            service.Decrypt("api.key", $"v1:{nonCanonicalPayload}"));

        Assert.Equal("Ciphertext is not a supported encrypted config value.", malformedV2.Message);
        Assert.Equal("Ciphertext is not a supported encrypted config value.", malformedV1.Message);
    }

    [Fact]
    public void Decrypt_RejectsV2CiphertextRelabeledAsV1_AndAcceptsGenuineLegacyV1()
    {
        var key = CreateKey();
        SetKeys(masterKey: key, oldKey: null);

        using var service = new ConfigEncryptionService();
        var v2Ciphertext = service.Encrypt("api.key", "legacy-rotate");
        var relabeledV1 = "v1:" + v2Ciphertext[3..];

        Assert.True(ConfigEncryptionService.IsEncryptedFormat(relabeledV1));
        Assert.Throws<CryptographicException>(() => service.Decrypt("api.key", relabeledV1));

        var genuineV1 = CreateLegacyV1Ciphertext(Convert.FromBase64String(key), "legacy-rotate");
        Assert.Equal("legacy-rotate", service.Decrypt("api.key", genuineV1).plaintext);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", _previousMasterKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", _previousOldKey);
    }

    private static string CreateKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static void SetKeys(string? masterKey, string? oldKey)
    {
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", masterKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
    }

    private static string CreateLegacyV1Ciphertext(byte[] key, string plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var input = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[input.Length];
        var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Encrypt(nonce, input, ciphertext, tag);
            var packed = nonce.Concat(ciphertext).Concat(tag).ToArray();
            return "v1:" + Convert.ToBase64String(packed).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
        finally
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(input);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    private static string MutateBase64WithNonCanonicalTrailingBits(string canonical)
    {
        if (string.IsNullOrEmpty(canonical))
            throw new ArgumentException("Base64 input cannot be empty.", nameof(canonical));

        var remainder = canonical.Length % 4;
        if (remainder is 2)
        {
            return MutateBase64LastChar(canonical, 0b110000, 0b00001111);
        }

        if (remainder is 3)
        {
            return MutateBase64LastChar(canonical, 0b111100, 0b00000011);
        }

        if (remainder is 0)
        {
            throw new ArgumentException(
                "Cannot mutate a padded base64url string with no trailing bits.",
                nameof(canonical));
        }

        throw new ArgumentException("Invalid base64url remainder.", nameof(canonical));
    }

    private static string MutateBase64LastChar(string encoded, int topBitMask, int bitMask)
    {
        var last = encoded[^1];
        var value = DecodeBase64Url(last);
        var mutated = (value & topBitMask) | ((value & bitMask) | 1);

        return string.Concat(encoded[..^1], EncodeBase64Url(mutated));
    }

    private static int DecodeBase64Url(char character)
    {
        if (character is >= 'A' and <= 'Z') return character - 'A';
        if (character is >= 'a' and <= 'z') return character - 'a' + 26;
        if (character is >= '0' and <= '9') return character - '0' + 52;
        if (character == '-') return 62;
        if (character == '_') return 63;

        throw new ArgumentOutOfRangeException(nameof(character));
    }

    private static char EncodeBase64Url(int value)
    {
        if (value is >= 0 and <= 25) return (char)('A' + value);
        if (value is >= 26 and <= 51) return (char)('a' + value - 26);
        if (value is >= 52 and <= 61) return (char)('0' + value - 52);
        if (value == 62) return '-';
        if (value == 63) return '_';

        throw new ArgumentOutOfRangeException(nameof(value));
    }
}
