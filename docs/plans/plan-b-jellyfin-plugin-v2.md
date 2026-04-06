# Plan B v2: Jellyfin Plugin + NZBDAV REST API (Unified Streaming)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate rclone FUSE by adding a REST API to NZBDAV and building a Jellyfin plugin that streams directly over HTTP. One streaming path, one auth path, one metrics path.

**Architecture:** A shared `StreamExecutionService` handles all file streaming — Range parsing, content headers, seek, stream cleanup, and active-stream metrics. Both the existing WebDAV GET handler and the new REST `/api/stream/{id}` endpoint call this same service. Auth is centralized in a single `ApiKeyAuthFilter`. The Jellyfin plugin consumes the REST API contract.

**Tech Stack:** .NET 10 (NZBDAV), Jellyfin Plugin SDK 10.x, HttpClient

**Key design decisions from architect review:**
- **One streaming path:** `StreamExecutionService` is the single place that opens a file stream, handles Range headers, sets Content-Length/Content-Range/Accept-Ranges, copies bytes, and tracks active streams. WebDAV GET and REST `/api/stream` both call it.
- **One auth path:** `ApiKeyAuthFilter` with `CryptographicOperations.FixedTimeEquals`. No inline auth in controllers. Applied via `[ServiceFilter]`.
- **Signed stream URLs:** For Jellyfin (which can't send custom headers on media source HTTP requests), generate time-limited HMAC-signed URLs instead of raw API keys in query strings. Raw `?apikey=` kept as fallback for SABnzbd compatibility.
- **No duplicated logic:** Browse and Meta controllers are thin — they query the DB and return JSON. Stream controller delegates entirely to `StreamExecutionService`.

---

## Part 1: Shared Infrastructure (NZBDAV Backend)

---

### Task 1: Create StreamExecutionService

**Files:**
- Create: `backend/Services/StreamExecutionService.cs`

This is the single place that serves file content. Both `GetAndHeadHandlerPatch` (WebDAV) and the new `StreamFileController` (REST) will call it.

- [ ] **Step 1: Create the service**

```csharp
// backend/Services/StreamExecutionService.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using NzbWebDAV.Extensions;
using NzbWebDAV.Metrics;

namespace NzbWebDAV.Services;

public class StreamExecutionService
{
    private static readonly FileExtensionContentTypeProvider MimeTypeProvider = new();

    /// <summary>
    /// Serves a stream to the HTTP response with full Range support, content headers,
    /// and active-stream metrics tracking. This is the ONE place streaming happens.
    /// </summary>
    public async Task ServeStreamAsync(
        Stream stream,
        string fileName,
        HttpResponse response,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        response.ContentType = GetContentType(fileName);
        response.Headers.AcceptRanges = "bytes";

        if (!stream.CanSeek)
        {
            NzbdavMetricsCollector.IncrementActiveStreams();
            try
            {
                await stream.CopyToPooledAsync(response.Body, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                NzbdavMetricsCollector.DecrementActiveStreams();
            }
            return;
        }

        var totalLength = stream.Length;
        var rangeHeader = request.Headers.Range.FirstOrDefault();
        long start = 0;
        long? end = null;

        if (!string.IsNullOrEmpty(rangeHeader) && TryParseRange(rangeHeader, totalLength, out var parsedStart, out var parsedEnd))
        {
            start = parsedStart;
            end = parsedEnd;
            var length = end.Value - start + 1;

            response.StatusCode = 206;
            response.ContentLength = length;
            response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";
        }
        else
        {
            response.StatusCode = 200;
            response.ContentLength = totalLength;
        }

        // HEAD requests: headers are set, no body
        if (HttpMethods.IsHead(request.Method))
            return;

        NzbdavMetricsCollector.IncrementActiveStreams();
        try
        {
            await stream.CopyRangeToPooledAsync(
                response.Body, start, end,
                cancellationToken: cancellationToken
            ).ConfigureAwait(false);
        }
        finally
        {
            NzbdavMetricsCollector.DecrementActiveStreams();
        }
    }

    /// <summary>
    /// Serves just the headers (for HEAD requests or metadata probes).
    /// </summary>
    public void SetFileHeaders(
        string fileName,
        long? fileSize,
        HttpResponse response)
    {
        response.ContentType = GetContentType(fileName);
        response.Headers.AcceptRanges = "bytes";
        if (fileSize.HasValue)
            response.ContentLength = fileSize.Value;
    }

    private static bool TryParseRange(string rangeHeader, long totalLength, out long start, out long end)
    {
        start = 0;
        end = totalLength - 1;

        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
            return false;

        var rangeSpec = rangeHeader["bytes=".Length..];
        var parts = rangeSpec.Split('-', 2);

        if (parts.Length != 2) return false;

        if (!string.IsNullOrEmpty(parts[0]))
            start = long.TryParse(parts[0], out var s) ? s : 0;
        else
        {
            // Suffix range: bytes=-500 means last 500 bytes
            if (long.TryParse(parts[1], out var suffix))
            {
                start = Math.Max(0, totalLength - suffix);
                end = totalLength - 1;
                return true;
            }
            return false;
        }

        if (!string.IsNullOrEmpty(parts[1]))
            end = long.TryParse(parts[1], out var e) ? Math.Min(e, totalLength - 1) : totalLength - 1;

        return start <= end && start < totalLength;
    }

    public static string GetContentType(string fileName)
    {
        if (MimeTypeProvider.TryGetContentType(fileName, out var contentType))
            return contentType;
        var ext = Path.GetExtension(fileName)?.ToLower();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".nfo" => "text/plain",
            ".rclonelink" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
```

- [ ] **Step 2: Register in DI**

In `Program.cs`, add:

```csharp
.AddSingleton<StreamExecutionService>()
```

- [ ] **Step 3: Verify build and commit**

```bash
cd backend && dotnet build
git add backend/Services/StreamExecutionService.cs backend/Program.cs
git commit -m "Add StreamExecutionService — single streaming path for WebDAV and REST"
```

---

### Task 2: Create ApiKeyAuthFilter — centralized auth

**Files:**
- Create: `backend/Api/Filters/ApiKeyAuthFilter.cs`

- [ ] **Step 1: Create the filter**

```csharp
// backend/Api/Filters/ApiKeyAuthFilter.cs
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Filters;

public class ApiKeyAuthFilter(ConfigManager configManager) : IAsyncActionFilter
{
    private byte[]? _cachedKeyBytes;
    private string? _cachedKeySource;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;

        // Accept key from header (preferred) or query string (Jellyfin/SABnzbd compat)
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault()
                          ?? request.Query["apikey"].FirstOrDefault();

        // Also accept signed stream tokens (Task 3)
        if (string.IsNullOrEmpty(providedKey))
        {
            var token = request.Query["token"].FirstOrDefault();
            if (!string.IsNullOrEmpty(token) && StreamTokenService.ValidateToken(token, request.Path, configManager))
            {
                await next().ConfigureAwait(false);
                return;
            }
        }

        if (string.IsNullOrEmpty(providedKey) || !ValidateApiKey(providedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
            return;
        }

        await next().ConfigureAwait(false);
    }

    private bool ValidateApiKey(string providedKey)
    {
        try
        {
            var expectedKey = configManager.GetApiKey();
            if (_cachedKeySource != expectedKey)
            {
                _cachedKeySource = expectedKey;
                _cachedKeyBytes = Encoding.UTF8.GetBytes(expectedKey);
            }

            var providedBytes = Encoding.UTF8.GetBytes(providedKey);

            // Constant-time comparison — keys must be same length for FixedTimeEquals
            if (providedBytes.Length != _cachedKeyBytes!.Length)
                return false;

            return CryptographicOperations.FixedTimeEquals(providedBytes, _cachedKeyBytes);
        }
        catch
        {
            return false;
        }
    }
}
```

- [ ] **Step 2: Register in DI**

```csharp
.AddScoped<ApiKeyAuthFilter>()
```

- [ ] **Step 3: Verify build**

Build will fail because `StreamTokenService` doesn't exist yet — that's Task 3.

- [ ] **Step 4: Commit**

```bash
git add backend/Api/Filters/ApiKeyAuthFilter.cs backend/Program.cs
git commit -m "Add ApiKeyAuthFilter with constant-time comparison"
```

---

### Task 3: Create StreamTokenService — signed stream URLs

**Files:**
- Create: `backend/Services/StreamTokenService.cs`

Jellyfin's media source HTTP client can't send custom headers. Instead of raw API keys in URLs (which get logged, cached, and show in browser history), generate short-lived HMAC-signed tokens.

- [ ] **Step 1: Create the service**

```csharp
// backend/Services/StreamTokenService.cs
using System.Security.Cryptography;
using System.Text;
using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

public static class StreamTokenService
{
    private const int DefaultExpiryMinutes = 60;

    /// <summary>
    /// Generate a signed token for a stream URL.
    /// Format: {expiryUnixSeconds}.{hmacHex}
    /// </summary>
    public static string GenerateToken(string path, ConfigManager configManager, int expiryMinutes = DefaultExpiryMinutes)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes).ToUnixTimeSeconds();
        var payload = $"{expiry}:{path}";
        var hmac = ComputeHmac(payload, configManager.GetApiKey());
        return $"{expiry}.{hmac}";
    }

    /// <summary>
    /// Validate a signed token against the request path.
    /// </summary>
    public static bool ValidateToken(string token, string path, ConfigManager configManager)
    {
        try
        {
            var parts = token.Split('.', 2);
            if (parts.Length != 2) return false;

            if (!long.TryParse(parts[0], out var expiry)) return false;
            if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expiry) return false;

            var payload = $"{expiry}:{path}";
            var expectedHmac = ComputeHmac(payload, configManager.GetApiKey());

            var expectedBytes = Encoding.UTF8.GetBytes(expectedHmac);
            var actualBytes = Encoding.UTF8.GetBytes(parts[1]);
            if (expectedBytes.Length != actualBytes.Length) return false;

            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        catch
        {
            return false;
        }
    }

    private static string ComputeHmac(string payload, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded (ApiKeyAuthFilter should now compile).

- [ ] **Step 3: Commit**

```bash
git add backend/Services/StreamTokenService.cs
git commit -m "Add StreamTokenService for HMAC-signed stream URLs"
```

---

### Task 4: Create BrowseController

**Files:**
- Create: `backend/Api/Controllers/Browse/BrowseController.cs`
- Create: `backend/Api/Controllers/Browse/BrowseResponse.cs`

- [ ] **Step 1: Create response DTO**

```csharp
// backend/Api/Controllers/Browse/BrowseResponse.cs
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Browse;

public class BrowseResponse
{
    public required string Path { get; init; }
    public required BrowseItem[] Items { get; init; }
}

public class BrowseItem
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public long? FileSize { get; init; }
    public required DateTime CreatedAt { get; init; }

    public static BrowseItem FromDavItem(DavItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Type = item.Type switch
        {
            DavItem.ItemType.Directory => "directory",
            DavItem.ItemType.NzbFile => "nzb_file",
            DavItem.ItemType.RarFile => "rar_file",
            DavItem.ItemType.MultipartFile => "multipart_file",
            _ => "directory"
        },
        FileSize = item.FileSize,
        CreatedAt = item.CreatedAt
    };
}
```

- [ ] **Step 2: Create controller**

```csharp
// backend/Api/Controllers/Browse/BrowseController.cs
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Browse;

[ApiController]
[Route("api/browse")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class BrowseController(DavDatabaseClient dbClient) : ControllerBase
{
    [HttpGet("{*path}")]
    public async Task<IActionResult> Browse(string? path, CancellationToken ct)
    {
        var normalizedPath = "/" + (path?.Trim('/') ?? "");
        var directory = await ResolvePath(normalizedPath, ct).ConfigureAwait(false);
        if (directory is null) return NotFound(new { error = $"Path not found: {normalizedPath}" });

        var children = await dbClient
            .GetDirectoryChildrenAsync(directory.Id, ct)
            .ConfigureAwait(false);

        return Ok(new BrowseResponse
        {
            Path = normalizedPath,
            Items = children.Select(BrowseItem.FromDavItem).ToArray()
        });
    }

    [HttpGet]
    public Task<IActionResult> BrowseRoot(CancellationToken ct) => Browse(null, ct);

    private async Task<DavItem?> ResolvePath(string path, CancellationToken ct)
    {
        if (path == "/") return DavItem.Root;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = DavItem.Root;
        foreach (var part in parts)
        {
            var child = await dbClient.GetDirectoryChildAsync(current.Id, part, ct).ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }
        return current;
    }
}
```

- [ ] **Step 3: Build and commit**

```bash
cd backend && dotnet build
git add backend/Api/Controllers/Browse/
git commit -m "Add /api/browse endpoint for JSON directory listing"
```

---

### Task 5: Create MetaController

**Files:**
- Create: `backend/Api/Controllers/Meta/MetaController.cs`
- Create: `backend/Api/Controllers/Meta/MetaResponse.cs`

- [ ] **Step 1: Create DTO and controller**

```csharp
// backend/Api/Controllers/Meta/MetaResponse.cs
namespace NzbWebDAV.Api.Controllers.Meta;

public class MetaResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public long? FileSize { get; init; }
    public required DateTime CreatedAt { get; init; }
    public Guid? ParentId { get; init; }
    public string? ContentType { get; init; }
    public string? StreamToken { get; init; }
}
```

```csharp
// backend/Api/Controllers/Meta/MetaController.cs
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.Meta;

[ApiController]
[Route("api/meta")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class MetaController(DavDatabaseClient dbClient, ConfigManager configManager) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMeta(Guid id, CancellationToken ct)
    {
        var item = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (item is null) return NotFound(new { error = "Item not found" });

        // Generate a signed stream token so Jellyfin can access /api/stream/{id}
        var streamPath = $"/api/stream/{id}";
        var token = StreamTokenService.GenerateToken(streamPath, configManager);

        return Ok(new MetaResponse
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Type = item.Type.ToString(),
            FileSize = item.FileSize,
            CreatedAt = item.CreatedAt,
            ParentId = item.ParentId,
            ContentType = StreamExecutionService.GetContentType(item.Name),
            StreamToken = token
        });
    }
}
```

- [ ] **Step 2: Build and commit**

```bash
cd backend && dotnet build
git add backend/Api/Controllers/Meta/
git commit -m "Add /api/meta/{id} with signed stream token"
```

---

### Task 6: Create StreamFileController — delegates to StreamExecutionService

**Files:**
- Create: `backend/Api/Controllers/StreamFile/StreamFileController.cs`

This controller is intentionally thin. All streaming logic lives in `StreamExecutionService`.

- [ ] **Step 1: Create controller**

```csharp
// backend/Api/Controllers/StreamFile/StreamFileController.cs
using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Api.Controllers.StreamFile;

[ApiController]
[Route("api/stream")]
[ServiceFilter(typeof(ApiKeyAuthFilter))]
public class StreamFileController(
    DatabaseStore store,
    DavDatabaseClient dbClient,
    StreamExecutionService streamService
) : ControllerBase
{
    [HttpGet("{id:guid}")]
    [HttpHead("{id:guid}")]
    public async Task HandleStream(Guid id, CancellationToken ct)
    {
        var davItem = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (davItem is null) { Response.StatusCode = 404; return; }

        // HEAD shortcut — no stream needed
        if (HttpMethods.IsHead(Request.Method))
        {
            streamService.SetFileHeaders(davItem.Name, davItem.FileSize, Response);
            return;
        }

        // Resolve store item and get stream (same pipeline as WebDAV GET)
        var storeItem = await store.GetItemAsync(davItem.Path, ct).ConfigureAwait(false);
        if (storeItem is null || storeItem is IStoreCollection) { Response.StatusCode = 404; return; }

        var stream = await storeItem.GetReadableStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            await streamService.ServeStreamAsync(stream, davItem.Name, Response, Request, ct)
                .ConfigureAwait(false);
        }
    }
}
```

- [ ] **Step 2: Build and test**

```bash
cd backend && dotnet build && cd ../backend.Tests && dotnet test
```

- [ ] **Step 3: Commit**

```bash
git add backend/Api/Controllers/StreamFile/
git commit -m "Add /api/stream/{id} — delegates to StreamExecutionService"
```

---

### Task 7: Refactor GetAndHeadHandlerPatch to use StreamExecutionService

**Files:**
- Modify: `backend/WebDav/Base/GetAndHeadHandlerPatch.cs`

Replace the inline streaming logic with a call to `StreamExecutionService`. This ensures WebDAV GET and REST `/api/stream` use identical Range handling, content headers, and metrics tracking.

- [ ] **Step 1: Add StreamExecutionService to constructor**

Change the constructor to accept `StreamExecutionService`:

```csharp
public class GetAndHeadHandlerPatch : IRequestHandler
{
    private readonly IStore _store;
    private readonly StreamExecutionService _streamService;

    public GetAndHeadHandlerPatch(IStore store, StreamExecutionService streamService)
    {
        _store = store;
        _streamService = streamService;
    }
```

- [ ] **Step 2: Replace the streaming block**

In `HandleRequestAsync`, replace the block from `var stream = await entry.GetReadableStreamAsync(...)` through the stream copy with:

```csharp
var stream = await entry.GetReadableStreamAsync(httpContext.RequestAborted).ConfigureAwait(false);
await using (stream.ConfigureAwait(false))
{
    if (stream != Stream.Null)
    {
        // Set ETag and other property-based headers (WebDAV-specific)
        response.SetStatus(DavStatusCode.Ok);

        if (etag != null && request.Headers.IfNoneMatch == etag)
        {
            response.ContentLength = 0;
            response.SetStatus(DavStatusCode.NotModified);
            return true;
        }

        // Delegate all streaming to the shared service
        await _streamService.ServeStreamAsync(
            stream, entry.Name, response, request, httpContext.RequestAborted
        ).ConfigureAwait(false);
    }
    else
    {
        response.SetStatus(DavStatusCode.NoContent);
    }
}
```

**Note:** The `ServeStreamAsync` call replaces all the inline Range parsing, Content-Length setting, and `CopyRangeToPooledAsync` logic that was previously in this method. The property-based headers (ETag, Last-Modified, Content-Type, Content-Language) remain set by the property manager above this block.

- [ ] **Step 3: Build and test**

```bash
cd backend && dotnet build && cd ../backend.Tests && dotnet test
```

- [ ] **Step 4: Commit**

```bash
git add backend/WebDav/Base/GetAndHeadHandlerPatch.cs
git commit -m "Refactor WebDAV GET to use shared StreamExecutionService"
```

---

## Part 2: Jellyfin Plugin

---

### Task 8: Scaffold plugin project

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Plugin.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/PluginConfiguration.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/config.html`

- [ ] **Step 1: Create project, plugin class, configuration, and config page**

(Same as Plan B v1 Task 6 — project file, Plugin.cs, PluginConfiguration.cs, config.html. No changes from v1.)

```xml
<!-- jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Jellyfin.Controller" Version="10.11.*" />
    <PackageReference Include="Jellyfin.Model" Version="10.11.*" />
  </ItemGroup>
  <ItemGroup>
    <EmbeddedResource Include="Configuration\config.html" />
  </ItemGroup>
</Project>
```

PluginConfiguration includes `NzbdavBaseUrl`, `ApiKey`, `TimeoutSeconds`.

- [ ] **Step 2: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/
git commit -m "Scaffold Jellyfin NZBDAV plugin"
```

---

### Task 9: Create NzbdavApiClient

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/NzbdavApiClient.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/BrowseResponse.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/MetaResponse.cs`

- [ ] **Step 1: Create DTOs (mirror NZBDAV's response shapes)**

```csharp
// BrowseResponse.cs
namespace Jellyfin.Plugin.Nzbdav.Api;
public class BrowseResponse
{
    public string Path { get; set; } = "";
    public BrowseItem[] Items { get; set; } = [];
}
public class BrowseItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}

// MetaResponse.cs
namespace Jellyfin.Plugin.Nzbdav.Api;
public class MetaResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ParentId { get; set; }
    public string? ContentType { get; set; }
    public string? StreamToken { get; set; }
}
```

- [ ] **Step 2: Create API client**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/NzbdavApiClient.cs
using System.Net.Http.Json;
using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav.Api;

public sealed class NzbdavApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly PluginConfiguration _config;

    public NzbdavApiClient(PluginConfiguration config)
    {
        _config = config;
        _http = new HttpClient
        {
            BaseAddress = new Uri(config.NzbdavBaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        _http.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    public Task<BrowseResponse?> BrowseAsync(string path, CancellationToken ct)
        => _http.GetFromJsonAsync<BrowseResponse>($"/api/browse/{path.TrimStart('/')}", ct);

    public Task<MetaResponse?> GetMetaAsync(Guid id, CancellationToken ct)
        => _http.GetFromJsonAsync<MetaResponse>($"/api/meta/{id}", ct);

    /// <summary>
    /// Build a stream URL using the signed token from /api/meta.
    /// Jellyfin will use this URL directly — no custom headers needed.
    /// </summary>
    public string GetSignedStreamUrl(Guid id, string streamToken)
        => $"{_config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{id}?token={streamToken}";

    public void Dispose() => _http.Dispose();
}
```

- [ ] **Step 3: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/
git commit -m "Add NzbdavApiClient with signed stream URL support"
```

---

### Task 10: Implement NzbdavMediaSourceProvider

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs`

- [ ] **Step 1: Create provider**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs
using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

public class NzbdavMediaSourceProvider : IMediaSourceProvider
{
    private readonly ILogger<NzbdavMediaSourceProvider> _logger;

    public NzbdavMediaSourceProvider(ILogger<NzbdavMediaSourceProvider> logger)
    {
        _logger = logger;
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken ct)
    {
        if (!item.ProviderIds.TryGetValue("NzbdavId", out var idStr) || !Guid.TryParse(idStr, out var nzbdavId))
            return [];

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
            return [];

        try
        {
            using var client = new NzbdavApiClient(config);
            var meta = await client.GetMetaAsync(nzbdavId, ct).ConfigureAwait(false);
            if (meta is null) return [];

            var streamUrl = client.GetSignedStreamUrl(nzbdavId, meta.StreamToken ?? "");

            return
            [
                new MediaSourceInfo
                {
                    Id = nzbdavId.ToString("N"),
                    Path = streamUrl,
                    Protocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    IsRemote = true,
                    SupportsDirectStream = true,
                    SupportsDirectPlay = true,
                    SupportsTranscoding = true,
                    RequiresOpening = false,
                    RequiresClosing = false,
                    Container = Path.GetExtension(meta.Name)?.TrimStart('.') ?? "mkv",
                    Name = $"NZBDAV - {meta.Name}",
                    Size = meta.FileSize
                }
            ];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get NZBDAV media source for {Item}", item.Name);
            return [];
        }
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken ct)
        => throw new NotSupportedException("NZBDAV uses HTTP protocol sources.");
}
```

- [ ] **Step 2: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs
git commit -m "Implement NzbdavMediaSourceProvider with signed stream URLs"
```

---

### Task 11: Integration verification

- [ ] **Step 1: Build everything**

```bash
cd backend && dotnet build
cd ../backend.Tests && dotnet test
cd ../jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
```

- [ ] **Step 2: Test REST API**

```bash
cd backend && dotnet run &
sleep 3

# Browse
curl -s -H "X-Api-Key: YOUR_KEY" http://localhost:8080/api/browse/content | python -m json.tool | head -20

# Meta (use an actual item ID)
curl -s -H "X-Api-Key: YOUR_KEY" http://localhost:8080/api/meta/SOME_GUID | python -m json.tool

# Stream HEAD (verify headers)
curl -sI -H "X-Api-Key: YOUR_KEY" http://localhost:8080/api/stream/SOME_GUID

# Stream with signed token from meta response
curl -sI "http://localhost:8080/api/stream/SOME_GUID?token=TOKEN_FROM_META"
```

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "Plan B complete: REST API + Jellyfin plugin with unified streaming"
```

---

## How Plans A and B Integrate

```
                         ┌──────────────────────────┐
                         │    StreamExecutionService │
                         │  (Range, headers, metrics)│
                         └─────┬──────────┬─────────┘
                               │          │
                    ┌──────────▼──┐  ┌────▼─────────────┐
                    │ WebDAV GET  │  │ /api/stream/{id}  │
                    │ (rclone/    │  │ (Jellyfin plugin) │
                    │  Jellyfin)  │  │                   │
                    └─────────────┘  └───────────────────┘
                               │          │
                         ┌─────▼──────────▼─────────┐
                         │  NzbdavMetricsCollector   │
                         │  (scrape-time collection) │
                         └──────────┬───────────────┘
                                    │
                              ┌─────▼─────┐
                              │  /metrics  │
                              └───────────┘
```

- **One streaming path:** `StreamExecutionService` — used by both WebDAV GET and REST stream
- **One auth path:** `ApiKeyAuthFilter` with `StreamTokenService` — used by all REST endpoints
- **One metrics path:** `NzbdavMetricsCollector` with `UseHttpMetrics()` — instruments everything
- **No duplicated logic:** Range parsing, content headers, seek behavior, stream cleanup, and active-stream tracking happen exactly once
