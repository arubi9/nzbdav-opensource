namespace NzbWebDAV.Database.Models;

public class YencHeaderCacheEntry
{
    public required string SegmentId { get; set; }
    public required string FileName { get; set; }
    public required long FileSize { get; set; }
    public required int LineLength { get; set; }
    public required int PartNumber { get; set; }
    public required int TotalParts { get; set; }
    public required long PartSize { get; set; }
    public required long PartOffset { get; set; }
    public DateTime CachedAt { get; set; }
}
