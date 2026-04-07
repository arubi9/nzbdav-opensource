# Spec: Shared Metadata Cache (Audit Item 9)

*Draft 2026-04-07 — suitable for handoff to Codex or another implementation agent*

Shared Postgres-backed cache for `UsenetYencHeader` data, keyed by segment
ID. Complements the per-node in-memory header cache in `LiveSegmentCache`
so that cold node startups don't have to re-fetch headers from NNTP that
another node has already retrieved.

This is the small tactical follow-up to the L2 segment body cache
(Item 8). It is OPTIONAL — Item 8 delivers most of the multi-node
bandwidth savings on its own. Item 9 closes a narrower gap: the ~200-byte
yEnc header metadata that's separately cached in memory on each node.

---

## Problem

`LiveSegmentCache` has a second cache besides the body cache:
`_headerCache` (a `Microsoft.Extensions.Caching.Memory.MemoryCache` with
a 20,000-entry size limit). It caches `UsenetYencHeader` structs keyed by
segment ID. These headers are needed for:

- Computing file sizes on directory listings (`GetYencHeadersAsync`)
- Byte-range seek support in `NzbFileStream.SeekSegment` (via
  `InterpolationSearch`)
- Cache warming when a cold node serves a stream that another node has
  already been streaming

The header cache is **per-node**. This means:

1. **Cold-start latency.** Every time a streaming node restarts, its
   header cache is empty. The first file listing or seek against each
   file pays an NNTP round-trip per segment (typically 1-5 segments per
   seek operation via interpolation search). On a library with 10,000
   files, the first "warm up" after a restart hits the NNTP provider
   for thousands of header fetches.

2. **Under-utilized 20k cap.** The 20k entry limit (about 4 MB of RAM)
   was sized for a single node. In a 3-node deployment, each node
   independently caches its own 20k entries, often overlapping. Total
   memory wasted: 8-12 MB. Not huge, but wasteful.

3. **SmallFilePrecache and MediaProbe misses.** When the ingest node
   probes segments to get their yEnc headers, those headers stay on
   the ingest node. Streaming nodes re-fetch them from NNTP on first
   access.

Unlike segment bodies, yEnc headers are tiny (~200 bytes each) and
retrieving them is cheap on a cache hit. So the cost of the per-node
design is "noticeable cold start" rather than "bandwidth catastrophe".
This spec improves it without complicating the architecture.

---

## Solution

Add a `yenc_header_cache` table in Postgres (multi-node mode only).
`LiveSegmentCache._headerCache` becomes an L1 read-through cache on top of
the shared Postgres L2:

```
┌────────────────────────────────────────────────────────────────────────┐
│                     Header Read Path                                   │
├────────────────────────────────────────────────────────────────────────┤
│                                                                        │
│  1. In-memory MemoryCache (L1) — sub-ms                                │
│        │                                                               │
│        │ miss                                                          │
│        ▼                                                               │
│  2. Postgres yenc_header_cache (L2) — ~2-5 ms                          │
│        │                                                               │
│        │ miss                                                          │
│        ▼                                                               │
│  3. NNTP fetch via GetYencHeadersAsync — ~100-300 ms                   │
│        │                                                               │
│        │ success                                                       │
│        ▼                                                               │
│  4. Write-through to BOTH L1 and L2 (synchronous)                      │
│     (write-through is fine here — the Postgres write is ~2-3 ms,       │
│      comparable to the read round-trip, and correctness of           │
│      subsequent reads depends on the write completing)                 │
│                                                                        │
└────────────────────────────────────────────────────────────────────────┘
```

### Why Postgres, not Redis

The audit discussion landed on Postgres over Redis for this tier. Reasons:

- **No new dependency.** Postgres is already deployed in multi-node mode
  for shared state (see `spec-multi-node-hardening.md`). Adding Redis
  introduces a new service to deploy, monitor, back up, and tune.
- **Performance is adequate.** Header lookups are ~2-5 ms against local
  Postgres vs sub-ms against Redis. The difference is imperceptible
  compared to the ~100-300 ms NNTP fetch we're avoiding on a hit.
- **The volume is tiny.** Even a library with 100k files × 20 segments =
  2M header entries × ~300 bytes per row = ~600 MB. Postgres handles
  that trivially, and the `yenc_header_cache` table is indexed on
  segment_id (primary key).
- **Same transactional semantics as the rest of NZBDAV's shared state.**
  No separate consistency model to reason about.

Redis could be bolted on later as an L1.5 tier between the in-memory
cache and Postgres if profiling shows Postgres is actually the
bottleneck. It's not expected to be.

### Why not the L2 object storage (Item 8)?

The Item 8 object-storage cache COULD store headers — e.g., as metadata
on the body objects, or as separate `.meta` keys. But:

- Headers are used without the body in many cases (directory listings,
  seek planning). Loading the header alone via S3 is a round-trip per
  segment vs a single batched Postgres query for multiple segments.
- S3 metadata retrieval requires a HEAD request per object; that's
  slower than a single SQL query with `WHERE segment_id = ANY(@ids)`.
- Postgres supports transactional write-through alongside other writes
  in the same session. S3 does not.

So headers go in Postgres and bodies go in object storage. The two L2
tiers serve different access patterns.

---

## Schema

Add one table. Lives in the same `backend/Database/Migrations/` pipeline
as the other multi-node hardening tables.

```sql
CREATE TABLE yenc_header_cache (
    segment_id      TEXT        PRIMARY KEY,
    file_name       TEXT        NOT NULL,
    file_size       BIGINT      NOT NULL,
    line_length     INTEGER     NOT NULL,
    part_number     INTEGER     NOT NULL,
    total_parts     INTEGER     NOT NULL,
    part_size       BIGINT      NOT NULL,
    part_offset     BIGINT      NOT NULL,
    cached_at       TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- Index for the retention sweeper — delete old entries efficiently
CREATE INDEX ix_yenc_header_cache_cached_at ON yenc_header_cache(cached_at);
```

### Column-by-column mapping

Each column corresponds to a field on `UsenetSharp.Models.UsenetYencHeader`.
The fields are flattened (not JSON-encoded) so Postgres can query
individual columns if ever needed for debugging and so the row layout is
stable across `UsenetSharp` library upgrades.

| Column | Type | `UsenetYencHeader` field |
|---|---|---|
| `segment_id` | TEXT | — (cache key) |
| `file_name` | TEXT | `FileName` |
| `file_size` | BIGINT | `FileSize` |
| `line_length` | INTEGER | `LineLength` |
| `part_number` | INTEGER | `PartNumber` |
| `total_parts` | INTEGER | `TotalParts` |
| `part_size` | BIGINT | `PartSize` |
| `part_offset` | BIGINT | `PartOffset` |
| `cached_at` | TIMESTAMPTZ | — (retention sweeper) |

**If `UsenetYencHeader` gains fields** in a future `UsenetSharp` release,
extend the schema with an `ALTER TABLE ADD COLUMN` migration. Old rows
get `NULL` in the new column; the application handles that via default
values in the read path.

### Retention

yEnc headers are immutable per segment ID (same as bodies — the
underlying NNTP article doesn't change). So the cache never needs
invalidation.

A periodic sweeper on the ingest node deletes entries older than 90 days:

```sql
DELETE FROM yenc_header_cache
WHERE cached_at < now() - INTERVAL '90 days';
```

90 days is much longer than the Item 8 body-cache retention (30 days)
because:
- Headers are ~200 bytes each — keeping more of them costs almost nothing
- Headers are reused across many operations (listings, seeks, probes) so
  they're more valuable per-byte than bodies
- The sweeper is cheap (one indexed DELETE per run)

Make the retention configurable via a new `cache.metadata-retention-days`
setting, defaulting to 90.

---

## Schema Migration

Add to the existing `AddMultiNodeCoordinationTables` migration from
`spec-multi-node-hardening.md` (if Item 9 ships together with that spec)
OR as a standalone migration if Item 9 ships independently.

```csharp
// backend/Database/Migrations/NNN_AddYencHeaderCache.cs

public partial class AddYencHeaderCache : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "yenc_header_cache",
            columns: table => new
            {
                segment_id = table.Column<string>(nullable: false),
                file_name = table.Column<string>(nullable: false),
                file_size = table.Column<long>(nullable: false),
                line_length = table.Column<int>(nullable: false),
                part_number = table.Column<int>(nullable: false),
                total_parts = table.Column<int>(nullable: false),
                part_size = table.Column<long>(nullable: false),
                part_offset = table.Column<long>(nullable: false),
                cached_at = table.Column<DateTime>(
                    nullable: false,
                    defaultValueSql: "now()")
            },
            constraints: table =>
                table.PrimaryKey("pk_yenc_header_cache", x => x.segment_id));

        migrationBuilder.CreateIndex(
            "ix_yenc_header_cache_cached_at",
            "yenc_header_cache",
            "cached_at");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("yenc_header_cache");
    }
}
```

**Only applies when using Postgres.** SQLite migrations are separate and
this one doesn't run there — the single-node path uses the existing
in-memory cache exclusively.

---

## New File: `Database/Models/YencHeaderCacheEntry.cs`

```csharp
// backend/Database/Models/YencHeaderCacheEntry.cs

namespace NzbWebDAV.Database.Models;

/// <summary>
/// Shared Postgres-backed cache row for yEnc segment headers.
/// See docs/plans/spec-metadata-cache.md.
/// </summary>
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
```

Add to `backend/Database/DavDatabaseContext.cs`:

```csharp
public DbSet<YencHeaderCacheEntry> YencHeaderCache => Set<YencHeaderCacheEntry>();

// In OnModelCreating:
b.Entity<YencHeaderCacheEntry>(e =>
{
    e.ToTable("yenc_header_cache");
    e.HasKey(x => x.SegmentId);
    e.Property(x => x.CachedAt).HasDefaultValueSql("now()");
    e.HasIndex(x => x.CachedAt);
});
```

---

## New File: `SharedHeaderCache.cs`

```csharp
// backend/Clients/Usenet/Caching/SharedHeaderCache.cs

using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

/// <summary>
/// Postgres-backed shared cache for yEnc segment headers. Acts as an L2
/// between the per-node <see cref="LiveSegmentCache"/> in-memory header
/// cache (L1) and NNTP fetches (L3). See docs/plans/spec-metadata-cache.md.
///
/// Only instantiated in multi-node Postgres mode. Single-node SQLite
/// deployments continue using just the in-memory cache.
/// </summary>
public sealed class SharedHeaderCache
{
    private long _hits;
    private long _misses;
    private long _writeFailures;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    /// <summary>
    /// Try to read a yEnc header from the shared cache. Returns null on
    /// miss or transient error. Callers fall through to the next tier
    /// (NNTP fetch) on null.
    /// </summary>
    public async Task<UsenetYencHeader?> TryReadAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var row = await dbContext.YencHeaderCache
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SegmentId == segmentId, cancellationToken)
                .ConfigureAwait(false);

            if (row == null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            Interlocked.Increment(ref _hits);
            return new UsenetYencHeader
            {
                FileName = row.FileName,
                FileSize = row.FileSize,
                LineLength = row.LineLength,
                PartNumber = row.PartNumber,
                TotalParts = row.TotalParts,
                PartSize = row.PartSize,
                PartOffset = row.PartOffset,
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Transient DB error — log and miss. Caller will fall through
            // to NNTP. We deliberately do NOT throw because this is a
            // best-effort cache, not a source of truth.
            Log.Debug(ex,
                "SharedHeaderCache read failed for segment {SegmentId} — "
                + "falling back to NNTP",
                segmentId);
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    /// <summary>
    /// Write a yEnc header to the shared cache. Uses upsert semantics so
    /// concurrent writes from different nodes don't conflict — both nodes
    /// should produce identical rows (yEnc headers are deterministic for
    /// a given segment ID) so last-write-wins is fine.
    ///
    /// Failure is logged but NOT thrown. Write-through is synchronous
    /// from the caller's perspective but the Postgres round-trip is ~2-5 ms,
    /// comparable to the read round-trip — negligible compared to the
    /// ~100-300 ms NNTP fetch that just happened.
    /// </summary>
    public async Task WriteAsync(
        string segmentId,
        UsenetYencHeader header,
        CancellationToken cancellationToken)
    {
        try
        {
            // Raw upsert SQL is simpler than EF Core's ChangeTracker
            // dance for a single-row insert-or-update pattern. Postgres
            // ON CONFLICT is native, no extension needed.
            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO yenc_header_cache
                    (segment_id, file_name, file_size, line_length,
                     part_number, total_parts, part_size, part_offset, cached_at)
                VALUES
                    ({segmentId}, {header.FileName}, {header.FileSize}, {header.LineLength},
                     {header.PartNumber}, {header.TotalParts}, {header.PartSize}, {header.PartOffset}, now())
                ON CONFLICT (segment_id) DO UPDATE SET
                    file_name = EXCLUDED.file_name,
                    file_size = EXCLUDED.file_size,
                    line_length = EXCLUDED.line_length,
                    part_number = EXCLUDED.part_number,
                    total_parts = EXCLUDED.total_parts,
                    part_size = EXCLUDED.part_size,
                    part_offset = EXCLUDED.part_offset,
                    cached_at = now();
            ", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _writeFailures);
            Log.Debug(ex,
                "SharedHeaderCache write failed for segment {SegmentId}",
                segmentId);
        }
    }
}
```

---

## Integration with `LiveSegmentCache`

`LiveSegmentCache.GetOrAddHeaderAsync` is the single read path for yEnc
headers in the codebase. It currently implements an L1 (`_headerCache`)
+ deduping Lazy pattern. We need to insert L2 between the L1 check and
the NNTP fetch.

### Modified constructor

Add an optional `SharedHeaderCache` dependency:

```csharp
public LiveSegmentCache(
    ConfigManager configManager,
    ObjectStorageSegmentCache? l2Cache = null,    // from Item 8
    SharedHeaderCache? sharedHeaderCache = null)  // NEW — Item 9
{
    // ... existing initialization ...
    _l2Cache = l2Cache;
    _sharedHeaderCache = sharedHeaderCache;
    // ...
}

private readonly SharedHeaderCache? _sharedHeaderCache;
```

### Modified `GetOrAddHeaderAsync`

Current shape (from the earlier Read of LiveSegmentCache.cs, lines 174-218):

```csharp
public async Task<UsenetYencHeader> GetOrAddHeaderAsync(
    string segmentId,
    Func<CancellationToken, Task<UsenetYencHeader>> headerFactory,
    CancellationToken cancellationToken)
{
    if (TryGetFreshEntry(segmentId, out var entry))
        return entry.YencHeaders;

    Lazy<Task<UsenetYencHeader>> lazyHeader;
    var created = false;
    lock (_headerCacheLock)
    {
        if (_headerCache.TryGetValue(segmentId, out Lazy<Task<UsenetYencHeader>>? cachedHeader))
        {
            lazyHeader = cachedHeader;
        }
        else
        {
            lazyHeader = new Lazy<Task<UsenetYencHeader>>(
                () => FetchHeaderAsync(segmentId, headerFactory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication);
            _headerCache.Set(segmentId, lazyHeader, CreateHeaderCacheOptions());
            created = true;
        }
    }
    // ... await and return ...
}
```

Insert the shared-cache check inside `FetchHeaderAsync` (the delegate
wrapped by `Lazy`), so the dedup guarantee still holds and only one
in-flight lookup per segment ID is ever active.

```csharp
private async Task<UsenetYencHeader> FetchHeaderAsync(
    string segmentId,
    Func<CancellationToken, Task<UsenetYencHeader>> headerFactory,
    CancellationToken cancellationToken)
{
    // Check the shared Postgres cache before paying an NNTP round-trip.
    if (_sharedHeaderCache != null)
    {
        var sharedHit = await _sharedHeaderCache
            .TryReadAsync(segmentId, cancellationToken)
            .ConfigureAwait(false);
        if (sharedHit.HasValue)
        {
            // Found in shared cache — return without touching NNTP. The
            // L1 cache (the Lazy in _headerCache) will remember this for
            // subsequent in-process lookups automatically.
            return sharedHit.Value;
        }
    }

    // L2 miss — fetch from NNTP via the factory (existing behavior).
    var header = await headerFactory(cancellationToken).ConfigureAwait(false);

    // Write-through to shared cache so the next cold node benefits.
    if (_sharedHeaderCache != null)
    {
        // Fire-and-forget is OK here because the L1 cache already has
        // the value locally. The shared-cache write just makes it visible
        // to other nodes, which isn't time-sensitive for the current
        // caller. A failure to write only affects future cold readers,
        // not this caller.
        _ = _sharedHeaderCache.WriteAsync(segmentId, header, CancellationToken.None);
    }

    return header;
}
```

**Note on fire-and-forget writes here vs. synchronous in the spec text
above:** the spec's "Tiered Architecture" ASCII diagram says "write-through
to BOTH L1 and L2 (synchronous)". The L1 write IS synchronous (the Lazy
stores the result). The L2 write is fire-and-forget because:
- The current caller already has the header in their hot path
- A shared-cache write failure doesn't affect correctness
- Fire-and-forget keeps the NNTP fetch path as fast as it is today

The `Task` returned by `WriteAsync` is discarded with `_ =` intentionally;
the `Log.Debug` in `WriteAsync`'s catch block handles failure observation.

---

## Configuration

### New `ConfigManager` methods

```csharp
/// <summary>
/// True when the shared metadata cache (yenc_header_cache Postgres table)
/// is enabled. Only meaningful in multi-node Postgres mode — falls through
/// to false in SQLite single-node mode.
/// </summary>
public bool IsSharedHeaderCacheEnabled()
{
    if (!MultiNodeMode.IsEnabled) return false;
    var val = StringUtil.EmptyToNull(GetConfigValue("cache.metadata-shared-enabled"));
    return val == null || bool.Parse(val);  // defaults to TRUE in multi-node mode
}

public int GetMetadataRetentionDays()
    => int.Parse(StringUtil.EmptyToNull(GetConfigValue("cache.metadata-retention-days")) ?? "90");
```

Default is ON in multi-node mode because there's no reason to leave it
off — it's cheap, has no breaking effects, and strictly improves cold
start. Operators can explicitly disable it via the config if they want
to isolate Postgres load.

### Frontend settings UI

Add to the existing "Cache" section in `frontend/app/routes/settings/webdav/webdav.tsx`:

| Field | Type | Default | Visibility |
|---|---|---|---|
| Share header cache across nodes | checkbox | true | Only shown in multi-node mode |
| Header cache retention (days) | number | 90 | Only shown in multi-node mode |

Detecting "multi-node mode" from the frontend: expose a new backend
status endpoint `GET /api/deployment-mode` that returns `{ mode: "single"
| "multi" }`, or simply hide the field unconditionally and show a note
"applies in multi-node deployments". The latter is simpler — default
behavior is fine for single-node users.

Add to `defaultConfig` in `frontend/app/routes/settings/route.tsx`:

```typescript
"cache.metadata-shared-enabled": "true",
"cache.metadata-retention-days": "90",
```

---

## DI Registration

In `backend/Program.cs`, after the `LiveSegmentCache` registration:

```csharp
.AddSingleton<SharedHeaderCache?>(sp =>
{
    var cm = sp.GetRequiredService<ConfigManager>();
    if (!cm.IsSharedHeaderCacheEnabled()) return null;
    return new SharedHeaderCache();
})
```

And update the `LiveSegmentCache` registration to consume both optional
dependencies:

```csharp
.AddSingleton<LiveSegmentCache>(sp => new LiveSegmentCache(
    sp.GetRequiredService<ConfigManager>(),
    sp.GetService<ObjectStorageSegmentCache?>(),  // optional L2 body cache
    sp.GetService<SharedHeaderCache?>()            // optional shared header cache
))
```

Note: `GetService<T?>` returns `null` if the nullable form isn't
registered. Combined with the conditional factories above, this means
the `LiveSegmentCache` operates in any of four modes:

| `ObjectStorageSegmentCache` | `SharedHeaderCache` | Mode |
|---|---|---|
| null | null | L1-only (current single-node) |
| not null | null | L1 + shared body L2 (Item 8 without Item 9) |
| null | not null | L1 + shared header L2 (Item 9 without Item 8) |
| not null | not null | Full multi-node tiering (both items) |

All four modes are valid — operators can roll out Items 8 and 9
independently.

---

## Sweeper

New `YencHeaderCacheSweeper` hosted service, ingest-only:

```csharp
// backend/Services/YencHeaderCacheSweeper.cs

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using Serilog;

namespace NzbWebDAV.Services;

/// <summary>
/// Periodic sweeper that removes expired entries from yenc_header_cache.
/// Runs only on the ingest node — streaming nodes don't need to sweep,
/// and running on all nodes would race for deletes.
///
/// Sweep interval is 1 hour and retention is configurable via
/// cache.metadata-retention-days (default 90 days). The sweeper is a
/// single DELETE query, typically sub-100ms even at large table sizes
/// because of the index on cached_at.
/// </summary>
public sealed class YencHeaderCacheSweeper(ConfigManager configManager)
    : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        try
        {
            // Run once at startup so a freshly-deployed node doesn't
            // wait an hour before its first cleanup.
            await SweepOnce(stoppingToken).ConfigureAwait(false);

            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await SweepOnce(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task SweepOnce(CancellationToken cancellationToken)
    {
        try
        {
            var retentionDays = configManager.GetMetadataRetentionDays();
            await using var dbContext = new DavDatabaseContext();
            var deleted = await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                DELETE FROM yenc_header_cache
                WHERE cached_at < now() - make_interval(days => {retentionDays});
            ", cancellationToken).ConfigureAwait(false);

            if (deleted > 0)
                Log.Debug(
                    "YencHeaderCacheSweeper removed {Count} expired entries "
                    + "(retention {Days} days)",
                    deleted, retentionDays);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "YencHeaderCacheSweeper failed");
        }
    }
}
```

Register in `Program.cs` inside the ingest-node block:

```csharp
if (NodeRoleConfig.RunsIngest && MultiNodeMode.IsEnabled)
{
    builder.Services
        // ... existing ingest-only services ...
        .AddHostedService<YencHeaderCacheSweeper>();
}
```

---

## Metrics

Extend `NzbdavMetricsCollector` with header-cache-specific counters:

```csharp
_sharedHeaderHits = metricFactory.CreateCounter(
    "nzbdav_shared_header_cache_hits_total",
    "Shared (Postgres) header cache hits");
_sharedHeaderMisses = metricFactory.CreateCounter(
    "nzbdav_shared_header_cache_misses_total",
    "Shared (Postgres) header cache misses");
_sharedHeaderWriteFailures = metricFactory.CreateCounter(
    "nzbdav_shared_header_cache_write_failures_total",
    "Shared (Postgres) header cache write failures");
```

Wire via a `Func<SharedHeaderCache?>` constructor parameter, similar to
the L2 body cache metrics in Item 8.

---

## Testing Strategy

### Unit tests (`backend.Tests/Clients/Usenet/Caching/`)

**`SharedHeaderCacheTests`** — use an in-memory Postgres (Testcontainers)
since EF Core's in-memory provider doesn't support the raw SQL upsert:

1. `TryReadAsync_ReturnsNullOnMissingRow` — no row, verify null + Misses++
2. `TryReadAsync_ReturnsPopulatedHeaderOnHit` — insert via WriteAsync,
   read via TryReadAsync, verify all fields round-trip correctly + Hits++
3. `WriteAsync_UpsertsExistingRow` — write once, write again with
   different field values, verify the second write's values are present
4. `WriteAsync_SwallowsTransientErrors` — stop Postgres mid-test, attempt
   write, verify no exception + WriteFailures++
5. `TryReadAsync_SwallowsTransientErrors` — same, for reads
6. `Concurrent_WritesSameSegmentId_LastWriterWins` — fire N concurrent
   WriteAsync calls with distinct values, verify exactly one of them
   persists and no deadlock

### Integration tests (`backend.Tests/Integration/`)

**`SharedHeaderCacheIntegrationTests`** — verify the integration with
`LiveSegmentCache.GetOrAddHeaderAsync`:

1. **Cold cache hit via shared cache:** Insert a header directly into
   Postgres (bypass the cache). Fire `GetOrAddHeaderAsync` on a fresh
   `LiveSegmentCache`. Verify the header is returned WITHOUT calling
   the NNTP factory delegate. Verify `_headerCache` now has it.
2. **Shared cache disabled passthrough:** Construct `LiveSegmentCache`
   with `sharedHeaderCache = null`. Fire `GetOrAddHeaderAsync`. Verify
   the factory delegate IS called. Verify no rows are inserted into
   Postgres.
3. **Shared cache unreachable fallthrough:** Stop Postgres. Fire
   `GetOrAddHeaderAsync`. Verify the factory is called (fallthrough to
   NNTP) and no exception is thrown to the caller.
4. **Sweeper removes expired rows:** Insert a row with
   `cached_at = now() - 100 days`. Run `SweepOnce`. Verify the row is
   gone. Insert a row with `cached_at = now() - 10 days`. Run
   `SweepOnce`. Verify the row is still present.

---

## Rollout

Item 9 is tiny and independent. Rollout can be as simple as:

1. **Deploy schema migration** (adds `yenc_header_cache` table). This is
   a no-op for SQLite deployments.
2. **Deploy the code.** `SharedHeaderCache` is registered conditionally
   on multi-node mode + the config flag. If either is off, behavior is
   unchanged from today.
3. **Monitor** `nzbdav_shared_header_cache_hits_total` vs
   `nzbdav_shared_header_cache_misses_total`. Expected: hits climb over
   the first few hours as streaming nodes fetch headers that the ingest
   node already has in the shared cache.
4. **Confirm sweeper runs** by watching for the `YencHeaderCacheSweeper
   removed N expired entries` debug log line after the first retention
   period.

No phased rollout is needed — the feature is behaviorally idempotent and
can be enabled globally.

### Interaction with Item 8

- **Item 8 alone:** Streaming nodes share body cache via S3. Header cache
  stays per-node, so cold-start header fetches still pay NNTP. Most of
  the bandwidth win is still realized because body fetches dwarf header
  fetches.
- **Item 9 alone:** Header cache is shared, bodies are not. This is a
  small incremental improvement — nodes cold-start faster because they
  don't re-fetch headers, but body cache misses still hit NNTP.
- **Both together:** Full cross-node coherency. Cold nodes warm up fast
  and serve from shared caches for both metadata and content.

The two items are additive but independent. Ship in either order.

---

## Open Questions for Review

1. **Retention of 90 days — too long?** Headers are tiny but a very
   active library could have millions of entries after a year. Postgres
   can handle it, but the sweeper runs over larger tables as time goes
   on. Alternative: 30 days (same as body cache). Trade-off: more NNTP
   round-trips for older content. My default: 90 is fine.

2. **Should the shared cache respect the L1 size limit?** Currently the
   L1 is capped at 20k entries. When L1 evicts an entry, the L2 row
   stays. That's fine — L2 has its own (time-based) lifecycle. But it
   means an operator can't "drain" the cache by restarting the node.
   Worth a documentation note rather than a behavioral change.

3. **Fire-and-forget shared write — guaranteed visible to other nodes?**
   Yes, because the write completes before the task is discarded — the
   `_ =` just ignores the Task return value. But if the writer dies
   before the write round-trips, it's lost. This is acceptable because
   the next cold read will just re-fetch from NNTP and try writing again.

4. **Why not extend `LiveSegmentCache._headerCache` size limit instead?**
   Cheaper, no new dependencies. Answer: doesn't solve cross-node
   cold-start, which is the actual problem. A 200k-entry in-memory cache
   on each node still has cold-start latency after restart — per-node
   memory is the wrong dimension to scale.

---

## Files Summary

### New Files (3)

| File | Purpose |
|---|---|
| `backend/Clients/Usenet/Caching/SharedHeaderCache.cs` | Postgres-backed shared cache |
| `backend/Database/Models/YencHeaderCacheEntry.cs` | EF Core entity |
| `backend/Services/YencHeaderCacheSweeper.cs` | Periodic cleanup (ingest only) |
| `backend/Database/Migrations/NNN_AddYencHeaderCache.cs` | Schema migration |

### Modified Files (~6)

| File | Change |
|---|---|
| `backend/Database/DavDatabaseContext.cs` | Add `YencHeaderCache` DbSet + model config |
| `backend/Clients/Usenet/Caching/LiveSegmentCache.cs` | Optional `SharedHeaderCache` dep; read-through + write-through in `FetchHeaderAsync` |
| `backend/Config/ConfigManager.cs` | 2 new getters (`IsSharedHeaderCacheEnabled`, `GetMetadataRetentionDays`) |
| `backend/Program.cs` | Conditional `SharedHeaderCache` DI + sweeper registration |
| `backend/Metrics/NzbdavMetricsCollector.cs` | 3 new counters |
| `frontend/app/routes/settings/route.tsx` | defaultConfig entries |
| `frontend/app/routes/settings/webdav/webdav.tsx` | 2 new settings fields (optional, nice-to-have) |

### Test Files (2)

| File | Coverage |
|---|---|
| `backend.Tests/Clients/Usenet/Caching/SharedHeaderCacheTests.cs` | Unit tests against Testcontainers Postgres |
| `backend.Tests/Integration/SharedHeaderCacheIntegrationTests.cs` | End-to-end via LiveSegmentCache |

---

## Non-Goals

- **Caching bodies in Postgres.** That's Item 8 (object storage), not
  this spec. Postgres is the wrong store for 750 KB × millions of
  binaries.
- **Replacing the in-memory L1.** The in-memory cache stays. It's still
  the fastest tier and Postgres can't beat a hashmap lookup.
- **Redis alternative.** Documented as "could be added later if
  profiling shows Postgres is the bottleneck". Not speced.
- **Invalidation protocol.** yEnc headers are immutable per segment ID.
  No invalidation needed, period.
- **Cross-library cache reuse.** This cache is scoped to one NZBDAV
  deployment's Postgres. Multiple NZBDAV instances do not share it.

---

## What This Unlocks

- **Fast cold-start for streaming nodes.** After a restart, a node can
  serve directory listings, computed seek targets, and file metadata
  without paying NNTP round-trips for headers that another node has
  already fetched.
- **Ingest-node probing pays off across nodes.** When the ingest node
  probes a new NZB (fetching all first-segment headers), those headers
  land in the shared cache immediately. Streaming nodes pick them up on
  first access.
- **Smaller per-node memory footprint.** The in-memory L1 can be sized
  smaller (say 5k entries) since the 20k-entry backing store now lives
  in Postgres. Optional operator tuning.
