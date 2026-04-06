namespace NzbWebDAV.Database.Models;

public class MissingSegmentId
{
    public required string SegmentId { get; init; }

    public required DateTimeOffset DetectedAt { get; init; }
}
