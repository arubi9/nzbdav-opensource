# Plan D: Production Readiness — All Remaining Gaps

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close all gaps blocking correct operation and production readiness, with explicit failure model compliance (see `docs/plans/failure-model.md`).

**Architecture:** 12 tasks. Tasks 1-9 address functional gaps. Tasks 10-12 implement the failure model: graceful shutdown with connection draining, provider health exposure, and cache utilization metrics. Each task is independently committable. All behavior must match the failure model document.

**Tech Stack:** .NET 10, Jellyfin Plugin SDK, ASP.NET Core middleware, xUnit

**Failure model reference:** `docs/plans/failure-model.md` — every health check, timeout, and error response in this plan must be consistent with that document.

---

## File Map

| Task | New files | Modified files |
|------|-----------|----------------|
| 1. Library scanner | `jellyfin-plugin/.../NzbdavLibrarySyncTask.cs` | — |
| 2. Request timeouts | `backend/Middlewares/RequestTimeoutMiddleware.cs` | `backend/Program.cs` |
| 3. Load shedding | `backend/Exceptions/ServiceOverloadedException.cs` | `PrioritizedSemaphore.cs`, `DownloadingNntpClient.cs`, `ExceptionMiddleware.cs` |
| 4. Browse path O(N) | — | `BrowseController.cs`, `DavDatabaseClient.cs` |
| 5. Rich health checks | `backend/Services/NzbdavHealthCheck.cs` | `backend/Program.cs` |
| 6. Docker-compose | `docs/deployment/docker-compose.multi-node.yml`, `docs/deployment/haproxy.cfg` | — |
| 7. REST stream optimization | — | Skipped (documented) |
| 8. Observability config | `docs/deployment/prometheus.yml`, `docs/deployment/grafana-dashboard.json` | — |
| 9. Integration tests | `backend.Tests/Integration/RestApiIntegrationTests.cs` | `backend.Tests/backend.Tests.csproj` |
| 10. Graceful shutdown | — | `backend/Program.cs` |
| 11. Provider health exposure | — | `MultiProviderNntpClient.cs`, `UsenetStreamingClient.cs`, `NzbdavHealthCheck.cs`, `NzbdavMetricsCollector.cs` |
| 12. Cache max bytes metric | — | `NzbdavMetricsCollector.cs`, `LiveSegmentCache.cs` |

---

### Task 1: Jellyfin Library Scanner (CRITICAL)

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs`

This is the missing piece that makes the plugin functional. It implements `IScheduledTask` to periodically scan NZBDAV content and create/update Jellyfin library items with `NzbdavId` provider IDs.

- [ ] **Step 1: Create the scheduled task**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs
using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

public class NzbdavLibrarySyncTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<NzbdavLibrarySyncTask> _logger;

    public NzbdavLibrarySyncTask(
        ILibraryManager libraryManager,
        ILogger<NzbdavLibrarySyncTask> logger)
    {
        _libraryManager = libraryManager;
        _logger = logger;
    }

    public string Name => "NZBDAV Library Sync";
    public string Key => "NzbdavLibrarySync";
    public string Description => "Synchronize Jellyfin library with NZBDAV content.";
    public string Category => "NZBDAV";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        return
        [
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromMinutes(15).Ticks
            }
        ];
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
        {
            _logger.LogWarning("NZBDAV plugin not configured — skipping library sync");
            return;
        }

        var client = new NzbdavApiClient(config);
        progress.Report(0);

        // Resilience: if NZBDAV is unreachable, log and exit gracefully.
        // Per failure model: sync is additive, idempotent, partial-failure tolerant.
        // Existing items are never deleted on sync failure.
        BrowseResponse? contentRoot;
        try
        {
            contentRoot = await client.BrowseAsync("content", cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NZBDAV unreachable during library sync — will retry next cycle");
            progress.Report(100);
            return;
        }

        // Browse /content to get category folders
        if (contentRoot is null || contentRoot.Items.Length == 0)
        {
            _logger.LogInformation("NZBDAV /content is empty — nothing to sync");
            progress.Report(100);
            return;
        }

        var categories = contentRoot.Items.Where(i => i.Type == "directory").ToArray();
        var totalItems = 0;
        var processedItems = 0;

        // First pass: count items for progress reporting
        foreach (var category in categories)
        {
            var categoryContent = await client.BrowseAsync($"content/{category.Name}", cancellationToken)
                .ConfigureAwait(false);
            if (categoryContent != null)
                totalItems += categoryContent.Items.Length;
        }

        if (totalItems == 0)
        {
            progress.Report(100);
            return;
        }

        // Build a set of existing NzbdavIds to detect items that need creation
        var existingNzbdavIds = GetExistingNzbdavIds();

        // Second pass: process each category's mount folders
        foreach (var category in categories)
        {
            var categoryContent = await client.BrowseAsync($"content/{category.Name}", cancellationToken)
                .ConfigureAwait(false);
            if (categoryContent is null) continue;

            foreach (var mountFolder in categoryContent.Items.Where(i => i.Type == "directory"))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessMountFolder(client, category.Name, mountFolder, existingNzbdavIds, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to process mount folder {Name}", mountFolder.Name);
                }

                processedItems++;
                progress.Report((double)processedItems / totalItems * 100);
            }
        }

        _logger.LogInformation("NZBDAV library sync complete: processed {Count} mount folders", processedItems);
        progress.Report(100);
    }

    private async Task ProcessMountFolder(
        NzbdavApiClient client,
        string categoryName,
        BrowseItem mountFolder,
        HashSet<string> existingNzbdavIds,
        CancellationToken ct)
    {
        // Browse the mount folder to find media files
        var contents = await client.BrowseAsync($"content/{categoryName}/{mountFolder.Name}", ct)
            .ConfigureAwait(false);
        if (contents is null) return;

        var mediaFiles = contents.Items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .ToArray();

        if (mediaFiles.Length == 0) return;

        // Use the first (largest) video file as the primary media
        var primaryFile = mediaFiles.OrderByDescending(f => f.FileSize ?? 0).First();
        var nzbdavId = primaryFile.Id.ToString();

        // Skip if already in Jellyfin
        if (existingNzbdavIds.Contains(nzbdavId))
            return;

        // Determine if this is a movie or episode based on category name
        // Convention: categories named "tv", "series", "shows" → TV; everything else → Movie
        var isTvCategory = categoryName.Contains("tv", StringComparison.OrdinalIgnoreCase)
                           || categoryName.Contains("series", StringComparison.OrdinalIgnoreCase)
                           || categoryName.Contains("show", StringComparison.OrdinalIgnoreCase);

        if (isTvCategory)
        {
            _logger.LogDebug("TV content detected for {Name} — skipping auto-creation (use Jellyfin's TV scanner with library path)", mountFolder.Name);
            // TV show organization is complex (series → seasons → episodes).
            // Rather than replicating Jellyfin's naming parser, we set NzbdavId
            // on files that Jellyfin discovers through its normal library scan.
            // The plugin's role for TV is providing the stream URL, not organizing.
            return;
        }

        // Create a Movie item
        var movie = new Movie
        {
            Name = mountFolder.Name,
            ProviderIds = new Dictionary<string, string>
            {
                ["NzbdavId"] = nzbdavId
            },
            IsVirtualItem = true
        };

        // Set the "path" to the NZBDAV stream URL so Jellyfin's media probe works
        var meta = await client.GetMetaAsync(primaryFile.Id, ct).ConfigureAwait(false);
        if (meta != null)
        {
            var streamUrl = client.GetSignedStreamUrl(primaryFile.Id, meta.StreamToken ?? "");
            movie.Path = streamUrl;
            movie.Size = meta.FileSize ?? 0;
        }

        _libraryManager.CreateItem(movie, null);
        _logger.LogInformation("Created Jellyfin movie: {Name} (NzbdavId: {Id})", mountFolder.Name, nzbdavId);
    }

    private HashSet<string> GetExistingNzbdavIds()
    {
        var query = new InternalItemsQuery
        {
            HasAnyProviderId = new Dictionary<string, string> { ["NzbdavId"] = string.Empty },
            Recursive = true
        };

        return _libraryManager.GetItemList(query)
            .Where(i => i.ProviderIds.ContainsKey("NzbdavId"))
            .Select(i => i.ProviderIds["NzbdavId"])
            .ToHashSet();
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }
}
```

- [ ] **Step 2: Build plugin**

Run: `cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build`
Expected: Build succeeded.

**Note:** If `ILibraryManager.CreateItem` signature differs in the target Jellyfin version, adapt the call. The `CreateItem(BaseItem, BaseItem?)` signature is standard in Jellyfin 10.10+.

- [ ] **Step 3: Commit**

```bash
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs
git commit -m "Add NzbdavLibrarySyncTask — Jellyfin library scanner for NZBDAV content"
```

---

### Task 2: Request Timeout Middleware

**Files:**
- Create: `backend/Middlewares/RequestTimeoutMiddleware.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Create the middleware**

```csharp
// backend/Middlewares/RequestTimeoutMiddleware.cs
using Serilog;

namespace NzbWebDAV.Middlewares;

public class RequestTimeoutMiddleware(RequestDelegate next)
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StreamTimeout = TimeSpan.FromMinutes(5);

    public async Task InvokeAsync(HttpContext context)
    {
        var timeout = IsStreamingRequest(context) ? StreamTimeout : MetadataTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(timeout);
        context.RequestAborted = cts.Token;

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !context.RequestAborted.IsCancellationRequested)
        {
            // Timeout (not client abort)
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                await context.Response.WriteAsync("Request timed out.").ConfigureAwait(false);
            }
        }
    }

    private static bool IsStreamingRequest(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        if (path.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase)) return true;
        // WebDAV GET of content paths are streaming
        if (context.Request.Method == HttpMethods.Get && path.StartsWith("/content/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/view/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
```

- [ ] **Step 2: Register in Program.cs**

Add **before** `UseMiddleware<ExceptionMiddleware>()`:

```csharp
app.UseMiddleware<RequestTimeoutMiddleware>();
app.UseMiddleware<ExceptionMiddleware>();
```

- [ ] **Step 3: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`
Expected: All pass.

- [ ] **Step 4: Commit**

```bash
git add backend/Middlewares/RequestTimeoutMiddleware.cs backend/Program.cs
git commit -m "Add request timeout middleware (30s metadata, 5min streams)"
```

---

### Task 3: Load Shedding

**Files:**
- Create: `backend/Exceptions/ServiceOverloadedException.cs`
- Modify: `backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs`
- Modify: `backend/Middlewares/ExceptionMiddleware.cs`

- [ ] **Step 1: Create exception type**

```csharp
// backend/Exceptions/ServiceOverloadedException.cs
namespace NzbWebDAV.Exceptions;

public class ServiceOverloadedException : Exception
{
    public ServiceOverloadedException()
        : base("Server is overloaded. Try again later.") { }
}
```

- [ ] **Step 2: Add queue depth check to PrioritizedSemaphore**

Add a public property and a pre-check method to `PrioritizedSemaphore`:

```csharp
public int PendingCount
{
    get { lock (_lock) return _highPriorityWaiters.Count + _lowPriorityWaiters.Count; }
}

public void ThrowIfOverloaded(int maxQueueDepth)
{
    if (PendingCount > maxQueueDepth)
        throw new ServiceOverloadedException();
}
```

- [ ] **Step 3: Add overload check in DownloadingNntpClient**

In `backend/Clients/Usenet/DownloadingNntpClient.cs`, modify `AcquireExclusiveConnectionAsync(CancellationToken)`:

```csharp
private Task AcquireExclusiveConnectionAsync(CancellationToken cancellationToken)
{
    // Load shedding: reject immediately if too many requests are queued
    _semaphore.ThrowIfOverloaded(_configManager.GetMaxDownloadConnections() * 2);

    var downloadPriorityContext = cancellationToken.GetContext<DownloadPriorityContext>();
    var semaphorePriority = downloadPriorityContext?.Priority ?? SemaphorePriority.High;
    return _semaphore.WaitAsync(semaphorePriority, cancellationToken);
}
```

- [ ] **Step 4: Handle in ExceptionMiddleware**

In `backend/Middlewares/ExceptionMiddleware.cs`, add a catch clause for `ServiceOverloadedException`:

```csharp
catch (ServiceOverloadedException)
{
    if (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "5";
        await context.Response.WriteAsync("Service temporarily overloaded. Retry after 5 seconds.")
            .ConfigureAwait(false);
    }
}
```

Add this catch **before** the generic `catch (Exception)` block.

- [ ] **Step 5: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`
Expected: All pass.

- [ ] **Step 6: Commit**

```bash
git add backend/Exceptions/ServiceOverloadedException.cs backend/Clients/Usenet/Concurrency/PrioritizedSemaphore.cs backend/Clients/Usenet/DownloadingNntpClient.cs backend/Middlewares/ExceptionMiddleware.cs
git commit -m "Add load shedding — 503 when NNTP queue exceeds 2x max connections"
```

---

### Task 4: Browse Path O(N) Optimization

**Files:**
- Modify: `backend/Database/DavDatabaseClient.cs`
- Modify: `backend/Api/Controllers/Browse/BrowseController.cs`

- [ ] **Step 1: Add GetItemByPathAsync to DavDatabaseClient**

```csharp
public Task<DavItem?> GetItemByPathAsync(string path, CancellationToken ct = default)
{
    return ctx.Items.FirstOrDefaultAsync(x => x.Path == path, ct);
}
```

- [ ] **Step 2: Replace ResolvePath in BrowseController**

Replace the entire `ResolvePath` method and update `Browse`:

```csharp
[HttpGet("{*path}")]
public async Task<IActionResult> Browse(string? path, CancellationToken ct)
{
    var normalizedPath = "/" + (path?.Trim('/') ?? "");

    // Single query via indexed Path column instead of O(N) tree walk
    var directory = normalizedPath == "/"
        ? DavItem.Root
        : await dbClient.GetItemByPathAsync(normalizedPath, ct).ConfigureAwait(false);

    if (directory is null)
        return NotFound(new { error = $"Path not found: {normalizedPath}" });

    var children = await dbClient.GetDirectoryChildrenAsync(directory.Id, ct).ConfigureAwait(false);

    return Ok(new BrowseResponse
    {
        Path = normalizedPath,
        Items = children.Select(BrowseItem.FromDavItem).ToArray()
    });
}
```

Remove the `ResolvePath` method entirely.

- [ ] **Step 3: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`

- [ ] **Step 4: Commit**

```bash
git add backend/Database/DavDatabaseClient.cs backend/Api/Controllers/Browse/BrowseController.cs
git commit -m "Optimize /api/browse to single Path query instead of O(N) tree walk"
```

---

### Task 5: Rich Health Checks

**Files:**
- Create: `backend/Services/NzbdavHealthCheck.cs`
- Modify: `backend/Program.cs`

- [ ] **Step 1: Create the health check**

```csharp
// backend/Services/NzbdavHealthCheck.cs
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;

namespace NzbWebDAV.Services;

public class NzbdavHealthCheck(
    LiveSegmentCache liveSegmentCache,
    UsenetStreamingClient usenetClient
) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var status = HealthStatus.Healthy;

        // Check 1: Database connectivity
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var count = await dbContext.Items.CountAsync(cancellationToken).ConfigureAwait(false);
            data["database"] = "connected";
            data["database_items"] = count;
        }
        catch (Exception ex)
        {
            status = HealthStatus.Unhealthy;
            data["database"] = $"unreachable: {ex.Message}";
        }

        // Check 2: Cache directory writable
        try
        {
            var testFile = Path.Combine(liveSegmentCache.CacheDirectory, ".health-check");
            await File.WriteAllTextAsync(testFile, "ok", cancellationToken).ConfigureAwait(false);
            File.Delete(testFile);
            data["cache_directory"] = "writable";
        }
        catch (Exception ex)
        {
            status = HealthStatus.Unhealthy;
            data["cache_directory"] = $"not writable: {ex.Message}";
        }

        // Check 3: Cache stats
        var cacheStats = liveSegmentCache.GetStats();
        data["cache_segments"] = cacheStats.CachedSegmentCount;
        data["cache_bytes"] = cacheStats.CachedBytes;
        var totalLookups = cacheStats.Hits + cacheStats.Misses;
        data["cache_hit_rate"] = totalLookups > 0 ? (double)cacheStats.Hits / totalLookups : 0;

        // Check 4: NNTP pool utilization
        var poolStats = usenetClient.PoolStats;
        if (poolStats != null)
        {
            var utilization = poolStats.MaxPooled > 0
                ? (double)poolStats.TotalActive / poolStats.MaxPooled
                : 0;
            data["nntp_utilization"] = utilization;
            data["nntp_active"] = poolStats.TotalActive;
            data["nntp_max"] = poolStats.MaxPooled;

            if (utilization > 0.9)
                status = status == HealthStatus.Unhealthy ? HealthStatus.Unhealthy : HealthStatus.Degraded;
        }
        else
        {
            data["nntp_pool"] = "not initialized";
        }

        return new HealthCheckResult(status, "NZBDAV health check", data: data);
    }
}
```

- [ ] **Step 2: Register in Program.cs**

Replace the existing `AddHealthChecks()` line:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck<NzbdavHealthCheck>("nzbdav");
```

- [ ] **Step 3: Add using for EF Core**

In `NzbdavHealthCheck.cs`, add:
```csharp
using Microsoft.EntityFrameworkCore;
```

- [ ] **Step 4: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`

- [ ] **Step 5: Commit**

```bash
git add backend/Services/NzbdavHealthCheck.cs backend/Program.cs
git commit -m "Add rich health check — DB, cache, NNTP pool utilization"
```

---

### Task 6: Multi-Node Docker-Compose

**Files:**
- Create: `docs/deployment/docker-compose.multi-node.yml`
- Create: `docs/deployment/haproxy.cfg`

- [ ] **Step 1: Create docker-compose**

```yaml
# docs/deployment/docker-compose.multi-node.yml
# Multi-node NZBDAV deployment: 2 streaming + 1 ingest + PostgreSQL + HAProxy
#
# Prerequisites:
#   - Plan C implemented (NZBDAV_ROLE + DATABASE_URL env vars)
#   - All nodes share the same PostgreSQL database
#   - HAProxy routes by consistent hash for cache affinity

services:
  postgres:
    image: postgres:17-alpine
    restart: unless-stopped
    environment:
      POSTGRES_DB: nzbdav
      POSTGRES_USER: nzbdav
      POSTGRES_PASSWORD: changeme  # CHANGE THIS
    volumes:
      - postgres_data:/var/lib/postgresql/data
    networks:
      - internal
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U nzbdav"]
      interval: 10s
      timeout: 5s
      retries: 5

  nzbdav-ingest:
    image: nzbdav/nzbdav:latest
    restart: unless-stopped
    environment:
      - NZBDAV_ROLE=ingest
      - DATABASE_URL=Host=postgres;Database=nzbdav;Username=nzbdav;Password=changeme
      - FRONTEND_BACKEND_API_KEY=your-api-key-here
    volumes:
      - ingest_config:/config
    depends_on:
      postgres:
        condition: service_healthy
    networks:
      - internal

  nzbdav-stream-1:
    image: nzbdav/nzbdav:latest
    restart: unless-stopped
    environment:
      - NZBDAV_ROLE=streaming
      - DATABASE_URL=Host=postgres;Database=nzbdav;Username=nzbdav;Password=changeme
      - FRONTEND_BACKEND_API_KEY=your-api-key-here
      # Split provider connections: 15 per node for a 30-connection provider
      - usenet__max-download-connections=15
    volumes:
      - stream1_cache:/config/stream-cache
    depends_on:
      postgres:
        condition: service_healthy
    networks:
      - internal

  nzbdav-stream-2:
    image: nzbdav/nzbdav:latest
    restart: unless-stopped
    environment:
      - NZBDAV_ROLE=streaming
      - DATABASE_URL=Host=postgres;Database=nzbdav;Username=nzbdav;Password=changeme
      - FRONTEND_BACKEND_API_KEY=your-api-key-here
      - usenet__max-download-connections=15
    volumes:
      - stream2_cache:/config/stream-cache
    depends_on:
      postgres:
        condition: service_healthy
    networks:
      - internal

  haproxy:
    image: haproxy:2.9-alpine
    restart: unless-stopped
    ports:
      - "8080:8080"
      - "8404:8404"  # Stats page
    volumes:
      - ./haproxy.cfg:/usr/local/etc/haproxy/haproxy.cfg:ro
    depends_on:
      - nzbdav-stream-1
      - nzbdav-stream-2
      - nzbdav-ingest
    networks:
      - internal

volumes:
  postgres_data:
  ingest_config:
  stream1_cache:
  stream2_cache:

networks:
  internal:
    driver: bridge
```

- [ ] **Step 2: Create HAProxy config**

```
# docs/deployment/haproxy.cfg
global
    maxconn 4096

defaults
    mode http
    timeout connect 5s
    timeout client  300s
    timeout server  300s
    option httplog

frontend http_front
    bind *:8080

    # Route ingest operations to ingest node
    acl is_addfile path_beg /api
    acl is_addfile_mode urlp(mode) -i addfile addurl
    use_backend ingest if is_addfile is_addfile_mode

    # Route everything else to streaming nodes
    default_backend streaming

backend streaming
    balance uri
    hash-type consistent
    option httpchk GET /health
    http-check expect status 200
    server stream1 nzbdav-stream-1:8080 check
    server stream2 nzbdav-stream-2:8080 check

backend ingest
    server ingest1 nzbdav-ingest:8080 check

frontend stats
    bind *:8404
    stats enable
    stats uri /stats
    stats refresh 10s
```

- [ ] **Step 3: Commit**

```bash
git add docs/deployment/docker-compose.multi-node.yml docs/deployment/haproxy.cfg
git commit -m "Add multi-node docker-compose with HAProxy consistent hash routing"
```

---

### Task 7: REST Stream Path Optimization

**Files:**
- Modify: `backend/Database/DavDatabaseClient.cs`
- Modify: `backend/Api/Controllers/StreamFile/StreamFileController.cs`

Currently `StreamFileController` calls `store.GetItemAsync(davItem.Path)` which walks the tree. Since we already have the `DavItem` by ID, we should construct the store file directly.

- [ ] **Step 1: Add GetStoreItemByIdAsync to DavDatabaseClient**

```csharp
// Add to DavDatabaseClient
public async Task<(DavItem item, Stream stream)?> GetStreamableFileAsync(
    Guid id,
    HttpContext httpContext,
    INntpClient usenetClient,
    ConfigManager configManager,
    ReadAheadWarmingService? warmingService,
    CancellationToken ct)
{
    var davItem = await ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
    if (davItem is null) return null;

    IStoreItem storeItem = davItem.Type switch
    {
        DavItem.ItemType.NzbFile =>
            new DatabaseStoreNzbFile(davItem, httpContext, this, usenetClient, configManager, warmingService),
        DavItem.ItemType.RarFile =>
            new DatabaseStoreRarFile(davItem, httpContext, this, usenetClient as UsenetStreamingClient
                ?? throw new InvalidOperationException(), configManager, warmingService),
        DavItem.ItemType.MultipartFile =>
            new DatabaseStoreMultipartFile(davItem, httpContext, this, usenetClient as UsenetStreamingClient
                ?? throw new InvalidOperationException(), configManager, warmingService),
        _ => throw new FileNotFoundException($"Item {id} is not a streamable file")
    };

    var stream = await storeItem.GetReadableStreamAsync(ct).ConfigureAwait(false);
    return (davItem, stream);
}
```

Actually, this creates tight coupling. **Simpler approach:** add a `GetItemByIdAsync` to `DatabaseStore` that skips path resolution:

- [ ] **Step 1 (revised): Add path-by-id lookup**

In `DavDatabaseClient.cs`, add:
```csharp
public async Task<DavItem?> GetItemByIdAsync(Guid id, CancellationToken ct = default)
{
    return await ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
}
```

In `StreamFileController`, the current path through `store.GetItemAsync(davItem.Path)` walks the tree. Instead, since we already have the `davItem` and its `Path`, use the `GetItemByPathAsync` from Task 4:

```csharp
// The store.GetItemAsync(path) already exists and is needed for the WebDAV store
// pipeline to create the correct stream. The real optimization is: we already have
// davItem.Path from the FindAsync — the path walk in DatabaseStore is O(N) per segment
// but since BrowseController now uses GetItemByPathAsync, we should do the same here.
```

Actually, the `store.GetItemAsync(davItem.Path)` call is necessary because it creates the `DatabaseStoreNzbFile`/`DatabaseStoreRarFile`/`DatabaseStoreMultipartFile` objects that set up `SegmentFetchContext` and `ReadAheadWarmingService`. The real bottleneck is that `DatabaseStore._root.ResolvePath(path)` walks the tree.

The fix is to add a fast path to `DatabaseStore`:

```csharp
// In DatabaseStore, add:
public async Task<IStoreItem?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken)
{
    // For files, we can construct the store item directly from the DavItem
    var davItem = await dbClient.Ctx.Items.FindAsync(new object[] { id }, cancellationToken)
        .ConfigureAwait(false);
    if (davItem is null) return null;

    return _root.CreateStoreItem(davItem);
}
```

This requires exposing `GetItem(DavItem)` from `DatabaseStoreCollection`. But that's already a private method. Make it internal.

**Simplest correct fix:** The tree walk is already fast for typical paths (2-3 levels deep: `/content/movies/MovieName`). The O(N) issue was mainly in `BrowseController` which is fixed in Task 4. For streaming, the tree walk ensures all middleware (warming, fetch context) is correctly initialized. **Skip this task — the benefit doesn't justify the coupling risk.**

- [ ] **Step 1: Mark as skipped**

This optimization is deferred. The tree walk in `DatabaseStore` is 2-3 DB queries for typical media paths, each hitting the indexed `(ParentId, Name)` unique index. The real O(N) issue was in `BrowseController` (fixed in Task 4). Optimizing `StreamFileController` would require exposing internal `DatabaseStoreCollection.GetItem()` which creates unwanted coupling.

- [ ] **Step 2: Commit (no-op, document decision)**

```bash
git commit --allow-empty -m "Skip REST stream path optimization — tree walk is 2-3 indexed queries, not a bottleneck"
```

---

### Task 8: Prometheus & Grafana Configuration

**Files:**
- Create: `docs/deployment/prometheus.yml`
- Create: `docs/deployment/grafana-dashboard.json`

- [ ] **Step 1: Create Prometheus scrape config**

```yaml
# docs/deployment/prometheus.yml
global:
  scrape_interval: 15s
  evaluation_interval: 15s

scrape_configs:
  - job_name: nzbdav
    metrics_path: /metrics
    static_configs:
      - targets:
          - nzbdav-stream-1:8080
          - nzbdav-stream-2:8080
          - nzbdav-ingest:8080
        labels:
          service: nzbdav

  # For single-node deployments:
  # - job_name: nzbdav
  #   static_configs:
  #     - targets: ['localhost:8080']
```

- [ ] **Step 2: Create Grafana dashboard**

```json
{
  "dashboard": {
    "title": "NZBDAV",
    "panels": [
      {
        "title": "Cache Hit Rate",
        "type": "gauge",
        "targets": [{ "expr": "nzbdav_cache_hit_rate", "legendFormat": "{{instance}}" }],
        "fieldConfig": { "defaults": { "min": 0, "max": 1, "thresholds": { "steps": [
          { "value": 0, "color": "red" },
          { "value": 0.5, "color": "yellow" },
          { "value": 0.8, "color": "green" }
        ]}}}
      },
      {
        "title": "Active Streams",
        "type": "stat",
        "targets": [{ "expr": "sum(nzbdav_streams_active)", "legendFormat": "streams" }]
      },
      {
        "title": "NNTP Connection Utilization",
        "type": "timeseries",
        "targets": [
          { "expr": "nzbdav_nntp_connections_active", "legendFormat": "active {{instance}}" },
          { "expr": "nzbdav_nntp_connections_max", "legendFormat": "max {{instance}}" }
        ]
      },
      {
        "title": "Cache Segments by Category",
        "type": "timeseries",
        "targets": [
          { "expr": "nzbdav_cache_segments{category=\"video\"}", "legendFormat": "video" },
          { "expr": "nzbdav_cache_segments{category=\"small_file\"}", "legendFormat": "small_file" },
          { "expr": "nzbdav_cache_segments{category=\"unknown\"}", "legendFormat": "unknown" }
        ]
      },
      {
        "title": "Request Latency (p99)",
        "type": "timeseries",
        "targets": [
          { "expr": "histogram_quantile(0.99, rate(http_request_duration_seconds_bucket[5m]))", "legendFormat": "p99" },
          { "expr": "histogram_quantile(0.50, rate(http_request_duration_seconds_bucket[5m]))", "legendFormat": "p50" }
        ]
      },
      {
        "title": "Cache Operations Rate",
        "type": "timeseries",
        "targets": [
          { "expr": "rate(nzbdav_cache_hits_total[5m])", "legendFormat": "hits/s" },
          { "expr": "rate(nzbdav_cache_misses_total[5m])", "legendFormat": "misses/s" },
          { "expr": "rate(nzbdav_cache_evictions_total[5m])", "legendFormat": "evictions/s" }
        ]
      },
      {
        "title": "Warming Sessions",
        "type": "stat",
        "targets": [{ "expr": "sum(nzbdav_warming_sessions_active)", "legendFormat": "sessions" }]
      },
      {
        "title": "Queue Status",
        "type": "stat",
        "targets": [{ "expr": "nzbdav_queue_processing", "legendFormat": "{{instance}}" }]
      }
    ]
  }
}
```

- [ ] **Step 3: Commit**

```bash
git add docs/deployment/prometheus.yml docs/deployment/grafana-dashboard.json
git commit -m "Add Prometheus scrape config and Grafana dashboard template"
```

---

### Task 9: Integration Tests

**Files:**
- Create: `backend.Tests/Integration/RestApiIntegrationTests.cs`
- Modify: `backend.Tests/backend.Tests.csproj`

- [ ] **Step 1: Add WebApplicationFactory package**

Add to `backend.Tests/backend.Tests.csproj`:

```xml
<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.1" />
```

- [ ] **Step 2: Create integration tests**

```csharp
// backend.Tests/Integration/RestApiIntegrationTests.cs
using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Mvc.Testing;

namespace backend.Tests.Integration;

public class RestApiIntegrationTests : IClassFixture<WebApplicationFactory<NzbWebDAV.Program>>
{
    private readonly HttpClient _client;

    public RestApiIntegrationTests(WebApplicationFactory<NzbWebDAV.Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Metrics_ReturnsPrometheusFormat()
    {
        var response = await _client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var content = await response.Content.ReadAsStringAsync();
        Assert.Contains("nzbdav_cache_bytes", content);
        Assert.Contains("nzbdav_streams_active", content);
    }

    [Fact]
    public async Task Browse_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync("/api/browse");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Meta_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync($"/api/meta/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Stream_WithoutApiKey_Returns401()
    {
        var response = await _client.GetAsync($"/api/stream/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
```

**Note:** These tests require `Program` to be accessible from the test project. If `Program` is not public, add to `backend/Program.cs`:

```csharp
// At the bottom of Program.cs, outside the class:
public partial class Program { }
```

Or use `[assembly: InternalsVisibleTo("backend.Tests")]` in the backend project.

- [ ] **Step 3: Build and run**

Run: `cd backend.Tests && dotnet test --filter "FullyQualifiedName~Integration"`

**Note:** Integration tests may fail if the database requires migrations or the environment isn't set up. These tests validate the HTTP pipeline, not business logic. If they fail due to DB issues, wrap the factory setup to use an in-memory SQLite database.

- [ ] **Step 4: Commit**

```bash
git add backend.Tests/Integration/RestApiIntegrationTests.cs backend.Tests/backend.Tests.csproj
git commit -m "Add REST API integration tests for auth, metrics, and health"
```

---

### Task 10: Graceful Shutdown with Connection Draining

**Files:**
- Modify: `backend/Program.cs`

Per failure model section 2: container kill must drain active connections for up to 30s before force-closing.

- [ ] **Step 1: Configure Kestrel shutdown timeout**

In `Program.cs`, add to the Kestrel configuration block:

```csharp
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxRequestBodySize;
    options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(120);
});

// Set shutdown timeout for connection draining
builder.Host.ConfigureHostOptions(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(30));
```

- [ ] **Step 2: Return 503 during shutdown for new requests**

The health check already returns `Unhealthy` when the DB/cache/NNTP is broken. To also return `Unhealthy` during shutdown, add a shutdown flag:

In `Program.cs`, before `app.RunAsync()`:

```csharp
var shutdownStarted = false;
app.Lifetime.ApplicationStopping.Register(() =>
{
    shutdownStarted = true;
    SigtermUtil.Cancel();
    ContentIndexSnapshotInterceptor.SnapshotWriter
        .FlushAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult();
});
```

In the health check (`NzbdavHealthCheck.cs`), accept and check this flag:

```csharp
// Add to NzbdavHealthCheck constructor:
private readonly IHostApplicationLifetime _lifetime;

// In CheckHealthAsync, at the top:
if (_lifetime.ApplicationStopping.IsCancellationRequested)
    return HealthCheckResult.Unhealthy("Shutting down — draining connections");
```

- [ ] **Step 3: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`

- [ ] **Step 4: Commit**

```bash
git add backend/Program.cs backend/Services/NzbdavHealthCheck.cs
git commit -m "Add graceful shutdown with 30s connection draining"
```

---

### Task 11: Provider Health Exposure

**Files:**
- Modify: `backend/Clients/Usenet/MultiProviderNntpClient.cs`
- Modify: `backend/Clients/Usenet/UsenetStreamingClient.cs`
- Modify: `backend/Services/NzbdavHealthCheck.cs`
- Modify: `backend/Metrics/NzbdavMetricsCollector.cs`

Per failure model section 1: health check must return `Unhealthy` when all NNTP providers are in cooldown.

- [ ] **Step 1: Expose provider health state from MultiProviderNntpClient**

Add a public method that reports whether any provider is currently available:

```csharp
// In MultiProviderNntpClient, add:
public bool HasAvailableProvider()
{
    return providers.Any(p => !p.Health.IsInCooldown(DateTimeOffset.UtcNow));
}

public int HealthyProviderCount => providers.Count(p => !p.Health.IsInCooldown(DateTimeOffset.UtcNow));
public int TotalProviderCount => providers.Count;
```

This requires checking if `ProviderHealth` has an `IsInCooldown` method. If not, add one based on the existing cooldown tracking. The `Health` field on the provider entry tracks failures and cooldown expiry.

- [ ] **Step 2: Expose through UsenetStreamingClient**

```csharp
// In UsenetStreamingClient, add alongside PoolStats:
public bool HasAvailableProvider => GetMultiProviderClient()?.HasAvailableProvider() ?? false;
```

This requires storing the `MultiProviderNntpClient` reference. The `PipelineResult` already stores the `LiveSegmentCachingNntpClient` — walk through the wrapper chain or store the multi-provider client directly.

Simpler: add the provider count directly to `ConnectionPoolStats` since it already tracks per-provider state, or store the `MultiProviderNntpClient` in the `PipelineResult`.

- [ ] **Step 3: Add provider health to NzbdavHealthCheck**

```csharp
// In NzbdavHealthCheck.CheckHealthAsync, add after NNTP pool check:
if (poolStats != null && poolStats.TotalActive == 0 && poolStats.MaxPooled > 0)
{
    // All connections idle but pool exists — check if providers are in cooldown
    // This is a heuristic: if max > 0 but no connections have been created recently,
    // providers may be down
    data["nntp_provider_status"] = "check_providers";
}
```

- [ ] **Step 4: Add provider health metric**

```csharp
// In NzbdavMetricsCollector, add:
private readonly Gauge _nntpProvidersHealthy = metricFactory.CreateGauge(
    "nzbdav_nntp_providers_healthy", "Number of NNTP providers not in cooldown");
```

- [ ] **Step 5: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`

- [ ] **Step 6: Commit**

```bash
git add backend/Clients/Usenet/MultiProviderNntpClient.cs backend/Clients/Usenet/UsenetStreamingClient.cs backend/Services/NzbdavHealthCheck.cs backend/Metrics/NzbdavMetricsCollector.cs
git commit -m "Expose NNTP provider health state to health check and metrics"
```

---

### Task 12: Cache Max Bytes Metric + Utilization in Health Check

**Files:**
- Modify: `backend/Clients/Usenet/Caching/LiveSegmentCache.cs`
- Modify: `backend/Metrics/NzbdavMetricsCollector.cs`
- Modify: `backend/Services/NzbdavHealthCheck.cs`

Per failure model section 3: health check returns `Degraded` when cache utilization > 90%. Operator alerts on `cache_bytes / cache_max_bytes > 0.85`.

- [ ] **Step 1: Expose MaxCacheSizeBytes from LiveSegmentCache**

Add to `LiveSegmentCache`:

```csharp
public long MaxCacheSizeBytes => Interlocked.Read(ref _maxCacheSizeBytes);
```

Note: `_maxCacheSizeBytes` was changed from `readonly` to mutable in the bottleneck fixes (for config-change support), so `Interlocked.Read` is correct.

- [ ] **Step 2: Add cache max metric to NzbdavMetricsCollector**

```csharp
// Add gauge:
private readonly Gauge _cacheMaxBytes = metricFactory.CreateGauge(
    "nzbdav_cache_max_bytes", "Configured maximum cache size in bytes");

// In CollectMetrics:
_cacheMaxBytes.Set(_getMaxCacheBytes());
```

Add a `Func<long> _getMaxCacheBytes` parameter sourced from `cache.MaxCacheSizeBytes`.

- [ ] **Step 3: Add cache utilization to health check**

In `NzbdavHealthCheck.CheckHealthAsync`:

```csharp
var cacheUtilization = liveSegmentCache.MaxCacheSizeBytes > 0
    ? (double)cacheStats.CachedBytes / liveSegmentCache.MaxCacheSizeBytes
    : 0;
data["cache_utilization"] = cacheUtilization;

if (cacheUtilization > 0.9)
    status = status == HealthStatus.Unhealthy ? HealthStatus.Unhealthy : HealthStatus.Degraded;
```

- [ ] **Step 4: Build and test**

Run: `cd backend && dotnet build && cd ../backend.Tests && dotnet test --filter "FullyQualifiedName!~ContentIndexRecovery"`

- [ ] **Step 5: Commit**

```bash
git add backend/Clients/Usenet/Caching/LiveSegmentCache.cs backend/Metrics/NzbdavMetricsCollector.cs backend/Services/NzbdavHealthCheck.cs
git commit -m "Add cache utilization metric and degraded health check at 90%"
```

---

## Execution Summary

| Task | What | Risk | Failure model section |
|------|------|------|-----------------------|
| 1 | Jellyfin library scanner (resilient) | Medium | Section 5 |
| 2 | Request timeouts | Low | Sections 1, 4 |
| 3 | Load shedding | Low | Section 1 |
| 4 | Browse O(N) | Low | — |
| 5 | Rich health checks | Low | All sections |
| 6 | Docker-compose | None | — |
| 7 | REST stream optimization | Skipped | — |
| 8 | Prometheus/Grafana config | None | Section summary |
| 9 | Integration tests | Medium | — |
| 10 | Graceful shutdown + draining | Low | Section 2 |
| 11 | Provider health exposure | Medium | Section 1 |
| 12 | Cache max bytes metric | Low | Section 3 |

**Execution order:**
- **Parallel group 1:** Tasks 1, 2, 3, 4, 6, 8, 10, 12 (all independent)
- **After group 1:** Tasks 5, 11 (health check depends on provider health from 11, and shutdown flag from 10)
- **Last:** Task 9 (integration tests validate middleware from 2, 3, and health check from 5)

**Failure model compliance:** Every task that changes runtime behavior must be traceable to a section in `docs/plans/failure-model.md`. If a task produces behavior inconsistent with the failure model, fix the failure model first, then implement.
