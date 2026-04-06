# Phase 1: Thread Starvation & Priority Fixes

Surgical fixes for sync-over-async patterns and priority inversion. These are safe, isolated changes that don't alter architecture.

**Bottlenecks addressed:** #1, #3, #4, #5, #7

---

## Fix 1: ContentIndexSnapshotInterceptor — Remove sync-over-async

### Problem
`ContentIndexSnapshotInterceptor.SavedChanges()` (line 33) calls `PersistSnapshotAsync().GetAwaiter().GetResult()`, blocking a thread pool thread on every content-affecting `SaveChanges()`. The snapshot involves 4 DB queries + JSON serialization + file I/O behind a `SemaphoreSlim` mutex.

### File
`backend/Database/Interceptors/ContentIndexSnapshotInterceptor.cs`

### Fix
The synchronous `SavedChanges` override should fire-and-forget the snapshot write instead of blocking. The async `SavedChangesAsync` override already works correctly.

**Change `SavedChanges` (line 31-35) from:**
```csharp
public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
{
    PersistSnapshotAsync(eventData.Context, CancellationToken.None).GetAwaiter().GetResult();
    return base.SavedChanges(eventData, result);
}
```

**To:**
```csharp
public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
{
    // Fire-and-forget: snapshot persistence is best-effort recovery data,
    // not critical to the write path. The async override handles async callers correctly.
    _ = Task.Run(() => PersistSnapshotAsync(eventData.Context, CancellationToken.None));
    return base.SavedChanges(eventData, result);
}
```

**Note:** Since `PersistSnapshotAsync` already has its own try-catch and SemaphoreSlim, fire-and-forget is safe. The snapshot is recovery data — if one write is missed, the next `SaveChangesAsync` call will capture the full state.

### Verification
- Existing `ContentIndexRecoveryServiceTests` should still pass
- No new tests needed — this is a threading fix, not a behavior change

---

## Fix 2: MultiSegmentStream.Dispose(bool) — Remove sync-over-async

### Problem
`MultiSegmentStream.Dispose(bool)` (line 197-201) calls `DisposeAsyncCore().GetAwaiter().GetResult()`, blocking a thread pool thread while waiting for in-flight NNTP downloads to cancel. This is called from `NzbFileStream.Seek()`.

### File
`backend/Streams/MultiSegmentStream.cs`

### Fix
Replace the synchronous `Dispose(bool)` with a non-blocking version that signals cancellation and detaches cleanup to a background task. The key insight: we don't need to *wait* for in-flight downloads to finish during dispose — we just need to cancel them and let them clean up asynchronously.

**Change `Dispose(bool)` (lines 197-202) from:**
```csharp
protected override void Dispose(bool disposing)
{
    if (!disposing) return;
    DisposeAsyncCore().GetAwaiter().GetResult();
    base.Dispose(disposing);
}
```

**To:**
```csharp
protected override void Dispose(bool disposing)
{
    if (!disposing) return;
    lock (_disposeLock)
    {
        if (_disposed) return;
        _disposed = true;
    }

    // Signal cancellation immediately (non-blocking)
    _cts.Cancel();
    _streamTasks.Writer.TryComplete();

    // Dispose current stream synchronously if possible
    _stream?.Dispose();
    _stream = null;

    // Detach async cleanup of in-flight downloads to background
    _ = Task.Run(async () =>
    {
        try { await _downloadSegmentsTask.ConfigureAwait(false); } catch { }
        while (_streamTasks.Reader.TryRead(out var streamTask))
            await DisposePendingStreamAsync(streamTask).ConfigureAwait(false);
        _prefetchWindow.Dispose();
        _cts.Dispose();
    });

    base.Dispose(disposing);
}
```

**Also update `DisposeAsyncCore` to handle the case where `Dispose(bool)` already ran:**
The existing `DisposeAsyncCoreInternal` already checks `if (_disposed) return;` so no change needed there.

### Verification
- Run `LiveSegmentCachingNntpClientTests` — all tests should pass
- The `EvictionRemovesOnlyUnreferencedFiles` test specifically exercises disposal

---

## Fix 3: Default download priority for streaming

### Problem
`DownloadingNntpClient.AcquireExclusiveConnectionAsync` (line 97-101) defaults to `SemaphorePriority.Low` when no `DownloadPriorityContext` is set on the cancellation token. WebDAV streaming requests may not set this context, causing streaming to wait behind queue downloads.

### Files to check and modify
1. `backend/Clients/Usenet/DownloadingNntpClient.cs` — change default
2. `backend/Clients/Usenet/Contexts/DownloadPriorityContext.cs` — check existing usage
3. `backend/Queue/QueueItemProcessor.cs` — verify queue sets Low priority

### Fix
**Change the default in `DownloadingNntpClient.cs` (line 100) from:**
```csharp
var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.Low;
```

**To:**
```csharp
var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.High;
```

**Then verify** that the queue processing path explicitly sets `SemaphorePriority.Low` via `DownloadPriorityContext`. Search for `DownloadPriorityContext` usage in the codebase:
- If the queue path already sets `Low` explicitly, the fix is complete.
- If neither path sets priority explicitly, then also add `DownloadPriorityContext` with `Low` to the queue processing path in `QueueItemProcessor` where it creates the `CancellationToken`.

### Rationale
The streaming path (WebDAV GET) is user-facing and latency-sensitive. The queue path (NZB processing) is background work. Defaulting to `High` means streaming "just works" without needing explicit context, and the queue explicitly opts into `Low`.

### Verification
- No unit test needed — this is a configuration change
- Integration test: start a video stream while queue is processing, verify stream gets priority

---

## Fix 4: Sync File.ReadAllText in cache rehydration

### Problem
`LiveSegmentCache.RehydrateFromDisk()` (line 605) uses synchronous `File.ReadAllText(metaPath)` for each `.meta` file. With a large cache (10,000+ segments), this is tens of thousands of synchronous file reads at startup.

### File
`backend/Clients/Usenet/Caching/LiveSegmentCache.cs`

### Fix
`RehydrateFromDisk` is called from the constructor, which can't be async. Two options:

**Option A (recommended): Keep sync but use buffered FileStream instead of ReadAllText**
`File.ReadAllText` allocates a string per file. Since we immediately deserialize, read directly into the deserializer:

```csharp
// Replace line 605:
// var json = File.ReadAllText(metaPath);
// var meta = JsonSerializer.Deserialize<CacheEntryMetadata>(json);

// With:
using var metaStream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: false);
var meta = JsonSerializer.Deserialize<CacheEntryMetadata>(metaStream);
```

This avoids the intermediate string allocation and is more efficient for bulk reads. Using `useAsync: false` is correct here since we're in a constructor (sync context).

**Option B: Parallelize with Parallel.ForEach**
For very large caches, parallelize the meta file reads:

```csharp
var metaFiles = Directory.EnumerateFiles(CacheDirectory, "*.meta").ToArray();
var results = new ConcurrentBag<(CacheEntry entry, string bodyPath)>();

Parallel.ForEach(metaFiles, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, metaPath =>
{
    var bodyPath = metaPath[..^5];
    if (!File.Exists(bodyPath)) { /* orphan cleanup */ return; }
    try
    {
        using var stream = new FileStream(metaPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, false);
        var meta = JsonSerializer.Deserialize<CacheEntryMetadata>(stream);
        // ... build CacheEntry, add to results
    }
    catch { /* corrupt cleanup */ }
});
```

### Verification
- `LiveSegmentCacheBehaviorTests` rehydration tests should still pass
- Measure startup time with a cache directory containing 1000+ `.meta` files

---

## Fix 5: Sync fallback Read() in stream types

### Problem
Multiple stream classes have synchronous `Read()` overrides that call `.GetAwaiter().GetResult()`:
- `AesDecoderStream.cs:94`
- `CancellableStream.cs:47`
- `DavMultipartFileStream.cs:27`
- `MultipartFileStream.cs:25`

### Files
- `backend/Streams/AesDecoderStream.cs`
- `backend/Streams/CancellableStream.cs`
- `backend/Streams/DavMultipartFileStream.cs`
- `backend/Streams/MultipartFileStream.cs`

### Fix
For each file, add a `NotSupportedException` throw or keep the sync path but document the risk. Since these streams are consumed by async callers (Kestrel's response pipeline, rclone), the sync path should ideally never be hit.

**Recommended approach — throw instead of blocking:**
```csharp
public override int Read(byte[] buffer, int offset, int count)
{
    throw new NotSupportedException(
        "Synchronous Read is not supported. Use ReadAsync instead.");
}
```

**However**, if rclone or any FUSE layer calls sync `Read()`, this would break. Check the call chain:
1. Grep for `.Read(` calls on these stream types
2. If all callers use `ReadAsync`, replace with `throw`
3. If any caller uses sync `Read`, keep the `.GetAwaiter().GetResult()` but add a comment explaining why

### Verification
- Run full test suite
- Stream a video through rclone mount to verify no sync Read path is hit
- Check rclone FUSE → WebDAV → response pipeline is fully async

---

## Implementation Order

1. Fix 3 (priority inversion) — smallest change, biggest user-visible impact
2. Fix 1 (snapshot interceptor) — one-line change, eliminates worst thread blocker
3. Fix 2 (MultiSegmentStream dispose) — medium change, unblocks seek performance
4. Fix 4 (cache rehydration) — startup-only, low risk
5. Fix 5 (sync Read fallback) — needs investigation first, may be no-op

## Testing
After all fixes, run:
```bash
dotnet test backend.Tests/backend.Tests.csproj
```
All existing tests should pass unchanged.
