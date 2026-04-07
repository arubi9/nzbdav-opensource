namespace NzbWebDAV.Clients.Usenet.Caching;

/// <summary>
/// Per-stream metadata for cache segment categorization.
/// The instance is mutable — the AsyncLocal value is the SAME instance across the
/// async chain, so calling UpgradeCategory() on the captured reference propagates
/// to all callers without re-setting _current.Value (which doesn't flow upward).
/// </summary>
public sealed class SegmentFetchContext
{
    public const string HttpContextItemKey = "_segmentFetchContext";

    private static readonly AsyncLocal<SegmentFetchContext?> _current = new();

    public SegmentCategory Category { get; private set; }
    public Guid? OwnerNzbId { get; }

    private SegmentFetchContext(SegmentCategory category, Guid? ownerNzbId)
    {
        Category = category;
        OwnerNzbId = ownerNzbId;
    }

    public static SegmentFetchContext? GetCurrent() => _current.Value;

    /// <summary>
    /// Mutate the category on this instance. Visible to all readers because they
    /// hold the same reference via AsyncLocal flow.
    /// </summary>
    public void UpgradeCategory(SegmentCategory newCategory) => Category = newCategory;

    /// <summary>
    /// Set the ambient context. Returns an IDisposable scope that restores
    /// the previous value on disposal.
    /// </summary>
    public static IDisposable Set(SegmentCategory category, Guid? ownerNzbId = null)
    {
        SetReturningContext(category, ownerNzbId, out var scope);
        return scope;
    }

    /// <summary>
    /// Set the ambient context AND return the context instance so the caller can
    /// later mutate it (via UpgradeCategory). Use this when classification may upgrade.
    /// </summary>
    public static SegmentFetchContext SetReturningContext(
        SegmentCategory category, Guid? ownerNzbId, out IDisposable scope)
    {
        var previous = _current.Value;
        var context = new SegmentFetchContext(category, ownerNzbId);
        _current.Value = context;
        scope = new ContextScope(previous);
        return context;
    }

    private sealed class ContextScope(SegmentFetchContext? previous) : IDisposable
    {
        public void Dispose() => _current.Value = previous;
    }
}
