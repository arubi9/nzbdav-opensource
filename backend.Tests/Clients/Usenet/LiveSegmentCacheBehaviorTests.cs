using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Tests.TestDoubles;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class LiveSegmentCacheBehaviorTests
{
    [Fact]
    public void CacheEntryMetadata_SerializesAndRoundTripsHeaderAndTieringMetadata()
    {
        var ownerId = Guid.NewGuid();
        var metadata = new CacheEntryMetadata
        {
            SegmentId = "segment-a",
            SizeBytes = 1234,
            LastAccessUtcTicks = 42,
            YencFileName = "example.mkv",
            YencFileSize = 5678,
            YencLineLength = 128,
            YencPartNumber = 2,
            YencTotalParts = 7,
            YencPartSize = 910,
            YencPartOffset = 1112,
            Category = SegmentCategory.VideoSegment,
            OwnerNzbId = ownerId
        };

        var roundTripped = JsonSerializer.Deserialize<CacheEntryMetadata>(JsonSerializer.Serialize(metadata));
        Assert.NotNull(roundTripped);
        Assert.Equal(metadata.SegmentId, roundTripped!.SegmentId);
        Assert.Equal(metadata.SizeBytes, roundTripped.SizeBytes);
        Assert.Equal(metadata.LastAccessUtcTicks, roundTripped.LastAccessUtcTicks);
        Assert.Equal(metadata.Category, roundTripped.Category);
        Assert.Equal(metadata.OwnerNzbId, roundTripped.OwnerNzbId);

        var header = roundTripped.ToYencHeader();
        Assert.Equal(metadata.YencFileName, header.FileName);
        Assert.Equal(metadata.YencFileSize, header.FileSize);
        Assert.Equal(metadata.YencLineLength, header.LineLength);
        Assert.Equal(metadata.YencPartNumber, header.PartNumber);
        Assert.Equal(metadata.YencTotalParts, header.TotalParts);
        Assert.Equal(metadata.YencPartSize, header.PartSize);
        Assert.Equal(metadata.YencPartOffset, header.PartOffset);
    }

    [Fact]
    public async Task RehydrateFromDisk_LoadsExistingBodyAndMetadata()
    {
        await using var cacheScope = new TempCacheScope();
        var ownerId = Guid.NewGuid();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0);

        using (var liveCache = new LiveSegmentCache(cacheScope.Path))
        using (var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache))
        {
            await CacheSegmentAsync(client, "segment-a", SegmentCategory.SmallFile, ownerId);
        }

        var cachePath = GetCachePath(cacheScope.Path, "segment-a");
        Assert.True(File.Exists(cachePath));
        Assert.True(File.Exists(cachePath + ".meta"));

        using var rehydratedCache = new LiveSegmentCache(cacheScope.Path);
        Assert.True(rehydratedCache.HasBody("segment-a"));

        var stats = rehydratedCache.GetStats();
        Assert.Equal(1, stats.CachedSegmentCount);
        Assert.Equal(1, stats.SmallFileCount);
        Assert.Equal(0, stats.VideoSegmentCount);
        Assert.Equal(0, stats.UnknownCount);
    }

    [Fact]
    public async Task RehydrateFromDisk_RemovesOrphanedBodyAndMetadataFiles()
    {
        await using var cacheScope = new TempCacheScope();
        var orphanBodyPath = GetCachePath(cacheScope.Path, "orphan-body");
        var orphanMetaBodyPath = GetCachePath(cacheScope.Path, "orphan-meta");

        await File.WriteAllBytesAsync(orphanBodyPath, "BODY"u8.ToArray());
        await File.WriteAllTextAsync(orphanMetaBodyPath + ".meta", JsonSerializer.Serialize(
            CreateMetadata("orphan-meta", SegmentCategory.Unknown, ownerNzbId: null, sizeBytes: 4)));

        using var liveCache = new LiveSegmentCache(cacheScope.Path);

        Assert.False(File.Exists(orphanBodyPath));
        Assert.False(File.Exists(orphanMetaBodyPath + ".meta"));
        Assert.Equal(0, liveCache.GetStats().CachedSegmentCount);
    }

    [Fact]
    public async Task RehydrateFromDisk_RemovesCorruptedMetadataAndBody()
    {
        await using var cacheScope = new TempCacheScope();
        var bodyPath = GetCachePath(cacheScope.Path, "corrupt-meta");

        await File.WriteAllBytesAsync(bodyPath, "BODY"u8.ToArray());
        await File.WriteAllTextAsync(bodyPath + ".meta", "{ not-json");

        using var liveCache = new LiveSegmentCache(cacheScope.Path);

        Assert.False(File.Exists(bodyPath));
        Assert.False(File.Exists(bodyPath + ".meta"));
        Assert.Equal(0, liveCache.GetStats().CachedSegmentCount);
    }

    [Fact]
    public async Task PruneAsync_EvictsVideoSegmentsBeforeUnknownAndSmallFiles()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("small", Encoding.ASCII.GetBytes("1111"), partOffset: 0)
            .AddSegment("video", Encoding.ASCII.GetBytes("2222"), partOffset: 4)
            .AddSegment("unknown", Encoding.ASCII.GetBytes("3333"), partOffset: 8);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, maxCacheSizeBytes: 8, maxAge: TimeSpan.FromHours(1));
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        await CacheSegmentAsync(client, "small", SegmentCategory.SmallFile, Guid.NewGuid());
        await CacheSegmentAsync(client, "video", SegmentCategory.VideoSegment, Guid.NewGuid());
        await CacheSegmentAsync(client, "unknown");

        Assert.True(liveCache.HasBody("small"));
        Assert.False(liveCache.HasBody("video"));
        Assert.True(liveCache.HasBody("unknown"));
    }

    [Fact]
    public async Task SmallFileEntries_DoNotExpireByTime()
    {
        await using var cacheScope = new TempCacheScope();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("small", Encoding.ASCII.GetBytes("1111"), partOffset: 0)
            .AddSegment("unknown", Encoding.ASCII.GetBytes("2222"), partOffset: 4);

        using var liveCache = new LiveSegmentCache(cacheScope.Path, maxCacheSizeBytes: 1024, maxAge: TimeSpan.FromMilliseconds(50));
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        await CacheSegmentAsync(client, "small", SegmentCategory.SmallFile, Guid.NewGuid());
        await CacheSegmentAsync(client, "unknown");

        await Task.Delay(150);
        await liveCache.PruneAsync();

        Assert.True(liveCache.HasBody("small"));
        Assert.False(liveCache.HasBody("unknown"));
    }

    [Fact]
    public async Task EvictByOwner_RemovesOnlyMatchingOwnerEntries()
    {
        await using var cacheScope = new TempCacheScope();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var fakeNntpClient = new FakeNntpClient()
            .AddSegment("segment-a", Encoding.ASCII.GetBytes("AAAA"), partOffset: 0)
            .AddSegment("segment-b", Encoding.ASCII.GetBytes("BBBB"), partOffset: 4);

        using var liveCache = new LiveSegmentCache(cacheScope.Path);
        using var client = new LiveSegmentCachingNntpClient(fakeNntpClient, liveCache);

        await CacheSegmentAsync(client, "segment-a", SegmentCategory.Unknown, ownerA);
        await CacheSegmentAsync(client, "segment-b", SegmentCategory.Unknown, ownerB);

        liveCache.EvictByOwner(ownerA);

        Assert.False(liveCache.HasBody("segment-a"));
        Assert.True(liveCache.HasBody("segment-b"));
    }

    [Theory]
    [InlineData("poster.jpg", SegmentCategory.SmallFile)]
    [InlineData("movie.mkv", SegmentCategory.VideoSegment)]
    [InlineData("archive.zzz", SegmentCategory.Unknown)]
    public void SegmentCategoryClassifier_MapsExpectedExtensions(string fileName, SegmentCategory expectedCategory)
    {
        Assert.Equal(expectedCategory, SegmentCategoryClassifier.Classify(fileName));
    }

    private static async Task CacheSegmentAsync(
        LiveSegmentCachingNntpClient client,
        string segmentId,
        SegmentCategory? category = null,
        Guid? ownerNzbId = null
    )
    {
        IDisposable? contextScope = null;
        try
        {
            if (category.HasValue)
                contextScope = SegmentFetchContext.Set(category.Value, ownerNzbId);

            var response = await client.DecodedBodyAsync(segmentId, CancellationToken.None);
            await response.Stream.DisposeAsync();
        }
        finally
        {
            contextScope?.Dispose();
        }
    }

    private static CacheEntryMetadata CreateMetadata(
        string segmentId,
        SegmentCategory category,
        Guid? ownerNzbId,
        long sizeBytes
    )
    {
        return new CacheEntryMetadata
        {
            SegmentId = segmentId,
            SizeBytes = sizeBytes,
            LastAccessUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            YencFileName = $"{segmentId}.bin",
            YencFileSize = sizeBytes,
            YencLineLength = 128,
            YencPartNumber = 1,
            YencTotalParts = 1,
            YencPartSize = sizeBytes,
            YencPartOffset = 0,
            Category = category,
            OwnerNzbId = ownerNzbId
        };
    }

    private static string GetCachePath(string cacheDirectory, string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        return Path.Combine(cacheDirectory, Convert.ToHexString(hash));
    }

    private sealed class TempCacheScope : IAsyncDisposable
    {
        public TempCacheScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public ValueTask DisposeAsync()
        {
            if (!Directory.Exists(Path))
                return ValueTask.CompletedTask;

            const int maxAttempts = 10;
            for (var attempt = 0; attempt < maxAttempts; attempt++)
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                    return ValueTask.CompletedTask;
                }
                catch (IOException)
                {
                    if (attempt >= maxAttempts - 1) break;
                    Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException)
                {
                    if (attempt >= maxAttempts - 1) break;
                    Thread.Sleep(50);
                }
            }

            return ValueTask.CompletedTask;
        }
    }
}
