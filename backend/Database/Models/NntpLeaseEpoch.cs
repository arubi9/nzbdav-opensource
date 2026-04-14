namespace NzbWebDAV.Database.Models;

public sealed class NntpLeaseEpoch
{
    public int ProviderIndex { get; set; }
    public long Epoch { get; set; }
}
