using System.Security.Cryptography;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class ConfigEncryptionServiceTests : IDisposable
{
    private readonly string? _previousMasterKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY");
    private readonly string? _previousOldKey = Environment.GetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD");

    [Fact]
    public void Encrypt_AndDecrypt_RoundTripsPlaintext()
    {
        SetKeys(masterKey: CreateKey(), oldKey: null);

        using var service = new ConfigEncryptionService();

        var ciphertext = service.Encrypt("super-secret");
        var (plaintext, usedOldKey) = service.Decrypt(ciphertext);

        Assert.StartsWith("v1:", ciphertext);
        Assert.Equal("super-secret", plaintext);
        Assert.False(usedOldKey);
    }

    [Fact]
    public void Decrypt_FallsBackToOldKey_WhenPrimaryKeyDoesNotMatch()
    {
        var oldKey = CreateKey();
        SetKeys(masterKey: oldKey, oldKey: null);
        using var writer = new ConfigEncryptionService();
        var ciphertext = writer.Encrypt("rotate-me");

        SetKeys(masterKey: CreateKey(), oldKey: oldKey);
        using var reader = new ConfigEncryptionService();
        var (plaintext, usedOldKey) = reader.Decrypt(ciphertext);

        Assert.Equal("rotate-me", plaintext);
        Assert.True(usedOldKey);
    }

    [Fact]
    public void Encrypt_WithoutPrimaryKey_Throws()
    {
        SetKeys(masterKey: null, oldKey: null);

        using var service = new ConfigEncryptionService();

        Assert.Throws<InvalidOperationException>(() => service.Encrypt("x"));
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
}
