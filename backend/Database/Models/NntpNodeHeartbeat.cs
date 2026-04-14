using NzbWebDAV.Config;

namespace NzbWebDAV.Database.Models;

public sealed class NntpNodeHeartbeat
{
    public required string NodeId { get; set; }
    public int ProviderIndex { get; set; }
    public NodeRole Role { get; set; }
    public required string Region { get; set; }
    public int DesiredSlots { get; set; }
    public int ActiveSlots { get; set; }
    public int LiveSlots { get; set; }
    public bool HasDemand { get; set; }
    public DateTime HeartbeatAt { get; set; }
}
