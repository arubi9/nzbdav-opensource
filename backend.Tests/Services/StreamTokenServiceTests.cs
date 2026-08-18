using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Services;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class StreamTokenServiceTests
{
    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public StreamTokenServiceTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task GenerateTokenAndValidateToken_RoundTripForMatchingPath()
    {
        var configManager = await CreateConfigManager();

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            timeProvider: new FixedTimeProvider(FromUnix(1_700_000_000)));

        Assert.True(
            StreamTokenService.ValidateToken(
                token,
                "/api/stream/abc123",
                configManager,
                timeProvider: new FixedTimeProvider(FromUnix(1_700_000_001))));
    }

    [Fact]
    public async Task GenerateToken_IsPathCanonicalizedAndMethodFamilySharedBetweenGetAndHead()
    {
        var configManager = await CreateConfigManager();

        var encodedPath = "/api/stream/%41bc";
        var token = StreamTokenService.GenerateToken(
            encodedPath,
            configManager,
            method: "GET",
            timeProvider: new FixedTimeProvider(FromUnix(1_700_000_000)));

        Assert.True(
            StreamTokenService.ValidateToken(
                token,
                "/api/stream/Abc",
                configManager,
                method: "HEAD",
                timeProvider: new FixedTimeProvider(FromUnix(1_700_000_001))));
    }

    [Fact]
    public async Task ValidateToken_RejectsPostMethod()
    {
        var configManager = await CreateConfigManager();
        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            method: "GET",
            timeProvider: new FixedTimeProvider(FromUnix(1_700_000_000)));

        Assert.False(
            StreamTokenService.ValidateToken(
                token,
                "/api/stream/abc123",
                configManager,
                method: "POST",
                timeProvider: new FixedTimeProvider(FromUnix(1_700_000_001))));
    }

    [Fact]
    public async Task ValidateToken_RejectsWrongPath()
    {
        var configManager = await CreateConfigManager();

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            timeProvider: new FixedTimeProvider(FromUnix(1_700_000_000)));

        Assert.False(
            StreamTokenService.ValidateToken(
                token,
                "/api/stream/xyz789",
                configManager,
                timeProvider: new FixedTimeProvider(FromUnix(1_700_000_001))));
    }

    [Fact]
    public async Task ValidateToken_RejectsTamperedSignature()
    {
        var configManager = await CreateConfigManager();

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            timeProvider: new FixedTimeProvider(FromUnix(1_700_000_000)));

        var tamperedToken = token[..^1] + (token.EndsWith('a') ? 'b' : 'a');

        Assert.False(
            StreamTokenService.ValidateToken(
                tamperedToken,
                "/api/stream/abc123",
                configManager,
                timeProvider: new FixedTimeProvider(FromUnix(1_700_000_001))));
    }

    [Fact]
    public async Task ValidateToken_RejectsMalformedAndOutOfBoundsTokens()
    {
        var configManager = await CreateConfigManager();
        var now = FromUnix(1_700_000_000);

        Assert.False(StreamTokenService.ValidateToken("not-a-token", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"{now.ToUnixTimeSeconds()}.short", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"{now.ToUnixTimeSeconds()}.{new string('a', 42)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"{now.ToUnixTimeSeconds()}.{new string('a', 44)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"{now.ToUnixTimeSeconds()}.{new string('g', 43)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($" {now.ToUnixTimeSeconds()}.{new string('a', 43)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"{now.ToUnixTimeSeconds()}.", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"+{now.ToUnixTimeSeconds()}.short", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
        Assert.False(StreamTokenService.ValidateToken($"-{now.ToUnixTimeSeconds()}.short", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now)));
    }

    [Fact]
    public async Task ValidateToken_GeneratesCanonicalBase64UrlSignatures()
    {
        var configManager = await CreateConfigManager();
        var now = FromUnix(1_700_000_000);

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            expiryMinutes: 1,
            timeProvider: new FixedTimeProvider(now));

        var parts = token.Split('.');
        Assert.Equal(2, parts.Length);
        Assert.Equal(43, parts[1].Length);
        Assert.DoesNotContain('=', parts[1]);
        Assert.Matches("^[A-Za-z0-9_-]+$", parts[1]);
    }

    [Fact]
    public async Task ValidateToken_RejectsPaddingOrTrailingBitsInBase64Signature()
    {
        var configManager = await CreateConfigManager();
        var now = FromUnix(1_700_000_000);

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            expiryMinutes: 1,
            timeProvider: new FixedTimeProvider(now));

        var parts = token.Split('.');
        var payload = parts[0];

        Assert.False(StreamTokenService.ValidateToken($"{payload}.{parts[1]}=", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddMinutes(1))));

        var tampered = parts[1][..^1] + "B";
        Assert.False(StreamTokenService.ValidateToken($"{payload}.{tampered}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddMinutes(1))));
    }

    [Fact]
    public async Task ValidateToken_UsesGraceWindowAfterExpiryAndRejectsBeyond()
    {
        var configManager = await CreateConfigManager();
        var now = FromUnix(1_700_000_000);

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            expiryMinutes: 0,
            timeProvider: new FixedTimeProvider(now));

        Assert.True(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddDays(6))));
        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddDays(8))));
    }

    [Fact]
    public async Task ValidateToken_RejectsExpiryTooFarInTheFuture()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager();

        var token = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            configManager,
            expiryMinutes: 60 * 24 * 14,
            timeProvider: new FixedTimeProvider(now));

        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddSeconds(1))));
    }

    [Fact]
    public async Task ValidateToken_AllowsLegacyApiTokenWithinMigrationWindow()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        var legacyToken = CreateLegacyApiToken();

        Assert.True(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenOutsideMigrationWindow()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-20));

        var legacyToken = CreateLegacyApiToken();

        Assert.False(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenWhenMarkerInFuture()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(1));

        var legacyToken = CreateLegacyApiToken();

        Assert.False(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_AcceptsLegacyHexSignaturesInBothCasesForFixedVector()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        var uppercase = CreateLegacyApiToken(issuedAt: now);
        var lowercase = CreateLegacyApiToken(issuedAt: now, uppercaseSignature: false);

        Assert.True(StreamTokenService.ValidateToken(uppercase, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
        Assert.True(StreamTokenService.ValidateToken(lowercase, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenWhenTokenMalformed()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        var legacyToken = CreateLegacyApiToken(issuedAt: now);
        var parts = legacyToken.Split('.');

        Assert.False(StreamTokenService.ValidateToken($"{parts[0]}.{new string('0', 63)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
        Assert.False(StreamTokenService.ValidateToken($"{parts[0]}.{new string('z', 64)}", "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenWrongMethod()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        var legacyToken = CreateLegacyApiToken(issuedAt: now, method: "GET");

        Assert.False(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            method: "POST",
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenWrongPath()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        var legacyToken = CreateLegacyApiToken(issuedAt: now, path: "/api/stream/abc123");

        const string invalidPath = "/api/stream/zzz";
        Assert.False(StreamTokenService.ValidateToken(legacyToken, invalidPath, configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenOnExpiryBoundaryOutsideWindow()
    {
        var now = FromUnix(1_700_000_000);
        var issuedAt = now;
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-1));

        var legacyToken = CreateLegacyApiToken(issuedAt: issuedAt);

        Assert.False(StreamTokenService.ValidateToken(
            legacyToken,
            "/api/stream/abc123",
            configManager,
            timeProvider: new FixedTimeProvider(issuedAt.AddSeconds(7 * 24 * 60 * 60 + 1))));
        Assert.True(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(issuedAt.AddDays(6).AddHours(23).AddMinutes(59).AddSeconds(59))));
    }

    [Fact]
    public async Task ValidateToken_RejectsLegacyApiTokenWithWrongApiKey()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-13));

        configManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "alternate-key"
                }
            ]);

        var legacyToken = CreateLegacyApiToken(issuedAt: now);

        Assert.False(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_AllowsPreviousStreamKeyWithinWindow()
    {
        var now = FromUnix(1_700_000_000);
        var previousKey = "previous-stream-key";
        var configManager = await CreateConfigManager(
            strmKeyPrevious: previousKey,
            strmKeyPreviousExpiresAt: now.AddDays(13));

        var previousKeyManager = new ConfigManager();
        previousKeyManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                },
                new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = previousKey
                }
            ]);
        var previousToken = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            previousKeyManager,
            timeProvider: new FixedTimeProvider(now));

        Assert.True(StreamTokenService.ValidateToken(previousToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsPreviousStreamKeyAfterWindow()
    {
        var now = FromUnix(1_700_000_000);
        var previousKey = "previous-stream-key";
        var configManager = await CreateConfigManager(
            strmKeyPrevious: previousKey,
            strmKeyPreviousExpiresAt: now.AddDays(-1));

        var previousKeyManager = new ConfigManager();
        previousKeyManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                },
                new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = previousKey
                }
            ]);
        var previousToken = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            previousKeyManager,
            timeProvider: new FixedTimeProvider(now));

        Assert.False(StreamTokenService.ValidateToken(previousToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsFuturePreviousStreamKeyExpiry()
    {
        var now = FromUnix(1_700_000_000);
        var previousKey = "previous-stream-key";
        var configManager = await CreateConfigManager(
            strmKeyPrevious: previousKey,
            strmKeyPreviousExpiresAt: now.AddDays(1));

        var previousKeyManager = new ConfigManager();
        previousKeyManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                },
                new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = previousKey
                }
            ]);
        var previousToken = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            previousKeyManager,
            timeProvider: new FixedTimeProvider(now));

        Assert.False(StreamTokenService.ValidateToken(previousToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsMalformedPreviousStreamKeyWindow()
    {
        var now = FromUnix(1_700_000_000);
        var previousKey = "previous-stream-key";
        var configManager = await CreateConfigManager(
            strmKeyPrevious: previousKey,
            strmKeyPreviousExpiresAtString: "not-a-timestamp");

        var previousKeyManager = new ConfigManager();
        previousKeyManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                },
                new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = previousKey
                }
            ]);
        var previousToken = StreamTokenService.GenerateToken(
            "/api/stream/abc123",
            previousKeyManager,
            timeProvider: new FixedTimeProvider(now));

        Assert.False(StreamTokenService.ValidateToken(previousToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsWhenStreamKeyRotated()
    {
        var configManager = await CreateConfigManager();
        var now = FromUnix(1_700_000_000);
        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager, timeProvider: new FixedTimeProvider(now));

        configManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.strm-key",
                    ConfigValue = "new-stream-key"
                }
            ]);

        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddSeconds(1))));
    }

    [Fact]
    public async Task ValidateToken_RejectsMalformedMigrationMarkerForLegacyFallback()
    {
        var now = FromUnix(1_700_000_000);
        var configManager = await CreateConfigManager(
            markerStart: now.AddDays(-1),
            markerValueOverride: "not-a-timestamp");

        var legacyToken = CreateLegacyApiToken();

        Assert.False(StreamTokenService.ValidateToken(legacyToken, "/api/stream/abc123", configManager,
            timeProvider: new FixedTimeProvider(now.AddHours(1))));
    }

    private static string CreateLegacyApiToken(
        DateTimeOffset? issuedAt = null,
        string path = "/api/stream/abc123",
        string method = "GET",
        bool uppercaseSignature = true)
    {
        issuedAt ??= FromUnix(1_700_000_000);

        var canonicalMethod = method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
            ? "GET"
            : method.ToUpperInvariant();

        var canonicalPath = "/" + path.TrimStart('/');

        var expiry = issuedAt.Value.ToUnixTimeSeconds();
        var payload = $"{canonicalMethod}:{expiry}:{canonicalPath}";
        var signature = GenerateLegacySignatureBytes(payload, "test-api-key");
        var signatureText = Convert.ToHexString(signature);

        return uppercaseSignature
            ? $"{expiry}.{signatureText}"
            : $"{expiry}.{signatureText.ToLowerInvariant()}";
    }

    private static byte[] GenerateLegacySignatureBytes(string payload, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        return HMACSHA256.HashData(keyBytes, payloadBytes);
    }

    private async Task<ConfigManager> CreateConfigManager(
        DateTimeOffset? markerStart = null,
        string? markerValueOverride = null,
        string? strmKeyPrevious = null,
        DateTimeOffset? strmKeyPreviousExpiresAt = null,
        string? strmKeyPreviousExpiresAtString = null)
    {
        await _fixture.ResetAsync();

        await using var setupContext = new DavDatabaseContext();
        await setupContext.Database.EnsureDeletedAsync();
        await setupContext.Database.EnsureCreatedAsync();

        setupContext.ConfigItems.AddRange(
            new ConfigItem
            {
                ConfigName = "api.key",
                ConfigValue = "test-api-key"
            },
            new ConfigItem
            {
                ConfigName = "api.strm-key",
                ConfigValue = "test-stream-key"
            });

        if (markerStart is not null)
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = SetupConfigKeys.StreamTokenLegacyMigrationStart,
                ConfigValue = markerValueOverride ?? markerStart.Value.ToString("O")
            });
        }

        if (strmKeyPrevious is not null)
        {
            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous",
                ConfigValue = strmKeyPrevious
            });

            setupContext.ConfigItems.Add(new ConfigItem
            {
                ConfigName = "api.strm-key-previous-expires-at",
                ConfigValue = strmKeyPreviousExpiresAtString
                               ?? strmKeyPreviousExpiresAt?.ToString("O")
                               ?? DateTimeOffset.UtcNow.AddDays(14).ToString("O")
            });
        }

        await setupContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        await configManager.LoadConfig();

        return configManager;
    }

    private static DateTimeOffset FromUnix(long unixTimeSeconds) => DateTimeOffset.FromUnixTimeSeconds(unixTimeSeconds);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
