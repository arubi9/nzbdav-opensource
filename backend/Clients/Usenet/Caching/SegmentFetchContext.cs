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
