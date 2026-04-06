using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public sealed class StreamTokenServiceTests
{
    [Fact]
    public void GenerateTokenAndValidateToken_RoundTripForMatchingPath()
    {
        var service = CreateService();

        var token = service.GenerateToken("/api/stream/abc123");

        Assert.True(service.ValidateToken(token, "/api/stream/abc123"));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseForDifferentPath()
    {
        var service = CreateService();

        var token = service.GenerateToken("/api/stream/abc123");

        Assert.False(service.ValidateToken(token, "/api/stream/xyz789"));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenTokenIsExpired()
    {
        var currentUtc = new DateTimeOffset(2026, 4, 6, 12, 0, 0, TimeSpan.Zero);
        var service = CreateService(() => currentUtc);

        var token = service.GenerateToken("/api/stream/abc123", TimeSpan.FromMinutes(1));
        currentUtc = currentUtc.AddMinutes(2);

        Assert.False(service.ValidateToken(token, "/api/stream/abc123"));
    }

    [Fact]
    public void ValidateToken_ReturnsFalseWhenTokenIsTampered()
    {
        var service = CreateService();

        var token = service.GenerateToken("/api/stream/abc123");
        var tamperedToken = token[..^1] + (token.EndsWith('A') ? 'B' : 'A');

        Assert.False(service.ValidateToken(tamperedToken, "/api/stream/abc123"));
    }

    private static StreamTokenService CreateService(Func<DateTimeOffset>? utcNow = null)
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

        return new StreamTokenService(configManager, utcNow);
    }
}
