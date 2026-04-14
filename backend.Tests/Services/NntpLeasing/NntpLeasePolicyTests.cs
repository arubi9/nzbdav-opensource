using NzbWebDAV.Services.NntpLeasing;

namespace backend.Tests.Services.NntpLeasing;

public sealed class NntpLeasePolicyTests
{
    [Fact]
    public void Compute_WhenBothRolesHaveDemand_SplitsSeventyThirty()
    {
        var grants = NntpLeasePolicy.Compute(
            100,
            [
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, true, 100),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, true, 100)
            ],
            currentEpoch: 42);

        Assert.Equal(70, grants.Single(x => x.NodeId == "stream-1").GrantedSlots);
        Assert.Equal(30, grants.Single(x => x.NodeId == "ingest-1").GrantedSlots);
        Assert.Equal(100, grants.Sum(x => x.GrantedSlots));
    }

    [Fact]
    public void Compute_WhenIngestIsIdle_GivesStreamingAllCapacity()
    {
        var grants = NntpLeasePolicy.Compute(
            100,
            [
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, true, 100),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, false, 100)
            ],
            currentEpoch: 42);

        Assert.Equal(100, grants.Single(x => x.NodeId == "stream-1").GrantedSlots);
        Assert.Equal(0, grants.Single(x => x.NodeId == "ingest-1").GrantedSlots);
        Assert.Equal(100, grants.Sum(x => x.GrantedSlots));
    }

    [Fact]
    public void Compute_WhenStreamingIsIdle_GivesIngestAllCapacity()
    {
        var grants = NntpLeasePolicy.Compute(
            100,
            [
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, false, 100),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, true, 100)
            ],
            currentEpoch: 42);

        Assert.Equal(0, grants.Single(x => x.NodeId == "stream-1").GrantedSlots);
        Assert.Equal(100, grants.Single(x => x.NodeId == "ingest-1").GrantedSlots);
        Assert.Equal(100, grants.Sum(x => x.GrantedSlots));
    }

    [Fact]
    public void Compute_WhenMultipleNodesShareARole_UsesStableNodeIdOrdering()
    {
        var grants = NntpLeasePolicy.Compute(
            10,
            [
                new NntpLeasePolicy.LeaseHeartbeat("stream-c", NntpLeaseNodeRole.Streaming, true, 10),
                new NntpLeasePolicy.LeaseHeartbeat("stream-a", NntpLeaseNodeRole.Streaming, true, 10),
                new NntpLeasePolicy.LeaseHeartbeat("stream-b", NntpLeaseNodeRole.Streaming, true, 10)
            ],
            currentEpoch: 42);

        Assert.Equal(4, grants.Single(x => x.NodeId == "stream-a").GrantedSlots);
        Assert.Equal(3, grants.Single(x => x.NodeId == "stream-b").GrantedSlots);
        Assert.Equal(3, grants.Single(x => x.NodeId == "stream-c").GrantedSlots);
    }

    [Fact]
    public void Compute_NeverGrantsMoreThanTotalSlots()
    {
        var grants = NntpLeasePolicy.Compute(
            17,
            [
                new NntpLeasePolicy.LeaseHeartbeat("stream-a", NntpLeaseNodeRole.Streaming, true, 10),
                new NntpLeasePolicy.LeaseHeartbeat("stream-b", NntpLeaseNodeRole.Streaming, true, 10),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-a", NntpLeaseNodeRole.Ingest, true, 10),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-b", NntpLeaseNodeRole.Ingest, true, 10)
            ],
            currentEpoch: 42);

        Assert.Equal(17, grants.Sum(x => x.GrantedSlots));
        Assert.True(grants.Sum(x => x.GrantedSlots) <= 17);
    }
}
