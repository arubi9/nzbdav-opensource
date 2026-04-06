# Phase 3: Content Index Snapshot Optimization

Reduce the cost of the content index recovery snapshot from O(entire library) on every write to debounced incremental updates.

**Bottleneck addressed:** #6
**Depends on:** Phase 1 Fix 1 (snapshot interceptor is no longer sync-blocking)

---

## Problem

`ContentIndexSnapshotStore.WriteAsync()` queries the **entire** `/content/` tree on every content-affecting `SaveChanges`:
1. `SELECT * FROM DavItems WHERE Path LIKE '/content/%'` → all items
2. `SELECT * FROM DavNzbFiles WHERE Id IN (...)` → all NZB file metadata
3. `SELECT * FROM DavRarFiles WHERE Id IN (...)` → all RAR file metadata
4. `SELECT * FROM DavMultipartFiles WHERE Id IN (...)` → all multipart file metadata
5. Serialize entire tree to JSON
6. Write to disk (atomic temp-file swap)
7. Copy to backup

With 50,000 items, this is ~500ms+ per snapshot. During batch NZB processing (e.g., Radarr importing 20 movies), `SaveChanges` is called per NZB, generating 20 full snapshots in rapid succession.

### File
`backend/Services/ContentIndexSnapshotStore.cs`
`backend/Database/Interceptors/ContentIndexSnapshotInterceptor.cs`

---

## Approach: Debounced Coalescing Snapshot

Instead of writing a snapshot on every `SaveChanges`, debounce: mark that a snapshot is needed, and write it after a configurable quiet period (e.g., 5 seconds of no further content changes).

### Design

**New `DebouncedSnapshotWriter` class** (replaces direct calls from the interceptor):

```csharp
public sealed class DebouncedSnapshotWriter : IDisposable
{
    private readonly TimeSpan _debounceInterval;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _debounceCts;
    private readonly object _triggerLock = new();
    private int _pendingCount;

    public DebouncedSnapshotWriter(TimeSpan? debounceInterval = null)
    {
        _debounceInterval = debounceInterval ?? TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Signal that content has changed and a snapshot is needed.
    /// Coalesces multiple calls within the debounce window into a single write.
    /// </summary>
    public void MarkDirty()
    {
        lock (_triggerLock)
        {
            Interlocked.Increment(ref _pendingCount);
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            var token = _debounceCts.Token;
            _ = Task.Run(() => DebouncedWriteAsync(token));
        }
    }

    private async Task DebouncedWriteAsync(CancellationToken debounceToken)
    {
        try
        {
            // Wait for quiet period — if MarkDirty is called again, this token is cancelled
            await Task.Delay(_debounceInterval, debounceToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Another MarkDirty superseded us
        }

        // Quiet period elapsed — write the snapshot
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _pendingCount, 0);
            await using var dbContext = new DavDatabaseContext();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to write debounced content index snapshot.");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Force an immediate snapshot write (e.g., on app shutdown).
    /// </summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _pendingCount, 0, 0) == 0) return;

        lock (_triggerLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var dbContext = new DavDatabaseContext();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, cancellationToken)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _pendingCount, 0);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        lock (_triggerLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
        }
        _writeLock.Dispose();
    }
}
```

### Changes to `ContentIndexSnapshotInterceptor`

The interceptor should call `MarkDirty()` instead of writing the snapshot directly.

```csharp
public sealed class ContentIndexSnapshotInterceptor : SaveChangesInterceptor
{
    // Singleton debounced writer — shared across all DbContext instances
    internal static readonly DebouncedSnapshotWriter SnapshotWriter = new();

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (HasContentIndexChanges(eventData.Context))
            SnapshotWriter.MarkDirty();
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (HasContentIndexChanges(eventData.Context))
            SnapshotWriter.MarkDirty();
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    // Remove SavedChanges and SavedChangesAsync overrides entirely —
    // snapshot writing is now decoupled from the SaveChanges lifecycle.

    // Remove SaveChangesFailed overrides — MarkDirty is idempotent,
    // a failed save just means the snapshot will reflect the pre-failure state.

    // Keep HasContentIndexChanges as-is.
}
```

### Shutdown flush

In `Program.cs`, flush pending snapshots on shutdown:

```csharp
app.Lifetime.ApplicationStopping.Register(() =>
{
    SigtermUtil.Cancel();
    ContentIndexSnapshotInterceptor.SnapshotWriter.FlushAsync(CancellationToken.None)
        .GetAwaiter().GetResult();
});
```

### DI registration (optional improvement)

Instead of a static singleton, register `DebouncedSnapshotWriter` in DI:

```csharp
builder.Services.AddSingleton<DebouncedSnapshotWriter>();
```

Then the interceptor receives it via `IServiceProvider`. This is cleaner but requires changing how the interceptor is constructed (it's currently added via `AddInterceptors()` which doesn't support DI). The static approach works and is simpler.

---

## Configurable debounce interval

Add to `ConfigManager.cs`:

```csharp
public int GetSnapshotDebounceSeconds()
    => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.snapshot-debounce-seconds")) ?? "5");
```

Wire this to `DebouncedSnapshotWriter` construction. For most users, 5 seconds is fine — it means during a batch import of 20 NZBs, only 1-2 snapshots are written instead of 20.

---

## New file

`backend/Services/DebouncedSnapshotWriter.cs`

## Modified files

- `backend/Database/Interceptors/ContentIndexSnapshotInterceptor.cs` — simplify to just call `MarkDirty()`
- `backend/Program.cs` — add shutdown flush
- `backend/Config/ConfigManager.cs` — add debounce config (optional)

---

## Verification

- Existing `ContentIndexRecoveryServiceTests` should pass — snapshot content is unchanged, only timing differs
- New test: call `MarkDirty()` 10 times in 1 second, verify only 1 snapshot file is written
- New test: call `FlushAsync()`, verify snapshot is written immediately
- Integration: process 5 NZBs rapidly, verify snapshot file is written once after quiet period

---

## Risk Assessment

**Low risk.** The snapshot is recovery data — it's only used if the database is lost/corrupted. Missing a snapshot during a narrow window (the debounce delay) means at worst losing the last 5 seconds of content changes in a disaster recovery scenario. Since the snapshot is a full tree dump (not incremental), the next successful write captures everything.

The only failure mode to watch for: if the app crashes during the debounce delay, the pending snapshot is lost. Mitigation: the existing backup snapshot (`content-index.snapshot.backup.json`) provides a second copy, and the `ContentIndexRecoveryService` already handles missing/stale snapshots gracefully.
