namespace NzbWebDAV.Clients.Usenet.Caching;

public readonly record struct LiveSegmentCacheStats(
    int CachedSegmentCount,
    long CachedBytes,
    long Hits,
    long Misses,
    long Dedupes,
    long Evictions,
    int SmallFileCount,
    int VideoSegmentCount,
    int UnknownCount
);
