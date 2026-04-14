using NzbWebDAV.Config;

namespace NzbWebDAV.Database.Models;

public sealed class NntpConnectionLease
{
    public required string NodeId { get; set; }
    public int ProviderIndex { get; set; }
    public NodeRole Role { get; set; }
    public int ReservedSlots { get; set; }
    public int BorrowedSlots { get; set; }
    public int GrantedSlots { get; set; }
    public long Epoch { get; set; }
    public DateTime LeaseUntil { get; set; }
    public DateTime UpdatedAt { get; set; }
}
