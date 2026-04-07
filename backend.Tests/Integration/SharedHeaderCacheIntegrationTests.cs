using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Tests.Clients.Usenet.Caching;
using UsenetSharp.Models;

namespace backend.Tests.Integration;

[Collection(nameof(SharedHeaderCacheCollection))]
public sealed class SharedHeaderCacheIntegrationTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SharedHeaderCacheIntegrationTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ColdCacheHitViaSharedCache_DoesNotCallNntpFactory()
    {
        if (!_fixture.IsAvailable) return;

        await _fixture.ResetAsync();
        var sharedCache = new SharedHeaderCache();
        await sharedCache.WriteAsync("segment-a", CreateHeader("cached.bin", 0), CancellationToken.None);

        var configManager = new ConfigManager();
        using var liveCache = new LiveSegmentCache(configManager, sharedCache);
        var called = false;

        var header = await liveCache.GetOrAddHeaderAsync(
            "segment-a",
            _ =>
            {
                called = true;
                return Task.FromResult(CreateHeader("fallback.bin", 0));
            },
            CancellationToken.None);

        Assert.False(called);
        Assert.Equal("cached.bin", header.FileName);
    }

    [Fact]
    public async Task SharedCacheDisabled_PassthroughCallsFactory()
    {
        if (!_fixture.IsAvailable) return;

        await _fixture.ResetAsync();

        var configManager = new ConfigManager();
        using var liveCache = new LiveSegmentCache(configManager, sharedHeaderCache: null);

        var header = await liveCache.GetOrAddHeaderAsync(
            "segment-a",
            _ => Task.FromResult(CreateHeader("factory.bin", 0)),
            CancellationToken.None);

        Assert.Equal("factory.bin", header.FileName);
    }

    private static UsenetYencHeader CreateHeader(string fileName, long partOffset)
    {
        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = partOffset + 100,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 100,
            PartOffset = partOffset
        };
    }
}
