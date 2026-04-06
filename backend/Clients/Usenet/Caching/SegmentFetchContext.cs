namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class SegmentFetchContext
{
    public const string HttpContextItemKey = "_segmentFetchContext";

    private static readonly AsyncLocal<SegmentFetchContext?> _current = new();

    public SegmentCategory Category { get; }
    public Guid? OwnerNzbId { get; }

    private SegmentFetchContext(SegmentCategory category, Guid? ownerNzbId)
    {
        Category = category;
        OwnerNzbId = ownerNzbId;
    }

    public static SegmentFetchContext? GetCurrent() => _current.Value;

    /// <summary>
    /// Update the category of the current async-local context.
    /// Used when StreamClassifier commits from Unknown (SmallFile default) to Playback (VideoSegment).
    /// </summary>
    public static void UpdateCurrentCategory(SegmentCategory newCategory)
    {
        if (_current.Value is not null)
            _current.Value = new SegmentFetchContext(newCategory, _current.Value.OwnerNzbId);
    }

    public static IDisposable Set(SegmentCategory category, Guid? ownerNzbId = null)
    {
        var previous = _current.Value;
        _current.Value = new SegmentFetchContext(category, ownerNzbId);
        return new ContextScope(previous);
    }

    private sealed class ContextScope(SegmentFetchContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
