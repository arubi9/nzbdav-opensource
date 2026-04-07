namespace NzbWebDAV.Database.Models;

public sealed class ConnectionPoolClaim
{
    public required string NodeId { get; set; }
    public int ProviderIndex { get; set; }
    public int ClaimedSlots { get; set; }
    public DateTime HeartbeatAt { get; set; }
}
