# Plan B: Jellyfin Media Provider Plugin + NZBDAV REST API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Eliminate the rclone FUSE mount by building a Jellyfin plugin that streams directly from NZBDAV over HTTP, and adding a lightweight REST API to NZBDAV that the plugin consumes.

**Architecture:** Two components: (1) New REST API endpoints on NZBDAV backend (`/api/browse`, `/api/meta`, `/api/stream`) that expose the virtual filesystem as JSON + binary streams. (2) A Jellyfin plugin that implements `IMediaSourceProvider` to serve media from these endpoints, eliminating 3 process boundaries (FUSE + rclone + WebDAV XML parsing).

**Tech Stack:** .NET 10 (NZBDAV REST API), Jellyfin Plugin SDK 10.x (C# class library), HttpClient for NZBDAV communication

**Security model:** All REST API endpoints authenticate via the existing NZBDAV API key, accepted as `X-Api-Key` header (preferred) or `?apikey=` query string (SABnzbd compatibility). The key is compared using constant-time comparison (`CryptographicOperations.FixedTimeEquals`) to prevent timing attacks. Authentication is validated in a shared `ApiKeyAuthFilter` action filter — not repeated inline in each controller. The `/metrics` endpoint is unauthenticated (standard Prometheus scraping pattern, restricted to private network via reverse proxy). Stream URLs use the API key in the query string because Jellyfin's HTTP media source provider doesn't support custom headers — the key should be rotatable and the stream endpoints should be behind TLS in production.

**Companion plan:** Plan A (Observability) provides the Prometheus metrics infrastructure. The REST API endpoints created here are instrumented with Plan A's `RequestDurationMiddleware` automatically (it intercepts all HTTP requests). The `/api/browse` and `/api/stream` routes are classified by the middleware's `GetRoutePattern` method.

---

## Part 1: NZBDAV REST API Endpoints

These endpoints are added to the existing NZBDAV backend. They provide JSON metadata and binary streaming that's simpler to consume than WebDAV PROPFIND/GET.

---

### Task 1: Create ApiKeyAuthFilter — shared authentication for REST API

**Files:**
- Create: `backend/Api/Filters/ApiKeyAuthFilter.cs`

This filter is applied to all REST API controllers. It validates the API key using constant-time comparison, adds zero allocation overhead on hot paths (the key bytes are cached), and short-circuits with 401 before any controller logic runs.

- [ ] **Step 1: Create the auth filter**

```csharp
// backend/Api/Filters/ApiKeyAuthFilter.cs
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Config;

namespace NzbWebDAV.Api.Filters;

/// <summary>
/// Action filter that validates the NZBDAV API key from X-Api-Key header or apikey query string.
/// Uses constant-time comparison to prevent timing attacks.
/// </summary>
public class ApiKeyAuthFilter(ConfigManager configManager) : IAsyncActionFilter
{
    private byte[]? _cachedKeyBytes;
    private string? _cachedKeyString;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        var providedKey = request.Headers["X-Api-Key"].FirstOrDefault()
                          ?? request.Query["apikey"].FirstOrDefault();

        if (string.IsNullOrEmpty(providedKey) || !ValidateKey(providedKey))
        {
            context.Result = new UnauthorizedObjectResult(new { error = "Invalid or missing API key" });
            return;
        }

        await next().ConfigureAwait(false);
    }

    private bool ValidateKey(string providedKey)
    {
        try
        {
            var expectedKey = configManager.GetApiKey();

            // Cache the expected key bytes to avoid re-encoding on every request
            if (_cachedKeyString != expectedKey)
            {
                _cachedKeyString = expectedKey;
                _cachedKeyBytes = Encoding.UTF8.GetBytes(expectedKey);
            }

            var providedBytes = Encoding.UTF8.GetBytes(providedKey);
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

In `Program.cs`, add after the existing service registrations:

```csharp
.AddScoped<ApiKeyAuthFilter>()
```

Add using:
```csharp
using NzbWebDAV.Api.Filters;
```

- [ ] **Step 3: Verify build and commit**

Run: `cd backend && dotnet build`

```bash
git add backend/Api/Filters/ApiKeyAuthFilter.cs backend/Program.cs
git commit -m "Add ApiKeyAuthFilter with constant-time key comparison"
```

---

### Task 2: Create BrowseController — directory listing as JSON

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
    public required string Type { get; init; } // "directory", "nzb_file", "rar_file", "multipart_file"
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
            DavItem.ItemType.SymlinkRoot => "directory",
            DavItem.ItemType.IdsRoot => "directory",
            _ => "unknown"
        },
        FileSize = item.FileSize,
        CreatedAt = item.CreatedAt
    };
}
```

- [ ] **Step 2: Create controller**

```csharp
// backend/Api/Controllers/Browse/BrowseController.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Browse;

[ApiController]
[Route("api/browse")]
[ServiceFilter(typeof(NzbWebDAV.Api.Filters.ApiKeyAuthFilter))]
public class BrowseController(DavDatabaseClient dbClient) : ControllerBase
{
    [HttpGet("{*path}")]
    public async Task<IActionResult> Browse(string? path, CancellationToken ct)
    {
        // Authenticate via API key (query param or header)
        // Auth handled by ApiKeyAuthFilter

        // Resolve the directory
        var normalizedPath = "/" + (path?.Trim('/') ?? "");
        var directory = await ResolvePath(normalizedPath, ct).ConfigureAwait(false);
        if (directory is null)
            return NotFound(new { error = $"Path not found: {normalizedPath}" });

        // Get children
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
            var child = await dbClient
                .GetDirectoryChildAsync(current.Id, part, ct)
                .ConfigureAwait(false);
            if (child is null) return null;
            current = child;
        }

        return current;
    }

    // Authentication handled by ApiKeyAuthFilter attribute
}
```

- [ ] **Step 3: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add backend/Api/Controllers/Browse/
git commit -m "Add /api/browse endpoint for JSON directory listing"
```

---

### Task 3: Create MetaController — file metadata as JSON

**Files:**
- Create: `backend/Api/Controllers/Meta/MetaController.cs`
- Create: `backend/Api/Controllers/Meta/MetaResponse.cs`

- [ ] **Step 1: Create response DTO**

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
}
```

- [ ] **Step 2: Create controller**

```csharp
// backend/Api/Controllers/Meta/MetaController.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Meta;

[ApiController]
[Route("api/meta")]
[ServiceFilter(typeof(NzbWebDAV.Api.Filters.ApiKeyAuthFilter))]
public class MetaController(DavDatabaseClient dbClient) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider MimeTypeProvider = new();

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetMeta(Guid id, CancellationToken ct)
    {
        var item = await dbClient.Ctx.Items
            .FindAsync(new object[] { id }, ct)
            .ConfigureAwait(false);
        if (item is null) return NotFound(new { error = "Item not found" });

        return Ok(new MetaResponse
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Type = item.Type switch
            {
                DavItem.ItemType.Directory => "directory",
                DavItem.ItemType.NzbFile => "nzb_file",
                DavItem.ItemType.RarFile => "rar_file",
                DavItem.ItemType.MultipartFile => "multipart_file",
                _ => "unknown"
            },
            FileSize = item.FileSize,
            CreatedAt = item.CreatedAt,
            ParentId = item.ParentId,
            ContentType = GetContentType(item.Name)
        });
    }

    private bool IsAuthenticated()
    {
        var apiKey = HttpContext.Request.Query["apikey"].FirstOrDefault()
                     ?? HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey)) return false;
        var configManager = HttpContext.RequestServices
            .GetRequiredService<NzbWebDAV.Config.ConfigManager>();
        try { return apiKey == configManager.GetApiKey(); }
        catch { return false; }
    }

    private static string? GetContentType(string name)
    {
        if (MimeTypeProvider.TryGetContentType(name, out var contentType))
            return contentType;
        var ext = System.IO.Path.GetExtension(name)?.ToLower();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".nfo" => "text/plain",
            _ => null
        };
    }
}
```

- [ ] **Step 3: Verify build and commit**

Run: `cd backend && dotnet build`

```bash
git add backend/Api/Controllers/Meta/
git commit -m "Add /api/meta/{id} endpoint for file metadata"
```

---

### Task 4: Create StreamController — binary streaming with Range support

**Files:**
- Create: `backend/Api/Controllers/StreamFile/StreamFileController.cs`

This reuses the same `DatabaseStore` + `IStoreItem` pipeline as the existing WebDAV GET, but exposed as a simpler REST endpoint.

- [ ] **Step 1: Create controller**

```csharp
// backend/Api/Controllers/StreamFile/StreamFileController.cs
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using NWebDav.Server.Stores;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Api.Controllers.StreamFile;

[ApiController]
[Route("api/stream")]
[ServiceFilter(typeof(NzbWebDAV.Api.Filters.ApiKeyAuthFilter))]
public class StreamFileController(DatabaseStore store, DavDatabaseClient dbClient) : ControllerBase
{
    private static readonly FileExtensionContentTypeProvider MimeTypeProvider = new();

    [HttpGet("{id:guid}")]
    public async Task StreamFile(Guid id, CancellationToken ct)
    {
        // Look up the DavItem to get its path
        var davItem = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (davItem is null)
        {
            Response.StatusCode = 404;
            return;
        }

        // Resolve via DatabaseStore to get the IStoreItem (which creates the stream pipeline)
        var storeItem = await store.GetItemAsync(davItem.Path, ct).ConfigureAwait(false);
        if (storeItem is null || storeItem is IStoreCollection)
        {
            Response.StatusCode = 404;
            return;
        }

        // Get the readable stream
        var stream = await storeItem.GetReadableStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            // Set headers
            Response.ContentType = GetContentType(davItem.Name);
            Response.Headers.AcceptRanges = "bytes";

            if (!stream.CanSeek)
            {
                // Non-seekable: just stream everything
                await stream.CopyToPooledAsync(Response.Body, cancellationToken: ct).ConfigureAwait(false);
                return;
            }

            var totalLength = stream.Length;

            // Parse Range header
            var rangeHeader = Request.Headers.Range.FirstOrDefault();
            if (!string.IsNullOrEmpty(rangeHeader) && rangeHeader.StartsWith("bytes="))
            {
                var rangeParts = rangeHeader["bytes=".Length..].Split('-');
                var start = long.TryParse(rangeParts[0], out var s) ? s : 0;
                var end = rangeParts.Length > 1 && long.TryParse(rangeParts[1], out var e) ? e : totalLength - 1;
                end = Math.Min(end, totalLength - 1);
                var length = end - start + 1;

                Response.StatusCode = 206;
                Response.ContentLength = length;
                Response.Headers.ContentRange = $"bytes {start}-{end}/{totalLength}";

                await stream.CopyRangeToPooledAsync(Response.Body, start, end, cancellationToken: ct)
                    .ConfigureAwait(false);
            }
            else
            {
                Response.ContentLength = totalLength;
                await stream.CopyToPooledAsync(Response.Body, cancellationToken: ct).ConfigureAwait(false);
            }
        }
    }

    [HttpHead("{id:guid}")]
    public async Task StreamFileHead(Guid id, CancellationToken ct)
    {
        var davItem = await dbClient.Ctx.Items.FindAsync(new object[] { id }, ct).ConfigureAwait(false);
        if (davItem is null) { Response.StatusCode = 404; return; }

        Response.ContentType = GetContentType(davItem.Name);
        Response.Headers.AcceptRanges = "bytes";
        if (davItem.FileSize.HasValue)
            Response.ContentLength = davItem.FileSize.Value;
    }

    private bool IsAuthenticated()
    {
        var apiKey = HttpContext.Request.Query["apikey"].FirstOrDefault()
                     ?? HttpContext.Request.Headers["X-Api-Key"].FirstOrDefault();
        if (string.IsNullOrEmpty(apiKey)) return false;
        var configManager = HttpContext.RequestServices
            .GetRequiredService<NzbWebDAV.Config.ConfigManager>();
        try { return apiKey == configManager.GetApiKey(); }
        catch { return false; }
    }

    private static string GetContentType(string name)
    {
        if (MimeTypeProvider.TryGetContentType(name, out var ct)) return ct;
        var ext = Path.GetExtension(name)?.ToLower();
        return ext switch
        {
            ".mkv" => "video/x-matroska",
            ".nfo" => "text/plain",
            _ => "application/octet-stream"
        };
    }
}
```

- [ ] **Step 2: Verify build**

Run: `cd backend && dotnet build`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run tests**

Run: `cd backend.Tests && dotnet test`
Expected: All tests pass.

- [ ] **Step 4: Commit**

```bash
git add backend/Api/Controllers/StreamFile/
git commit -m "Add /api/stream/{id} endpoint for direct binary streaming"
```

---

### Task 5: Update RequestDurationMiddleware route patterns (Plan A integration)

**Files:**
- Modify: `backend/Metrics/RequestDurationMiddleware.cs`

If Plan A has been implemented, update the `GetRoutePattern` method to classify the new endpoints. If Plan A hasn't been implemented yet, skip this task — the middleware will classify them automatically via ASP.NET route pattern matching.

- [ ] **Step 1: Add route patterns**

In `GetRoutePattern`, add before the fallback:

```csharp
if (path.StartsWith("/api/browse", StringComparison.OrdinalIgnoreCase)) return "/api/browse/{path}";
if (path.StartsWith("/api/meta/", StringComparison.OrdinalIgnoreCase)) return "/api/meta/{id}";
if (path.StartsWith("/api/stream/", StringComparison.OrdinalIgnoreCase)) return "/api/stream/{id}";
```

- [ ] **Step 2: Commit**

```bash
git add backend/Metrics/RequestDurationMiddleware.cs
git commit -m "Add route patterns for REST API metrics classification"
```

---

## Part 2: Jellyfin Plugin

This is a separate .NET class library project that builds as a Jellyfin plugin DLL. It does NOT modify the Jellyfin source code — it uses Jellyfin's plugin API.

---

### Task 6: Scaffold plugin project

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Plugin.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/PluginConfiguration.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/config.html`

- [ ] **Step 1: Create project file**

```xml
<!-- jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
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

- [ ] **Step 2: Create plugin class**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Plugin.cs
using System;
using System.Collections.Generic;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Nzbdav;

public class Plugin : BasePlugin<Configuration.PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    public override Guid Id => new("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
    public override string Name => "NZBDAV";
    public override string Description => "Stream media directly from NZBDAV without rclone.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.config.html"
        };
    }
}
```

- [ ] **Step 3: Create plugin configuration**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/PluginConfiguration.cs
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Nzbdav.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string NzbdavBaseUrl { get; set; } = "http://nzbdav:3000";
    public string ApiKey { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
}
```

- [ ] **Step 4: Create config page**

```html
<!-- jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/config.html -->
<!DOCTYPE html>
<html>
<head>
    <title>NZBDAV Plugin</title>
</head>
<body>
    <div id="NzbdavConfigPage" data-role="page" class="page type-interior pluginConfigurationPage">
        <div data-role="content">
            <div class="content-primary">
                <form id="NzbdavConfigForm">
                    <div class="inputContainer">
                        <label for="txtNzbdavUrl">NZBDAV Server URL</label>
                        <input id="txtNzbdavUrl" type="text" is="emby-input" />
                        <div class="fieldDescription">e.g. http://nzbdav:3000</div>
                    </div>
                    <div class="inputContainer">
                        <label for="txtApiKey">API Key</label>
                        <input id="txtApiKey" type="password" is="emby-input" />
                    </div>
                    <div class="inputContainer">
                        <label for="txtTimeout">Timeout (seconds)</label>
                        <input id="txtTimeout" type="number" is="emby-input" value="30" />
                    </div>
                    <button is="emby-button" type="submit" class="raised button-submit block">
                        <span>Save</span>
                    </button>
                </form>
            </div>
        </div>
    </div>
    <script type="text/javascript">
        var NzbdavConfig = {
            pluginUniqueId: 'a1b2c3d4-e5f6-7890-abcd-ef1234567890'
        };
        document.querySelector('#NzbdavConfigPage').addEventListener('pageshow', function () {
            ApiClient.getPluginConfiguration(NzbdavConfig.pluginUniqueId).then(function (config) {
                document.querySelector('#txtNzbdavUrl').value = config.NzbdavBaseUrl || '';
                document.querySelector('#txtApiKey').value = config.ApiKey || '';
                document.querySelector('#txtTimeout').value = config.TimeoutSeconds || 30;
            });
        });
        document.querySelector('#NzbdavConfigForm').addEventListener('submit', function (e) {
            e.preventDefault();
            ApiClient.getPluginConfiguration(NzbdavConfig.pluginUniqueId).then(function (config) {
                config.NzbdavBaseUrl = document.querySelector('#txtNzbdavUrl').value;
                config.ApiKey = document.querySelector('#txtApiKey').value;
                config.TimeoutSeconds = parseInt(document.querySelector('#txtTimeout').value) || 30;
                ApiClient.updatePluginConfiguration(NzbdavConfig.pluginUniqueId, config).then(function () {
                    Dashboard.processPluginConfigurationUpdateResult();
                });
            });
        });
    </script>
</body>
</html>
```

- [ ] **Step 5: Verify build**

Run: `cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build`
Expected: Build succeeded. (May need to adjust Jellyfin package versions based on what's available.)

- [ ] **Step 6: Commit**

```bash
git add jellyfin-plugin/
git commit -m "Scaffold Jellyfin plugin project with configuration"
```

---

### Task 7: Create NzbdavApiClient — HTTP client for NZBDAV REST API

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/NzbdavApiClient.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/BrowseResponse.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/MetaResponse.cs`

- [ ] **Step 1: Create DTOs**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/BrowseResponse.cs
namespace Jellyfin.Plugin.Nzbdav.Api;

public class BrowseResponse
{
    public string Path { get; set; } = string.Empty;
    public BrowseItem[] Items { get; set; } = [];
}

public class BrowseItem
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
}
```

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/MetaResponse.cs
namespace Jellyfin.Plugin.Nzbdav.Api;

public class MetaResponse
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? ParentId { get; set; }
    public string? ContentType { get; set; }
}
```

- [ ] **Step 2: Create API client**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/NzbdavApiClient.cs
using System.Net.Http.Json;
using Jellyfin.Plugin.Nzbdav.Configuration;

namespace Jellyfin.Plugin.Nzbdav.Api;

public class NzbdavApiClient : IDisposable
{
    private readonly HttpClient _httpClient;

    public NzbdavApiClient(PluginConfiguration config)
    {
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.NzbdavBaseUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(config.TimeoutSeconds)
        };
        _httpClient.DefaultRequestHeaders.Add("X-Api-Key", config.ApiKey);
    }

    public async Task<BrowseResponse?> BrowseAsync(string path, CancellationToken ct)
    {
        var url = $"/api/browse/{path.TrimStart('/')}";
        return await _httpClient.GetFromJsonAsync<BrowseResponse>(url, ct).ConfigureAwait(false);
    }

    public async Task<MetaResponse?> GetMetaAsync(Guid id, CancellationToken ct)
    {
        return await _httpClient.GetFromJsonAsync<MetaResponse>($"/api/meta/{id}", ct).ConfigureAwait(false);
    }

    public string GetStreamUrl(Guid id)
    {
        var config = Plugin.Instance?.Configuration;
        return $"{config?.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{id}?apikey={config?.ApiKey}";
    }

    public async Task<HttpResponseMessage> GetStreamAsync(Guid id, long? rangeStart, long? rangeEnd, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/stream/{id}");
        if (rangeStart.HasValue)
        {
            var rangeEnd2 = rangeEnd.HasValue ? rangeEnd.Value.ToString() : "";
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(rangeStart.Value, rangeEnd);
        }

        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}
```

- [ ] **Step 3: Verify build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Api/
git commit -m "Add NzbdavApiClient for REST API communication"
```

---

### Task 8: Implement NzbdavMediaSourceProvider

**Files:**
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs`
- Create: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLiveStream.cs`

This is the core of the plugin — it tells Jellyfin how to get media streams from NZBDAV.

- [ ] **Step 1: Create the media source provider**

```csharp
// jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs
using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
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

    public Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        // Only handle items that have NZBDAV provider IDs
        if (!item.ProviderIds.TryGetValue("NzbdavId", out var nzbdavIdStr)
            || !Guid.TryParse(nzbdavIdStr, out var nzbdavId))
        {
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());
        }

        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
            return Task.FromResult(Enumerable.Empty<MediaSourceInfo>());

        // Build a media source that points to NZBDAV's stream endpoint
        var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{nzbdavId}?apikey={config.ApiKey}";

        var mediaSource = new MediaSourceInfo
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
            Container = System.IO.Path.GetExtension(item.Path)?.TrimStart('.') ?? "mkv",
            Name = $"NZBDAV - {item.Name}",
            Size = item.Size
        };

        _logger.LogDebug("Providing NZBDAV media source for {ItemName} ({NzbdavId})", item.Name, nzbdavId);
        return Task.FromResult<IEnumerable<MediaSourceInfo>>(new[] { mediaSource });
    }

    public Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // Not needed for HTTP protocol sources — Jellyfin handles HTTP streaming directly
        throw new NotSupportedException("NZBDAV sources use HTTP protocol and don't require OpenMediaSource.");
    }
}
```

- [ ] **Step 2: Verify build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavMediaSourceProvider.cs
git commit -m "Implement NzbdavMediaSourceProvider for direct HTTP streaming"
```

---

### Task 9: Create plugin build and packaging script

**Files:**
- Create: `jellyfin-plugin/build.sh`

- [ ] **Step 1: Create build script**

```bash
#!/bin/bash
# jellyfin-plugin/build.sh
set -e

PROJECT="Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj"
OUTPUT="artifacts"

echo "Building NZBDAV Jellyfin plugin..."
dotnet build "$PROJECT" -c Release

echo "Publishing plugin..."
dotnet publish "$PROJECT" -c Release -o "$OUTPUT"

echo "Plugin built successfully at $OUTPUT/"
echo "Copy the .dll files to your Jellyfin plugins directory:"
echo "  cp $OUTPUT/Jellyfin.Plugin.Nzbdav.dll /path/to/jellyfin/plugins/Nzbdav/"
```

- [ ] **Step 2: Commit**

```bash
chmod +x jellyfin-plugin/build.sh
git add jellyfin-plugin/build.sh
git commit -m "Add plugin build script"
```

---

### Task 10: Integration verification

- [ ] **Step 1: Build everything**

```bash
cd backend && dotnet build
cd ../jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
cd ../../backend.Tests && dotnet test
```

Expected: All builds succeed, all tests pass.

- [ ] **Step 2: Test REST API endpoints manually**

Start NZBDAV and test the new endpoints:

```bash
cd backend && dotnet run &
sleep 3

# Browse root
curl -s "http://localhost:8080/api/browse?apikey=YOUR_KEY" | head -20

# Browse content
curl -s "http://localhost:8080/api/browse/content?apikey=YOUR_KEY" | head -20

# Get metadata for a known item ID
curl -s "http://localhost:8080/api/meta/SOME_GUID?apikey=YOUR_KEY"

# Stream a file (HEAD to verify)
curl -sI "http://localhost:8080/api/stream/SOME_GUID?apikey=YOUR_KEY"
```

Expected: JSON responses for browse/meta, proper headers for stream.

- [ ] **Step 3: Final commit**

```bash
git add -A
git commit -m "Jellyfin plugin + NZBDAV REST API: eliminate rclone dependency"
```

---

## Integration Points Between Plan A and Plan B

| Plan A Provides | Plan B Uses |
|----------------|-------------|
| `RequestDurationMiddleware` | Automatically instruments `/api/browse`, `/api/meta`, `/api/stream` |
| `NzbdavMetricsCollector.IncrementActiveStreams()` | Called from `StreamFileController` when streaming starts |
| `/metrics` endpoint | Plugin health can be verified via `nzbdav_http_request_duration_seconds{route="/api/stream/{id}"}` |
| `nzbdav_streams_active` gauge | Counts both WebDAV and REST API streams |

**Note for StreamFileController:** Add active stream tracking (same as GetAndHeadHandlerPatch from Plan A):

```csharp
// In StreamFileController.StreamFile, wrap the stream copy:
NzbdavMetricsCollector.IncrementActiveStreams();
try { await stream.CopyRangeToPooledAsync(...); }
finally { NzbdavMetricsCollector.DecrementActiveStreams(); }
```

This is included in Task 3's StreamFileController code above if Plan A is implemented first. If not, add it later when Plan A ships.
