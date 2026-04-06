using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class StreamTokenServiceTests
{
    [Fact]
    public void GenerateTokenAndValidateToken_RoundTripForMatchingPath()
    {
        var configManager = CreateConfigManager();

        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager);

        Assert.True(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseForDifferentPath()
    {
        var configManager = CreateConfigManager();

        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager);

        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/xyz789", configManager));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenTokenIsTampered()
    {
        var configManager = CreateConfigManager();

        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager);
        var tamperedToken = token[..^1] + (token.EndsWith('a') ? 'b' : 'a');

        Assert.False(StreamTokenService.ValidateToken(tamperedToken, "/api/stream/abc123", configManager));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenTokenIsMalformed()
    {
        var configManager = CreateConfigManager();
        Assert.False(StreamTokenService.ValidateToken("not-a-token", "/api/stream/abc123", configManager));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenTokenIsExpiredBeyondGracePeriod()
    {
        var configManager = CreateConfigManager();
        // Must be beyond expiry + 7-day grace period (10080 + 1 = 10081 minutes ago)
        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager, expiryMinutes: -10081);
        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager));
    }

    [Fact]
    public void ValidateToken_AcceptsTokenWithinGracePeriod()
    {
        var configManager = CreateConfigManager();
        // Expired 1 minute ago — within the 7-day grace period
        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager, expiryMinutes: -1);
        Assert.True(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenMethodMismatches()
    {
        var configManager = CreateConfigManager();
        var token = StreamTokenService.GenerateToken("/api/stream/abc123", configManager, method: "GET");
        Assert.False(StreamTokenService.ValidateToken(token, "/api/stream/abc123", configManager, method: "DELETE"));
    }

    private static ConfigManager CreateConfigManager()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
            [
                new ConfigItem
                {
                    ConfigName = "api.key",
                    ConfigValue = "test-api-key"
                }
            ]
        );
        return configManager;
    }
}
