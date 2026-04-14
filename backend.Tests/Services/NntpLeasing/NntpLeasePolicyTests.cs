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
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, true)
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
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, false)
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
                new NntpLeasePolicy.LeaseHeartbeat("stream-1", NntpLeaseNodeRole.Streaming, false),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-1", NntpLeaseNodeRole.Ingest, true)
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
                new NntpLeasePolicy.LeaseHeartbeat("stream-c", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("stream-a", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("stream-b", NntpLeaseNodeRole.Streaming, true)
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
                new NntpLeasePolicy.LeaseHeartbeat("stream-a", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("stream-b", NntpLeaseNodeRole.Streaming, true),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-a", NntpLeaseNodeRole.Ingest, true),
                new NntpLeasePolicy.LeaseHeartbeat("ingest-b", NntpLeaseNodeRole.Ingest, true)
            ],
            currentEpoch: 42);

        Assert.Equal(6, grants.Single(x => x.NodeId == "stream-a").GrantedSlots);
        Assert.Equal(5, grants.Single(x => x.NodeId == "stream-b").GrantedSlots);
        Assert.Equal(3, grants.Single(x => x.NodeId == "ingest-a").GrantedSlots);
        Assert.Equal(2, grants.Single(x => x.NodeId == "ingest-b").GrantedSlots);
        Assert.Equal(16, grants.Sum(x => x.GrantedSlots));
        Assert.True(grants.Sum(x => x.GrantedSlots) <= 17);
    }
}
