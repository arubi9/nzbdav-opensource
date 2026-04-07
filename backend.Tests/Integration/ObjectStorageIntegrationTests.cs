using System.Diagnostics;
using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Minio;
using Minio.DataModel.Args;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace backend.Tests.Integration;

[Collection(nameof(ObjectStorageIntegrationCollection))]
public sealed class ObjectStorageIntegrationTests
{
    [SkippableFact]
    public async Task EndToEnd_WriteAndRead()
    {
        Skip.IfNot(DockerAvailable(), "Docker is required for this integration test.");

        await using var fixture = await MinioFixture.StartAsync();

        fixture.Cache.EnqueueWrite(
            "segment-a",
            Encoding.ASCII.GetBytes("segment-a"),
            SegmentCategory.Unknown,
            null,
            CreateHeader("segment.bin"));

        var stream = await WaitForReadAsync(fixture.Cache, "segment-a");
        await using (stream)
        {
            Assert.NotNull(stream);
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(stream!)));
        }
    }

    [SkippableFact]
    public async Task L1MissL2Hit_PromotesToL1()
    {
        Skip.IfNot(DockerAvailable(), "Docker is required for this integration test.");

        await using var fixture = await MinioFixture.StartAsync();
        await fixture.PutDirectAsync(
            "segment-a",
            Encoding.ASCII.GetBytes("segment-a"),
            SegmentCategory.SmallFile,
            Guid.NewGuid(),
            CreateHeader("segment.bin"));

        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: fixture.Cache);

        var fetchResult = await liveCache.GetOrAddBodyAsync(
            "segment-a",
            _ => throw new InvalidOperationException("NNTP should not be used on L2 hit."),
            CancellationToken.None);

        await using (fetchResult.Response.Stream)
        {
            Assert.Equal("segment-a", Encoding.ASCII.GetString(await ReadAllBytesAsync(fetchResult.Response.Stream)));
        }

        Assert.Equal(LiveSegmentCache.BodyFetchOrigin.L2, fetchResult.Origin);
        Assert.True(liveCache.HasBody("segment-a"));
        Assert.Equal(1, liveCache.GetStats().SmallFileCount);
    }

    [SkippableFact]
    public async Task L2Unreachable_FallsThroughToNntp()
    {
        Skip.IfNot(DockerAvailable(), "Docker is required for this integration test.");

        await using var fixture = await MinioFixture.StartAsync();
        await fixture.Container.StopAsync();

        await using var cacheScope = new TempCacheScope();
        using var liveCache = new LiveSegmentCache(cacheScope.Path, l2Cache: fixture.Cache);
        var fakeNntp = new FakeNntpClient().AddSegment("segment-a", Encoding.ASCII.GetBytes("segment-a"), partOffset: 0);

        var fetchResult = await liveCache.GetOrAddBodyAsync(
            "segment-a",
            async _ =>
            {
                var response = await fakeNntp.DecodedBodyAsync("segment-a", CancellationToken.None);
                var header = await response.Stream.GetYencHeadersAsync(CancellationToken.None);
                return new LiveSegmentCache.BodyFetchSource(response.Stream, header!, null);
            },
            CancellationToken.None);

        await fetchResult.Response.Stream.DisposeAsync();

        Assert.Equal(LiveSegmentCache.BodyFetchOrigin.Nntp, fetchResult.Origin);
        Assert.Equal(1, fakeNntp.DecodedBodyCallCount);
    }

    [SkippableFact]
    public async Task BucketCreation_IsIdempotent()
    {
        Skip.IfNot(DockerAvailable(), "Docker is required for this integration test.");

        await using var fixture = await MinioFixture.StartAsync();

        await fixture.Cache.EnsureBucketExistsAsync(CancellationToken.None);
        await fixture.Cache.EnsureBucketExistsAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task MetadataRoundTrip()
    {
        Skip.IfNot(DockerAvailable(), "Docker is required for this integration test.");

        await using var fixture = await MinioFixture.StartAsync();
        var ownerId = Guid.NewGuid();
        fixture.Cache.EnqueueWrite(
            "segment-a",
            Encoding.ASCII.GetBytes("segment-a"),
            SegmentCategory.SmallFile,
            ownerId,
            CreateHeader("segment.bin"));

        var result = await WaitForReadResultAsync(fixture.Cache, "segment-a");
        Assert.NotNull(result);
        Assert.Equal("smallfile", result!.Metadata["x-amz-meta-category"]);
        Assert.Equal(ownerId.ToString(), result.Metadata["x-amz-meta-owner-nzb-id"]);
        Assert.True(result.Metadata.ContainsKey("x-amz-meta-yenc-header"));
    }

    private static bool DockerAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.Version}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<Stream?> WaitForReadAsync(ObjectStorageSegmentCache cache, string segmentId)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var stream = await cache.TryReadAsync(segmentId, CancellationToken.None);
            if (stream != null)
                return stream;
            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<ObjectStorageSegmentCache.ReadResult?> WaitForReadResultAsync(ObjectStorageSegmentCache cache, string segmentId)
    {
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until)
        {
            var result = await cache.TryReadWithMetadataAsync(segmentId, CancellationToken.None);
            if (result != null)
                return result;
            await Task.Delay(100);
        }

        return null;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream)
    {
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory);
        return memory.ToArray();
    }

    private static UsenetYencHeader CreateHeader(string fileName)
    {
        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = 9,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 9,
            PartOffset = 0
        };
    }
}

public sealed class MinioFixture : IAsyncDisposable
{
    private readonly string _accessKey;
    private readonly string _secretKey;
    private readonly TestcontainersContainer _container;

    private MinioFixture(string accessKey, string secretKey, TestcontainersContainer container)
    {
        _accessKey = accessKey;
        _secretKey = secretKey;
        _container = container;
        Cache = CreateCache();
    }

    public TestcontainersContainer Container => _container;
    public ObjectStorageSegmentCache Cache { get; }

    public static async Task<MinioFixture> StartAsync()
    {
        var accessKey = "nzbdav";
        var secretKey = "nzbdav-secret-123";

        var container = new TestcontainersBuilder<TestcontainersContainer>()
            .WithImage("minio/minio:latest")
            .WithName($"nzbdav-minio-{Guid.NewGuid():N}")
            .WithPortBinding(9000, true)
            .WithEnvironment("MINIO_ROOT_USER", accessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", secretKey)
            .WithCommand("server", "/data")
            .WithCleanUp(true)
            .Build();

        await container.StartAsync();
        var fixture = new MinioFixture(accessKey, secretKey, container);
        await fixture.Cache.EnsureBucketExistsAsync(CancellationToken.None);
        return fixture;
    }

    public async ValueTask DisposeAsync()
    {
        Cache.Dispose();
        await _container.DisposeAsync();
    }

    public async Task PutDirectAsync(
        string segmentId,
        byte[] body,
        SegmentCategory category,
        Guid? ownerNzbId,
        UsenetYencHeader header)
    {
        var client = CreateClient();
        using var stream = new MemoryStream(body, writable: false);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["x-amz-meta-segment-id"] = segmentId,
            ["x-amz-meta-yenc-filename"] = header.FileName,
            ["x-amz-meta-yenc-header"] = System.Text.Json.JsonSerializer.Serialize(header),
            ["x-amz-meta-category"] = category.ToString().ToLowerInvariant()
        };
        if (ownerNzbId.HasValue)
            headers["x-amz-meta-owner-nzb-id"] = ownerNzbId.Value.ToString();

        var args = new PutObjectArgs()
            .WithBucket("nzbdav-segments")
            .WithObject(ObjectStorageSegmentCache.GetObjectKey(segmentId))
            .WithStreamData(stream)
            .WithObjectSize(body.Length)
            .WithContentType("application/octet-stream")
            .WithHeaders(headers);
        await client.PutObjectAsync(args, CancellationToken.None);
    }

    private ObjectStorageSegmentCache CreateCache()
    {
        var config = new ConfigManager();
        config.UpdateValues(
        [
            new ConfigItem { ConfigName = "cache.l2.enabled", ConfigValue = "true" },
            new ConfigItem { ConfigName = "cache.l2.endpoint", ConfigValue = $"{_container.Hostname}:{_container.GetMappedPublicPort(9000)}" },
            new ConfigItem { ConfigName = "cache.l2.access-key", ConfigValue = _accessKey },
            new ConfigItem { ConfigName = "cache.l2.secret-key", ConfigValue = _secretKey },
            new ConfigItem { ConfigName = "cache.l2.bucket-name", ConfigValue = "nzbdav-segments" },
            new ConfigItem { ConfigName = "cache.l2.ssl", ConfigValue = "false" }
        ]);
        return new ObjectStorageSegmentCache(config);
    }

    private IMinioClient CreateClient()
    {
        return new MinioClient()
            .WithEndpoint($"{_container.Hostname}:{_container.GetMappedPublicPort(9000)}")
            .WithCredentials(_accessKey, _secretKey)
            .WithSSL(false)
            .Build();
    }
}

public sealed class TempCacheScope : IAsyncDisposable
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

[CollectionDefinition(nameof(ObjectStorageIntegrationCollection), DisableParallelization = true)]
public sealed class ObjectStorageIntegrationCollection;
