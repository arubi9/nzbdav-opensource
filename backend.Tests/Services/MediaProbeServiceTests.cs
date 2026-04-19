using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

public sealed class MediaProbeServiceTests
{
    [Fact]
    public async Task ProcessBackfillBatch_TagsCancellationTokenWithLowPriority()
    {
        // Arrange — single video item. Contents of DavItem don't matter for
        // this test; the helper only forwards it to the processor.
        var item = MakeItem("sample.mkv");

        SemaphorePriority? observedPriority = null;
        var processorInvocations = 0;

        Task CapturePriority(DavItem _, CancellationToken ct)
        {
            processorInvocations++;
            observedPriority = ct.GetContext<DownloadPriorityContext>()?.Priority;
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();

        // Act
        await MediaProbeService.ProcessBackfillBatchAsync(new[] { item }, CapturePriority, cts.Token);

        // Assert — the helper must have tagged the ct with Low priority so any
        // DecodedBodyWithFallbackAsync calls inside the real processor flow
        // through DownloadingNntpClient at Low priority, yielding to live
        // streams at High.
        Assert.Equal(1, processorInvocations);
        Assert.NotNull(observedPriority);
        Assert.Equal(SemaphorePriority.Low, observedPriority);
    }

    [Fact]
    public async Task ProcessBackfillBatch_RemovesPriorityContextAfterCompletion()
    {
        // The `using var priorityScope` must dispose after the foreach so
        // later use of the raw token does not carry Low priority into
        // unrelated work.
        var item = MakeItem("sample.mkv");

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        await MediaProbeService.ProcessBackfillBatchAsync(
            new[] { item },
            (_, _) => Task.CompletedTask,
            token);

        // After the helper returns, the context must no longer be attached
        // to the token.
        Assert.Null(token.GetContext<DownloadPriorityContext>());
    }

    [Fact]
    public async Task ProcessBackfillBatch_SwallowsProcessorExceptionsPerItem()
    {
        // Guard: a failing item must not abort the rest of the batch.
        var items = new[]
        {
            MakeItem("a.mkv"),
            MakeItem("b.mkv"),
            MakeItem("c.mkv")
        };

        var seen = new List<string>();
        Task ThrowOnMiddle(DavItem item, CancellationToken _)
        {
            seen.Add(item.Name);
            if (item.Name == "b.mkv") throw new InvalidOperationException("boom");
            return Task.CompletedTask;
        }

        await MediaProbeService.ProcessBackfillBatchAsync(items, ThrowOnMiddle, CancellationToken.None);

        Assert.Equal(new[] { "a.mkv", "b.mkv", "c.mkv" }, seen);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstOnlyReturnsZero()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-only", 10);
        Assert.Equal(new[] { 0 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstAndLast()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-and-last", 10);
        Assert.Equal(new[] { 0, 9 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_FirstMiddleLastIsDefault()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("unknown-policy", 100);
        Assert.Equal(new[] { 0, 50, 99 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_CollapsesOnShortVideo()
    {
        // 2 segments under first-middle-last -> only 0 and 1 (distinct)
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 2);
        Assert.Equal(new[] { 0, 1 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_SingleSegmentVideo()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 1);
        Assert.Equal(new[] { 0 }, offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_EmptyReturnsEmpty()
    {
        var offsets = MediaProbeService.ResolvePrewarmOffsets("first-middle-last", 0);
        Assert.Empty(offsets);
    }

    [Fact]
    public void ResolvePrewarmOffsets_AggressivePolicy()
    {
        // first-quartile-mid-threequartile-last on 100 segments
        var offsets = MediaProbeService.ResolvePrewarmOffsets(
            "first-quartile-mid-threequartile-last", 100);
        Assert.Equal(new[] { 0, 25, 50, 75, 99 }, offsets);
    }

    private static DavItem MakeItem(string name)
    {
        var id = Guid.NewGuid();
        return new DavItem
        {
            Id = id,
            IdPrefix = id.ToString("N")[..5],
            Name = name,
            Path = "/content/" + name,
            Type = DavItem.ItemType.NzbFile,
            FileSize = 1_000_000
        };
    }
}
