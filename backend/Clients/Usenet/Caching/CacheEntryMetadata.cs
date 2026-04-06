using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class CacheEntryMetadata
{
    public required string SegmentId { get; init; }
    public required long SizeBytes { get; init; }
    public required long LastAccessUtcTicks { get; init; }

    // Flattened UsenetYencHeader fields
    public required string YencFileName { get; init; }
    public required long YencFileSize { get; init; }
    public required int YencLineLength { get; init; }
    public required int YencPartNumber { get; init; }
    public required int YencTotalParts { get; init; }
    public required long YencPartSize { get; init; }
    public required long YencPartOffset { get; init; }

    // Tiered eviction metadata
    public SegmentCategory Category { get; init; } = SegmentCategory.Unknown;
    public Guid? OwnerNzbId { get; init; }

    public UsenetYencHeader ToYencHeader()
    {
        return new UsenetYencHeader
        {
            FileName = YencFileName,
            FileSize = YencFileSize,
            LineLength = YencLineLength,
            PartNumber = YencPartNumber,
            TotalParts = YencTotalParts,
            PartSize = YencPartSize,
            PartOffset = YencPartOffset
        };
    }
}
