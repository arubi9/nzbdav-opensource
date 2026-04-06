# Plan C: Horizontal Scaling Infrastructure

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable NZBDAV to run across multiple nodes — separate streaming from ingest, share state via PostgreSQL, partition cache by content ID, and coordinate NNTP connection budgets.

**Architecture:** A single `NZBDAV_ROLE` environment variable controls which services a node runs. All nodes share a PostgreSQL database. Cache partitioning uses consistent hashing by DavItem.Id at the load balancer level (no cross-node cache queries). NNTP connection limits are enforced per-node via configuration (each node gets its own connection budget from the total provider allowance).

**Tech Stack:** .NET 10, Npgsql (EF Core PostgreSQL provider), existing DI/service infrastructure

**Prerequisites:** Plan A (observability) and Plan B (REST API + Jellyfin plugin) are merged to main.

---

## Sub-Plan C1: PostgreSQL Migration

### Task 1: Add Npgsql EF Core provider

**Files:**
- Modify: `backend/NzbWebDAV.csproj`

- [ ] **Step 1: Add package reference**

```xml
<PackageReference Include="Npgsql.EntityFrameworkCore.PostgreSQL" Version="10.0.1" />
```

- [ ] **Step 2: Restore and build**

Run: `cd backend && dotnet restore && dotnet build`

- [ ] **Step 3: Commit**

```bash
git add backend/NzbWebDAV.csproj
git commit -m "Add Npgsql EF Core provider for PostgreSQL support"
```

---

### Task 2: Make DavDatabaseContext provider-switchable

**Files:**
- Modify: `backend/Database/DavDatabaseContext.cs`

The current context hardcodes `UseSqlite`. Make it switch based on a `DATABASE_URL` environment variable. If `DATABASE_URL` is set and starts with `postgres://` or `Host=`, use Npgsql. Otherwise fall back to SQLite (backward compatible).

- [ ] **Step 1: Refactor the Options lazy initialization**

Replace the static `Options` field with a provider-aware builder:

```csharp
public static string ConfigPath => EnvironmentUtil.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
public static string DatabaseFilePath => Path.Join(ConfigPath, "db.sqlite");

private static readonly Lazy<DbContextOptions<DavDatabaseContext>> Options = new(() =>
{
    var builder = new DbContextOptionsBuilder<DavDatabaseContext>();
    var databaseUrl = EnvironmentUtil.GetEnvironmentVariable("DATABASE_URL");

    if (!string.IsNullOrEmpty(databaseUrl))
    {
        // PostgreSQL mode
        var connectionString = databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            ? ConvertPostgresUrl(databaseUrl)
            : databaseUrl;
        builder.UseNpgsql(connectionString);
    }
    else
    {
        // SQLite mode (default, backward compatible)
        builder.UseSqlite($"Data Source={DatabaseFilePath}")
            .AddInterceptors(new SqliteForeignKeyEnabler());
    }

    builder.AddInterceptors(new ContentIndexSnapshotInterceptor());
    return builder.Options;
});

private static string ConvertPostgresUrl(string url)
{
    // Convert postgres://user:pass@host:port/db to Npgsql connection string
    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':');
    return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={userInfo[0]};Password={userInfo[1]};Pooling=true;MinPoolSize=5;MaxPoolSize=50";
}
```

- [ ] **Step 2: Add using for Npgsql**

```csharp
using Npgsql.EntityFrameworkCore.PostgreSQL; // conditional usage
```

Actually, `UseNpgsql` is an extension method — just having the package reference is enough. The `using` is auto-resolved.

- [ ] **Step 3: Verify SQLite still works (no DATABASE_URL set)**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test`
Expected: All tests pass (they use SQLite).

- [ ] **Step 4: Commit**

```bash
git add backend/Database/DavDatabaseContext.cs
git commit -m "Make DavDatabaseContext provider-switchable (SQLite or PostgreSQL)"
```

---

### Task 3: Handle PostgreSQL-specific SQL

**Files:**
- Modify: `backend/Database/DavDatabaseClient.cs`

The `GetRecursiveSize` method uses a raw SQL CTE. SQLite and PostgreSQL both support recursive CTEs, but the parameter syntax differs. SQLite uses `@paramName`, PostgreSQL uses `@paramName` too (Npgsql handles this). The main issue is the `PRAGMA foreign_keys` in `SqliteForeignKeyEnabler` — this is already conditionally applied (only for SQLite via the interceptor), so no change needed there.

Check for any other raw SQL:

- [ ] **Step 1: Verify all raw SQL is provider-compatible**

Search for raw SQL:
```bash
grep -rn "FromSqlRaw\|ExecuteSqlRaw\|const.*sql\|SQL\|PRAGMA" backend/Database/ --include="*.cs"
```

The recursive CTE in `GetRecursiveSize` uses `@parentId` parameter syntax which works in both SQLite and PostgreSQL.

The `SqliteForeignKeyEnabler` interceptor runs `PRAGMA foreign_keys = ON` — this will error on PostgreSQL (FK enforcement is always on). Wrap it:

```csharp
// In SqliteForeignKeyEnabler, check if it's SQLite before running PRAGMA
public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
{
    if (connection is not Microsoft.Data.Sqlite.SqliteConnection) return;
    // ... existing PRAGMA logic
}
```

- [ ] **Step 2: Commit**

```bash
git add backend/Database/
git commit -m "Guard SQLite-specific PRAGMAs from running on PostgreSQL"
```

---

### Task 4: Generate PostgreSQL migrations

**Files:**
- May create migration files in `backend/Database/Migrations/`

- [ ] **Step 1: Test PostgreSQL connection**

With a local PostgreSQL instance:
```bash
export DATABASE_URL="Host=localhost;Database=nzbdav_test;Username=postgres;Password=postgres"
cd backend && dotnet ef database update
```

If migrations fail because they were generated for SQLite, generate PostgreSQL-compatible migrations:
```bash
dotnet ef migrations add PostgresqlInit --context DavDatabaseContext
```

**Note:** EF Core migrations are provider-specific. The existing SQLite migrations won't run on PostgreSQL. Options:
- **Option A (simpler):** Use `EnsureCreated()` for PostgreSQL (skips migrations, creates tables from model). Add to startup when PostgreSQL is detected.
- **Option B (proper):** Maintain dual migration sets (one for SQLite, one for PostgreSQL). More complex but correct.

For initial implementation, use Option A — add to `Program.cs`:
```csharp
if (!string.IsNullOrEmpty(EnvironmentUtil.GetEnvironmentVariable("DATABASE_URL")))
{
    await databaseContext.Database.EnsureCreatedAsync(SigtermUtil.GetCancellationToken())
        .ConfigureAwait(false);
}
```

- [ ] **Step 2: Commit**

```bash
git add backend/Program.cs
git commit -m "Support PostgreSQL schema creation via EnsureCreated"
```

---

## Sub-Plan C2: Node Role Separation

### Task 5: Add NZBDAV_ROLE environment variable support

**Files:**
- Modify: `backend/Program.cs`
- Create: `backend/Config/NodeRole.cs`

- [ ] **Step 1: Create NodeRole enum**

```csharp
// backend/Config/NodeRole.cs
using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public enum NodeRole
{
    Combined,   // Default: runs everything
    Streaming,  // WebDAV + REST API + streaming only (no queue processing)
    Ingest      // Queue processing + Arr monitoring only (no WebDAV serving)
}

public static class NodeRoleConfig
{
    public static NodeRole Current { get; } = ParseRole(
        EnvironmentUtil.GetEnvironmentVariable("NZBDAV_ROLE"));

    private static NodeRole ParseRole(string? value)
    {
        if (string.IsNullOrEmpty(value)) return NodeRole.Combined;
        return Enum.TryParse<NodeRole>(value, ignoreCase: true, out var role)
            ? role
            : NodeRole.Combined;
    }

    public static bool RunsStreaming => Current is NodeRole.Combined or NodeRole.Streaming;
    public static bool RunsIngest => Current is NodeRole.Combined or NodeRole.Ingest;
}
```

- [ ] **Step 2: Conditionally register services in Program.cs**

Wrap the ingest-only services:
```csharp
// Always register (needed by both roles)
.AddSingleton<LiveSegmentCache>()
.AddSingleton<UsenetStreamingClient>()
.AddSingleton<StreamExecutionService>()
.AddSingleton<NzbdavMetricsCollector>()
.AddSingleton<ReadAheadWarmingService>()

// Ingest-only services
if (NodeRoleConfig.RunsIngest)
{
    builder.Services
        .AddSingleton<QueueManager>()
        .AddHostedService<HealthCheckService>()
        .AddHostedService<ArrMonitoringService>()
        .AddHostedService<BlobCleanupService>()
        .AddHostedService<SmallFilePrecacheService>();
}

// Streaming nodes still need QueueManager registered (for metrics) but don't process
if (!NodeRoleConfig.RunsIngest)
{
    // Register a no-op QueueManager or make it optional
    builder.Services.AddSingleton<QueueManager>();
    // QueueManager won't process because it checks a flag
}
```

**Note:** `QueueManager` starts processing in its constructor (`_ = ProcessQueueAsync()`). To prevent streaming nodes from processing, add a guard:

```csharp
// In QueueManager constructor:
if (!NodeRoleConfig.RunsIngest)
    return; // Don't start the processing loop on streaming-only nodes
```

- [ ] **Step 3: Log the active role on startup**

```csharp
Log.Information("NZBDAV starting in {Role} mode", NodeRoleConfig.Current);
```

- [ ] **Step 4: Commit**

```bash
git add backend/Config/NodeRole.cs backend/Program.cs backend/Queue/QueueManager.cs
git commit -m "Add NZBDAV_ROLE for node role separation (streaming/ingest/combined)"
```

---

## Sub-Plan C3: Cache Partitioning Strategy

### Task 6: Document load balancer configuration for cache affinity

**Files:**
- Create: `docs/deployment/load-balancer.md`

Cache partitioning is handled at the **infrastructure level**, not in application code. The NZBDAV application doesn't need changes — the load balancer routes requests for the same content to the same node.

- [ ] **Step 1: Write load balancer configuration documentation**

```markdown
# Load Balancer Configuration for NZBDAV Horizontal Scaling

## Cache Affinity Routing

Route requests for the same content to the same NZBDAV streaming node
so that LiveSegmentCache hits are maximized.

### HAProxy (recommended)

    backend nzbdav_streaming
        balance uri           # Hash by request URI
        hash-type consistent  # Consistent hashing — adding nodes only remaps 1/N of keys
        server nzbdav1 10.0.0.11:8080 check
        server nzbdav2 10.0.0.12:8080 check
        server nzbdav3 10.0.0.13:8080 check

### Nginx

    upstream nzbdav_streaming {
        hash $request_uri consistent;
        server 10.0.0.11:8080;
        server 10.0.0.12:8080;
        server 10.0.0.13:8080;
    }

### Traefik (docker labels)

    labels:
      - "traefik.http.services.nzbdav.loadbalancer.strategy=consistent-hash"
      - "traefik.http.services.nzbdav.loadbalancer.sticky.cookie=true"

## Endpoint Routing

| Path pattern | Route to |
|---|---|
| `/api/*`, WebDAV PROPFIND/GET/HEAD | Streaming nodes (consistent hash by URI) |
| `/api?mode=addfile`, `/api?mode=addurl` | Ingest node (direct) |
| `/health`, `/metrics` | All nodes (round-robin) |
| `/ws` | Any node (WebSocket sticky) |

## Private Network

All NZBDAV ↔ load balancer traffic should use the private vRack network
(10.0.0.0/24) to avoid public bandwidth charges and reduce latency.
```

- [ ] **Step 2: Commit**

```bash
git add docs/deployment/load-balancer.md
git commit -m "Add load balancer configuration guide for cache partitioning"
```

---

## Sub-Plan C4: NNTP Connection Budget

### Task 7: Per-node connection limit via configuration

**Files:**
- Modify: `backend/Config/ConfigManager.cs`

NNTP providers typically allow 30-50 connections per account. With multiple nodes, each node gets a share. The simplest approach: each node configures its own `usenet.max-download-connections` to be `total_provider_connections / number_of_streaming_nodes`.

No code change is needed — this is already configurable. Document the operational practice:

- [ ] **Step 1: Add connection budget documentation**

Add to `docs/deployment/load-balancer.md`:

```markdown
## NNTP Connection Budget

Each NZBDAV streaming node manages its own NNTP connection pool.
Split your provider's total connection limit across nodes:

| Provider limit | Nodes | Per-node setting |
|---|---|---|
| 30 connections | 2 streaming | `usenet.max-download-connections = 15` |
| 50 connections | 3 streaming | `usenet.max-download-connections = 16` |
| 50 connections | 5 streaming | `usenet.max-download-connections = 10` |

Set via NZBDAV settings UI or environment variable.
Leave 2-3 connections of headroom per node for health checks.

### Alternative: Separate provider accounts

For true isolation, give each streaming node its own NNTP provider
account. This eliminates connection budget coordination entirely
but costs more from the provider.
```

- [ ] **Step 2: Commit**

```bash
git add docs/deployment/load-balancer.md
git commit -m "Document NNTP connection budget strategy for multi-node"
```

---

### Task 8: Move HealthCheckService._missingSegmentIds from static to shared state

**Files:**
- Modify: `backend/Services/HealthCheckService.cs`

The static `_missingSegmentIds` HashSet is process-local. On multi-node, each node has its own incomplete view. Move it to the database.

- [ ] **Step 1: Create MissingSegment table**

Add to `DavDatabaseContext.OnModelCreating`:

```csharp
b.Entity<MissingSegmentId>(e =>
{
    e.ToTable("MissingSegmentIds");
    e.HasKey(i => i.SegmentId);
    e.Property(i => i.SegmentId).HasMaxLength(512);
    e.Property(i => i.DetectedAt).IsRequired();
});
```

Create the model:
```csharp
// backend/Database/Models/MissingSegmentId.cs
namespace NzbWebDAV.Database.Models;

public class MissingSegmentId
{
    public required string SegmentId { get; init; }
    public required DateTimeOffset DetectedAt { get; init; }
}
```

- [ ] **Step 2: Replace static HashSet with DB queries in HealthCheckService**

Replace `_missingSegmentIds` usage:

```csharp
// Instead of:
// lock (_missingSegmentIds) _missingSegmentIds.Add(e.SegmentId);

// Use:
dbClient.Ctx.Set<MissingSegmentId>().Add(new MissingSegmentId
{
    SegmentId = e.SegmentId,
    DetectedAt = DateTimeOffset.UtcNow
});
await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);
```

```csharp
// Instead of:
// public static void CheckCachedMissingSegmentIds(IEnumerable<string> segmentIds)
// {
//     lock (_missingSegmentIds) { foreach ... if (_missingSegmentIds.Contains(segmentId)) throw; }
// }

// Use an instance method that queries the DB:
public static async Task CheckMissingSegmentIdsAsync(
    DavDatabaseContext dbContext,
    IEnumerable<string> segmentIds,
    CancellationToken ct)
{
    var ids = segmentIds.ToList();
    var missing = await dbContext.Set<MissingSegmentId>()
        .Where(m => ids.Contains(m.SegmentId))
        .Select(m => m.SegmentId)
        .FirstOrDefaultAsync(ct)
        .ConfigureAwait(false);
    if (missing != null)
        throw new UsenetArticleNotFoundException(missing);
}
```

- [ ] **Step 3: Add migration**

```bash
cd backend && dotnet ef migrations add AddMissingSegmentIds
```

- [ ] **Step 4: Run tests**

```bash
cd backend.Tests && dotnet test
```

- [ ] **Step 5: Commit**

```bash
git add backend/Database/ backend/Services/HealthCheckService.cs
git commit -m "Move missing segment tracking from static HashSet to database"
```

---

### Task 9: Integration verification

- [ ] **Step 1: Test combined mode (default)**

```bash
unset NZBDAV_ROLE
unset DATABASE_URL
cd backend && dotnet run
# Should start normally, all services active
```

- [ ] **Step 2: Test streaming mode**

```bash
export NZBDAV_ROLE=streaming
cd backend && dotnet run
# Should start without queue processing
# /metrics should show nzbdav_queue_processing 0
```

- [ ] **Step 3: Test ingest mode**

```bash
export NZBDAV_ROLE=ingest
cd backend && dotnet run
# Should start with queue processing active
```

- [ ] **Step 4: Test PostgreSQL mode** (requires PostgreSQL instance)

```bash
export DATABASE_URL="Host=localhost;Database=nzbdav_test;Username=postgres;Password=postgres"
cd backend && dotnet run
# Should create tables and start normally
```

- [ ] **Step 5: Final commit**

```bash
git add -A
git commit -m "Plan C complete: horizontal scaling with PostgreSQL, node roles, cache partitioning"
```
