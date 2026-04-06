using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Streams;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class LiveSegmentCache : IDisposable
{
    public readonly record struct BodyFetchSource(
        Stream Stream,
        UsenetYencHeader YencHeaders,
        UsenetArticleHeader? ArticleHeaders
    );

    public readonly record struct BodyFetchResult(
        UsenetDecodedBodyResponse Response,
        bool UsedExistingFetch
    );

    private sealed class CacheEntry
    {
        private UsenetArticleHeader? _articleHeaders;
        private long _lastAccessUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
        private int _referenceCount;

        public CacheEntry(
            string segmentId,
            string cachePath,
            UsenetYencHeader yencHeaders,
            long sizeBytes,
            UsenetArticleHeader? articleHeaders,
            SegmentCategory category = SegmentCategory.Unknown,
            Guid? ownerNzbId = null
        )
        {
            SegmentId = segmentId;
            CachePath = cachePath;
            YencHeaders = yencHeaders;
            SizeBytes = sizeBytes;
            _articleHeaders = articleHeaders;
            Category = category;
            OwnerNzbId = ownerNzbId;
        }

        public string SegmentId { get; }
        public string CachePath { get; }
        public UsenetYencHeader YencHeaders { get; }
        public long SizeBytes { get; }
        public SegmentCategory Category { get; }
        public Guid? OwnerNzbId { get; }
        public long LastAccessUtcTicks => Interlocked.Read(ref _lastAccessUtcTicks);
        public int ReferenceCount => Volatile.Read(ref _referenceCount);
        public UsenetArticleHeader? ArticleHeaders => Volatile.Read(ref _articleHeaders);

        public void Touch()
        {
            Interlocked.Exchange(ref _lastAccessUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        }

        public void AddReference()
        {
            Interlocked.Increment(ref _referenceCount);
            Touch();
        }

        public void ReleaseReference()
        {
            Interlocked.Decrement(ref _referenceCount);
            Touch();
        }

        public UsenetArticleHeader SetArticleHeaders(UsenetArticleHeader articleHeaders)
        {
            ArgumentNullException.ThrowIfNull(articleHeaders);
            Interlocked.CompareExchange(ref _articleHeaders, articleHeaders, null);
            Touch();
            return ArticleHeaders ?? articleHeaders;
        }

        public bool IsExpired(TimeSpan maxAge)
        {
            if (Category == SegmentCategory.SmallFile) return false;
            var age = DateTimeOffset.UtcNow - new DateTimeOffset(LastAccessUtcTicks, TimeSpan.Zero);
            return age > maxAge;
        }
    }

    private readonly ConcurrentDictionary<string, CacheEntry> _cachedSegments = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Lazy<Task<CacheEntry>>> _inflightBodies = new(StringComparer.Ordinal);
    private readonly MemoryCache _headerCache;
    private readonly SemaphoreSlim _pruneLock = new(1, 1);
    private readonly object _headerCacheLock = new();
    private long _maxCacheSizeBytes;
    private TimeSpan _maxAge;
    private bool _disposed;

    private long _cachedBytes;
    private long _hits;
    private long _misses;
    private long _dedupes;
    private long _evictions;

    public LiveSegmentCache(ConfigManager configManager)
    {
        var configuredDir = configManager.GetCacheDirectory();
        CacheDirectory = configuredDir ?? Path.Join(DavDatabaseContext.ConfigPath, "stream-cache");
        _maxCacheSizeBytes = (long)configManager.GetCacheMaxSizeGb() * 1024 * 1024 * 1024;
        _maxAge = TimeSpan.FromHours(configManager.GetCacheMaxAgeHours());
        _headerCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 20_000 });

        configManager.OnConfigChanged += (_, args) =>
        {
            if (args.ChangedConfig.ContainsKey("cache.max-size-gb") ||
                args.ChangedConfig.ContainsKey("cache.max-age-hours"))
            {
                _maxCacheSizeBytes = (long)configManager.GetCacheMaxSizeGb() * 1024 * 1024 * 1024;
                _maxAge = TimeSpan.FromHours(configManager.GetCacheMaxAgeHours());
                _ = Task.Run(() => PruneAsync());
            }

            if (args.ChangedConfig.ContainsKey("cache.directory"))
            {
                Log.Warning("Cache directory changed — restart required to take effect");
            }
        };

        InitializeAndRehydrate();
    }

    // Test-friendly constructor
    public LiveSegmentCache(
        string cacheDirectory,
        long maxCacheSizeBytes = 10L * 1024 * 1024 * 1024,
        TimeSpan? maxAge = null
    )
    {
        CacheDirectory = cacheDirectory;
        _maxCacheSizeBytes = maxCacheSizeBytes;
        _maxAge = maxAge ?? TimeSpan.FromHours(6);
        _headerCache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 20_000 });
        InitializeAndRehydrate();
    }

    public string CacheDirectory { get; }
    public long MaxCacheSizeBytes => Interlocked.Read(ref _maxCacheSizeBytes);

    public bool TryReadBody(string segmentId, out UsenetDecodedBodyResponse response)
    {
        if (!TryGetFreshEntry(segmentId, out var entry))
        {
            response = default!;
            return false;
        }

        Interlocked.Increment(ref _hits);
        response = OpenBodyResponse(segmentId, entry);
        return true;
    }

    public bool HasBody(string segmentId)
    {
        return TryGetFreshEntry(segmentId, out _);
    }

    public async Task<UsenetYencHeader> GetOrAddHeaderAsync(
        string segmentId,
        Func<CancellationToken, Task<UsenetYencHeader>> headerFactory,
        CancellationToken cancellationToken
    )
    {
        if (TryGetFreshEntry(segmentId, out var entry))
        {
            Interlocked.Increment(ref _hits);
            return entry.YencHeaders;
        }

        Lazy<Task<UsenetYencHeader>> lazyHeader;
        var created = false;

        lock (_headerCacheLock)
        {
            if (_headerCache.TryGetValue(segmentId, out Lazy<Task<UsenetYencHeader>>? cachedHeader))
            {
                lazyHeader = cachedHeader;
                Interlocked.Increment(ref _dedupes);
            }
            else
            {
                Interlocked.Increment(ref _misses);
                lazyHeader = new Lazy<Task<UsenetYencHeader>>(
                    () => FetchHeaderAsync(segmentId, headerFactory, cancellationToken),
                    LazyThreadSafetyMode.ExecutionAndPublication
                );
                _headerCache.Set(segmentId, lazyHeader, CreateHeaderCacheOptions());
                created = true;
            }
        }

        try
        {
            return await lazyHeader.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            if (created)
                _headerCache.Remove(segmentId);
            throw;
        }
    }

    public async Task<BodyFetchResult> GetOrAddBodyAsync(
        string segmentId,
        Func<CancellationToken, Task<BodyFetchSource>> fetchBodyAsync,
        CancellationToken cancellationToken,
        SegmentCategory category = SegmentCategory.Unknown,
        Guid? ownerNzbId = null
    )
    {
        if (TryReadBody(segmentId, out var cachedResponse))
            return new BodyFetchResult(cachedResponse, UsedExistingFetch: false);

        Interlocked.Increment(ref _misses);

        var createdLazy = new Lazy<Task<CacheEntry>>(
            () => FetchAndStoreBodyAsync(segmentId, fetchBodyAsync, cancellationToken, category, ownerNzbId),
            LazyThreadSafetyMode.ExecutionAndPublication
        );

        var activeLazy = _inflightBodies.GetOrAdd(segmentId, createdLazy);
        var usedExistingFetch = !ReferenceEquals(activeLazy, createdLazy);
        if (usedExistingFetch)
            Interlocked.Increment(ref _dedupes);

        if (!usedExistingFetch)
        {
            _ = activeLazy.Value.ContinueWith(
                _ => _inflightBodies.TryRemove(segmentId, out Lazy<Task<CacheEntry>>? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default
            );
        }

        var entry = await activeLazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        var response = OpenBodyResponse(segmentId, entry);
        await PruneAsync(cancellationToken).ConfigureAwait(false);
        return new BodyFetchResult(response, usedExistingFetch);
    }

    public async Task<UsenetDecodedArticleResponse> CreateArticleResponseAsync(
        string segmentId,
        Func<CancellationToken, Task<UsenetArticleHeader>> articleHeaderFactory,
        CancellationToken cancellationToken
    )
    {
        if (!TryGetFreshEntry(segmentId, out var entry))
            throw new FileNotFoundException($"The live cache does not contain segment {segmentId}.");

        var articleHeaders = entry.ArticleHeaders;
        if (articleHeaders is null)
        {
            var fetchedHeaders = await articleHeaderFactory(cancellationToken).ConfigureAwait(false);
            articleHeaders = entry.SetArticleHeaders(fetchedHeaders);
        }

        Interlocked.Increment(ref _hits);
        return OpenArticleResponse(segmentId, entry, articleHeaders);
    }

    public LiveSegmentCacheStats GetStats()
    {
        int smallFileCount = 0, videoSegmentCount = 0, unknownCount = 0;
        foreach (var pair in _cachedSegments)
        {
            switch (pair.Value.Category)
            {
                case SegmentCategory.SmallFile: smallFileCount++; break;
                case SegmentCategory.VideoSegment: videoSegmentCount++; break;
                default: unknownCount++; break;
            }
        }

        return new LiveSegmentCacheStats(
            CachedSegmentCount: _cachedSegments.Count,
            CachedBytes: Interlocked.Read(ref _cachedBytes),
            Hits: Interlocked.Read(ref _hits),
            Misses: Interlocked.Read(ref _misses),
            Dedupes: Interlocked.Read(ref _dedupes),
            Evictions: Interlocked.Read(ref _evictions),
            SmallFileCount: smallFileCount,
            VideoSegmentCount: videoSegmentCount,
            UnknownCount: unknownCount
        );
    }

    public async Task PruneAsync(CancellationToken cancellationToken = default)
    {
        await _pruneLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Pass 1: Remove expired entries (any category — SmallFile never expires by time)
            foreach (var pair in _cachedSegments.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (pair.Value.IsExpired(_maxAge))
                    TryEvict(pair.Key, pair.Value);
            }

            if (Interlocked.Read(ref _cachedBytes) <= _maxCacheSizeBytes) return;

            // Pass 2: Evict VideoSegment by LRU
            var videoEntries = _cachedSegments.ToArray()
                .Where(p => p.Value.Category == SegmentCategory.VideoSegment)
                .OrderBy(p => p.Value.LastAccessUtcTicks)
                .ToArray();
            foreach (var pair in videoEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _cachedBytes) <= _maxCacheSizeBytes) return;
                TryEvict(pair.Key, pair.Value);
            }

            // Pass 3: Evict Unknown by LRU
            var unknownEntries = _cachedSegments.ToArray()
                .Where(p => p.Value.Category == SegmentCategory.Unknown)
                .OrderBy(p => p.Value.LastAccessUtcTicks)
                .ToArray();
            foreach (var pair in unknownEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _cachedBytes) <= _maxCacheSizeBytes) return;
                TryEvict(pair.Key, pair.Value);
            }

            // Pass 4: Evict SmallFile by LRU (last resort)
            var smallFileEntries = _cachedSegments.ToArray()
                .Where(p => p.Value.Category == SegmentCategory.SmallFile)
                .OrderBy(p => p.Value.LastAccessUtcTicks)
                .ToArray();
            foreach (var pair in smallFileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Interlocked.Read(ref _cachedBytes) <= _maxCacheSizeBytes) return;
                TryEvict(pair.Key, pair.Value);
            }
        }
        finally
        {
            _pruneLock.Release();
        }
    }

    public void EvictByOwner(Guid ownerNzbId)
    {
        foreach (var pair in _cachedSegments.ToArray())
        {
            if (pair.Value.OwnerNzbId == ownerNzbId)
                TryEvict(pair.Key, pair.Value);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _headerCache.Dispose();
        _pruneLock.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<UsenetYencHeader> FetchHeaderAsync(
        string segmentId,
        Func<CancellationToken, Task<UsenetYencHeader>> headerFactory,
        CancellationToken cancellationToken
    )
    {
        var header = await headerFactory(cancellationToken).ConfigureAwait(false);
        StoreHeader(segmentId, header);
        return header;
    }

    private async Task<CacheEntry> FetchAndStoreBodyAsync(
        string segmentId,
        Func<CancellationToken, Task<BodyFetchSource>> fetchBodyAsync,
        CancellationToken cancellationToken,
        SegmentCategory category = SegmentCategory.Unknown,
        Guid? ownerNzbId = null
    )
    {
        var bodyFetch = await fetchBodyAsync(cancellationToken).ConfigureAwait(false);
        await using var bodyStream = bodyFetch.Stream;

        var tempPath = Path.Join(CacheDirectory, $"{Guid.NewGuid():N}.tmp");
        var finalPath = GetCachePath(segmentId);

        try
        {
            await using (var fileStream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81_920,
                             useAsync: true))
            {
                await bodyStream.CopyToPooledAsync(fileStream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, finalPath, overwrite: true);

            // Use ambient context if caller didn't specify
            var ctx = SegmentFetchContext.GetCurrent();
            if (category == SegmentCategory.Unknown && ctx != null)
            {
                category = ctx.Category;
                ownerNzbId ??= ctx.OwnerNzbId;
            }

            var cacheEntry = new CacheEntry(
                segmentId,
                finalPath,
                bodyFetch.YencHeaders,
                new FileInfo(finalPath).Length,
                bodyFetch.ArticleHeaders,
                category,
                ownerNzbId
            );

            if (_cachedSegments.TryAdd(segmentId, cacheEntry))
            {
                Interlocked.Add(ref _cachedBytes, cacheEntry.SizeBytes);
                StoreHeader(segmentId, bodyFetch.YencHeaders);

                // Persist metadata sidecar
                await WriteMetadataAsync(finalPath, cacheEntry).ConfigureAwait(false);

                return cacheEntry;
            }

            return _cachedSegments[segmentId];
        }
        catch
        {
            DeleteFileQuietly(tempPath);
            throw;
        }
    }

    private bool TryGetFreshEntry(string segmentId, out CacheEntry entry)
    {
        if (!_cachedSegments.TryGetValue(segmentId, out entry!))
            return false;

        if (entry.IsExpired(_maxAge) || !File.Exists(entry.CachePath))
        {
            TryEvict(segmentId, entry);
            entry = null!;
            return false;
        }

        entry.Touch();
        return true;
    }

    private UsenetDecodedBodyResponse OpenBodyResponse(string segmentId, CacheEntry entry)
    {
        var stream = OpenCachedStream(entry);
        return new UsenetDecodedBodyResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedBodyFollows,
            ResponseMessage = "222 - Article retrieved from live cache",
            Stream = new CachedYencStream(entry.YencHeaders, stream)
        };
    }

    private UsenetDecodedArticleResponse OpenArticleResponse(
        string segmentId,
        CacheEntry entry,
        UsenetArticleHeader articleHeaders
    )
    {
        var stream = OpenCachedStream(entry);
        return new UsenetDecodedArticleResponse
        {
            SegmentId = segmentId,
            ResponseCode = (int)UsenetResponseType.ArticleRetrievedHeadAndBodyFollow,
            ResponseMessage = "220 - Article retrieved from live cache",
            ArticleHeaders = articleHeaders,
            Stream = new CachedYencStream(entry.YencHeaders, stream)
        };
    }

    private Stream OpenCachedStream(CacheEntry entry)
    {
        entry.AddReference();

        var fileStream = new FileStream(
            entry.CachePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81_920,
            useAsync: true
        );

        return new DisposableCallbackStream(
            fileStream,
            onDispose: entry.ReleaseReference,
            onDisposeAsync: () =>
            {
                entry.ReleaseReference();
                return ValueTask.CompletedTask;
            }
        );
    }

    private bool TryEvict(string segmentId, CacheEntry entry)
    {
        if (entry.ReferenceCount > 0) return false;
        if (!_cachedSegments.TryRemove(new KeyValuePair<string, CacheEntry>(segmentId, entry))) return false;

        DeleteFileQuietly(entry.CachePath);
        DeleteFileQuietly(entry.CachePath + ".meta");
        _headerCache.Remove(segmentId);
        Interlocked.Add(ref _cachedBytes, -entry.SizeBytes);
        Interlocked.Increment(ref _evictions);
        return true;
    }

    private void StoreHeader(string segmentId, UsenetYencHeader header)
    {
        lock (_headerCacheLock)
        {
            _headerCache.Set(
                segmentId,
                new Lazy<Task<UsenetYencHeader>>(
                    () => Task.FromResult(header),
                    LazyThreadSafetyMode.ExecutionAndPublication
                ),
                CreateHeaderCacheOptions()
            );
        }
    }

    private MemoryCacheEntryOptions CreateHeaderCacheOptions()
    {
        return new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _maxAge,
            Size = 1
        };
    }

    private string GetCachePath(string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var filename = Convert.ToHexString(hash);
        return Path.Combine(CacheDirectory, filename);
    }

    // Feature 2: Persistent cache — rehydrate from disk instead of deleting
    private void InitializeAndRehydrate()
    {
        Directory.CreateDirectory(CacheDirectory);
        RehydrateFromDisk();
        _ = Task.Run(() => PruneAsync());
    }

    private void RehydrateFromDisk()
    {
        var rehydrated = 0;
        long rehydratedBytes = 0;
        var orphansRemoved = 0;

        try
        {
            // Find all .meta sidecar files
            var metaFiles = Directory.EnumerateFiles(CacheDirectory, "*.meta").ToArray();
            var knownBodyPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var metaPath in metaFiles)
            {
                var bodyPath = metaPath[..^5]; // strip ".meta"
                knownBodyPaths.Add(bodyPath);

                if (!File.Exists(bodyPath))
                {
                    // Orphaned .meta — delete
                    DeleteFileQuietly(metaPath);
                    orphansRemoved++;
                    continue;
                }

                try
                {
                    using var metaStream = new FileStream(
                        metaPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        bufferSize: 4096,
                        useAsync: false
                    );
                    var meta = JsonSerializer.Deserialize<CacheEntryMetadata>(metaStream);
                    if (meta is null)
                    {
                        DeleteFileQuietly(metaPath);
                        DeleteFileQuietly(bodyPath);
                        orphansRemoved++;
                        continue;
                    }

                    var yencHeader = meta.ToYencHeader();
                    var entry = new CacheEntry(
                        meta.SegmentId,
                        bodyPath,
                        yencHeader,
                        meta.SizeBytes,
                        articleHeaders: null,
                        meta.Category,
                        meta.OwnerNzbId
                    );

                    if (_cachedSegments.TryAdd(meta.SegmentId, entry))
                    {
                        Interlocked.Add(ref _cachedBytes, entry.SizeBytes);
                        StoreHeader(meta.SegmentId, yencHeader);
                        rehydrated++;
                        rehydratedBytes += entry.SizeBytes;
                    }
                }
                catch
                {
                    // Corrupt .meta — clean up
                    DeleteFileQuietly(metaPath);
                    DeleteFileQuietly(bodyPath);
                    orphansRemoved++;
                }
            }

            // Scan for orphaned body files (no matching .meta)
            var allFiles = Directory.EnumerateFiles(CacheDirectory).ToArray();
            foreach (var file in allFiles)
            {
                if (file.EndsWith(".meta") || file.EndsWith(".tmp")) continue;
                if (!knownBodyPaths.Contains(file))
                {
                    DeleteFileQuietly(file);
                    orphansRemoved++;
                }
            }
        }
        catch (Exception e)
        {
            Log.Warning($"Error rehydrating cache: {e.Message}");
        }

        if (rehydrated > 0 || orphansRemoved > 0)
        {
            var sizeMb = rehydratedBytes / (1024.0 * 1024.0);
            Log.Information($"Rehydrated {rehydrated} cache entries ({sizeMb:F1} MB), pruned {orphansRemoved} orphans");
        }
    }

    private static async Task WriteMetadataAsync(string bodyPath, CacheEntry entry)
    {
        try
        {
            var meta = new CacheEntryMetadata
            {
                SegmentId = entry.SegmentId,
                SizeBytes = entry.SizeBytes,
                LastAccessUtcTicks = entry.LastAccessUtcTicks,
                YencFileName = entry.YencHeaders.FileName,
                YencFileSize = entry.YencHeaders.FileSize,
                YencLineLength = entry.YencHeaders.LineLength,
                YencPartNumber = entry.YencHeaders.PartNumber,
                YencTotalParts = entry.YencHeaders.TotalParts,
                YencPartSize = entry.YencHeaders.PartSize,
                YencPartOffset = entry.YencHeaders.PartOffset,
                Category = entry.Category,
                OwnerNzbId = entry.OwnerNzbId
            };
            var metaTempPath = bodyPath + ".meta.tmp";
            await File.WriteAllTextAsync(metaTempPath, JsonSerializer.Serialize(meta)).ConfigureAwait(false);
            File.Move(metaTempPath, bodyPath + ".meta", overwrite: true);
        }
        catch
        {
            // Best-effort metadata persistence
        }
    }

    private static void DeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best-effort cache cleanup.
        }
    }
}
