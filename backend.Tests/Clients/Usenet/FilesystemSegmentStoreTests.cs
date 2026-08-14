using System.Text;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Models;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class FilesystemSegmentStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nzbdav-l2-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static UsenetYencHeader BuildHeader(string fileName) => new()
    {
        FileName = fileName,
        FileSize = 55_665_932_765,
        LineLength = 128,
        PartNumber = 7,
        TotalParts = 77_659,
        PartOffset = 4_300_800,
        PartSize = 716_800,
    };

    private static ObjectStorageSegmentCache.WriteRequest BuildRequest(
        string segmentId,
        byte[] body,
        Guid? ownerNzbId = null) => new(
        segmentId,
        body,
        SegmentCategory.VideoSegment,
        ownerNzbId,
        BuildHeader("The.Matrix.1999.mkv"));

    /// <summary>
    /// The reason this backend exists. An S3 gateway that fronts a filesystem
    /// (rclone serve s3) keeps user metadata in memory only, so a restart loses
    /// every yEnc header: reads then return a body, count an L2 hit, fail to
    /// parse the header, and silently fall back to NNTP. Delegates built fresh
    /// here stand in for a process restart.
    /// </summary>
    [Fact]
    public async Task MetadataSurvivesARestartOfTheProcess()
    {
        var body = Encoding.ASCII.GetBytes("segment-body");
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");
        await FilesystemSegmentStore.CreateEnsureRootDelegate(_root)(CancellationToken.None);
        await write(BuildRequest("segment-a", body), CancellationToken.None);

        // Fresh delegates: nothing carries over in memory.
        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        var result = await read("segment-a", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(body, result!.Body);
        Assert.Equal(
            "The.Matrix.1999.mkv",
            Assert.Contains("x-amz-meta-yenc-filename", result.Metadata));

        // The header must round-trip as parseable JSON, not merely be present.
        var headerJson = Assert.Contains("x-amz-meta-yenc-header", result.Metadata);
        var header = System.Text.Json.JsonSerializer.Deserialize<UsenetYencHeader>(
            headerJson,
            ObjectStorageSegmentCache.YencHeaderJsonOptions);
        Assert.Equal(716_800, header.PartSize);
        Assert.Equal(4_300_800, header.PartOffset);
        Assert.Equal(77_659, header.TotalParts);
    }

    [Fact]
    public async Task MissingSegmentReadsAsAMiss()
    {
        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        Assert.Null(await read("never-written", CancellationToken.None));
    }

    /// <summary>
    /// A crash between the body write and the sidecar write leaves an orphaned
    /// body. Serving it would produce a counted hit that cannot reconstruct the
    /// yEnc header, so it must read as a miss and be re-fetched.
    /// </summary>
    [Fact]
    public async Task BodyWithoutItsSidecarReadsAsAMiss()
    {
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");
        await write(BuildRequest("segment-a", [1, 2, 3]), CancellationToken.None);

        var objectKey = ObjectStorageSegmentCache.GetObjectKey("segment-a");
        var bodyPath = Path.Combine(_root, objectKey.Replace('/', Path.DirectorySeparatorChar));
        File.Delete(bodyPath + ".meta");

        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        Assert.Null(await read("segment-a", CancellationToken.None));
    }

    [Fact]
    public async Task DeleteByOwnerRemovesOnlyThatOwnersSegments()
    {
        var doomed = Guid.NewGuid();
        var keeper = Guid.NewGuid();
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");

        await write(BuildRequest("doomed-1", [1], doomed), CancellationToken.None);
        await write(BuildRequest("doomed-2", [2], doomed), CancellationToken.None);
        await write(BuildRequest("keeper-1", [3], keeper), CancellationToken.None);

        await FilesystemSegmentStore.CreateDeleteByOwnerDelegate(_root)(doomed, CancellationToken.None);

        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        Assert.Null(await read("doomed-1", CancellationToken.None));
        Assert.Null(await read("doomed-2", CancellationToken.None));
        Assert.NotNull(await read("keeper-1", CancellationToken.None));
    }

    /// <summary>
    /// Deletion must stay proportional to one NZB's segments. The S3 delegate
    /// lists the entire bucket and filters client-side, which over a mounted
    /// NAS would mean walking every cached segment on every content removal.
    /// </summary>
    [Fact]
    public async Task DeleteByOwnerDoesNotScanUnrelatedSegments()
    {
        var owner = Guid.NewGuid();
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");
        await write(BuildRequest("owned", [1], owner), CancellationToken.None);
        for (var i = 0; i < 50; i++)
            await write(BuildRequest($"unowned-{i}", [2]), CancellationToken.None);

        var ownerDirectory = Path.Combine(_root, "owners", owner.ToString("N"));
        Assert.Single(Directory.EnumerateFiles(ownerDirectory));

        await FilesystemSegmentStore.CreateDeleteByOwnerDelegate(_root)(owner, CancellationToken.None);

        Assert.False(Directory.Exists(ownerDirectory));
        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        Assert.Null(await read("owned", CancellationToken.None));
        Assert.NotNull(await read("unowned-0", CancellationToken.None));
    }

    [Fact]
    public async Task RewritingASegmentLeavesNoTemporaryFiles()
    {
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");
        await write(BuildRequest("segment-a", [1, 1, 1]), CancellationToken.None);
        await write(BuildRequest("segment-a", [9, 9, 9]), CancellationToken.None);

        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));

        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        var result = await read("segment-a", CancellationToken.None);
        Assert.Equal<byte[]>([9, 9, 9], result!.Body);
    }

    [Fact]
    public async Task ConcurrentWritersForTheSameSegmentDoNotCorruptIt()
    {
        var write = FilesystemSegmentStore.CreateWriteDelegate(_root, "STANDARD");
        var body = Enumerable.Repeat((byte)7, 64 * 1024).ToArray();

        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(_ => write(BuildRequest("hot-segment", body), CancellationToken.None)));

        var read = FilesystemSegmentStore.CreateReadDelegate(_root);
        var result = await read("hot-segment", CancellationToken.None);
        Assert.Equal(body, result!.Body);
        Assert.Empty(Directory.EnumerateFiles(_root, "*.tmp", SearchOption.AllDirectories));
    }
}
