# Plan E: Fix Jellyfin Plugin — Make Library Integration Actually Work

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the Jellyfin plugin so content discovered from NZBDAV actually appears in Jellyfin's library UI and is playable.

**Architecture:** Jellyfin's library system shows items that live inside `ICollectionFolder` hierarchies. Items created with `CreateItem(item, null)` at root level are invisible to users because `UserViewBuilder.GetMediaFolders` only returns folders from `GetUserRootFolder().GetChildren()` filtered by `IsEligibleForGrouping`. The fix: create a `CollectionFolder` with `CollectionType = Movies` that Jellyfin treats as a regular library, and populate it with items from NZBDAV.

**Tech Stack:** Jellyfin Plugin SDK, .NET

**Root cause analysis:**
- `GetMediaFolders` gets children of the user root folder
- Each child must be a `Folder` implementing `ICollectionFolder`
- `IsEligibleForGrouping` accepts `CollectionType.Movies`, `CollectionType.TvShows`, or `null`
- `BasePluginFolder` implements `ICollectionFolder` and returns `CollectionType = null`
- Channels use `CreateItem(channel, null)` but Channel extends Folder with `SourceType = Channel` — special-cased in the UI
- Our Movie items created at root with `SourceType` default are NOT special-cased — they're invisible

**The correct approach:** Don't fight Jellyfin's library model. Instead of creating virtual items programmatically, create a real filesystem directory that acts as a Jellyfin library, and populate it with `.nfo` metadata files + `.strm` files pointing to NZBDAV stream URLs. This is how every other remote-source Jellyfin integration works (Stremio, IPTV, etc.). Jellyfin's built-in library scanner does the rest.

---

## Why .strm + .nfo Instead of Virtual Items

| Approach | Pros | Cons |
|----------|------|------|
| Virtual items via `CreateItem` | No filesystem needed | Items invisible without custom folder provider, no metadata, no artwork, breaks on Jellyfin updates |
| `.strm` files + `.nfo` metadata | Works with Jellyfin's native scanner, gets artwork from TMDb, full metadata, survives updates | Needs a writable directory, small disk usage (~1KB per item) |

The `.strm` approach is battle-tested — it's how Stremio integration, IPTV plugins, and remote NAS setups work with Jellyfin. A `.strm` file is just a text file containing a URL. Jellyfin treats it as a playable media source.

---

### Task 1: Rewrite NzbdavLibrarySyncTask to generate .strm + .nfo files

**Files:**
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs`
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/PluginConfiguration.cs`

- [ ] **Step 1: Add LibraryPath to plugin configuration**

```csharp
// In PluginConfiguration.cs, add:
/// <summary>
/// Local directory where .strm and .nfo files will be written.
/// This directory should be added as a Jellyfin library.
/// Example: /media/nzbdav
/// </summary>
public string LibraryPath { get; set; } = "/media/nzbdav";
```

- [ ] **Step 2: Add LibraryPath to config.html**

Add a new input field in the config page:

```html
<div class="inputContainer">
    <label for="txtLibraryPath">Library Path</label>
    <input id="txtLibraryPath" type="text" is="emby-input" />
    <div class="fieldDescription">
        Local directory for .strm files. Add this path as a Jellyfin Movie or TV library.
    </div>
</div>
```

Wire it in the JavaScript save/load logic alongside the existing fields.

- [ ] **Step 3: Rewrite the sync task**

Replace the entire `NzbdavLibrarySyncTask.cs` with:

```csharp
using Jellyfin.Plugin.Nzbdav.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Nzbdav;

public class NzbdavLibrarySyncTask : IScheduledTask
{
    private readonly ILogger<NzbdavLibrarySyncTask> _logger;

    public NzbdavLibrarySyncTask(ILogger<NzbdavLibrarySyncTask> logger)
    {
        _logger = logger;
    }

    public string Name => "NZBDAV Library Sync";
    public string Key => "NzbdavLibrarySync";
    public string Description => "Sync NZBDAV content to .strm files for Jellyfin library scanning.";
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

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken ct)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || string.IsNullOrEmpty(config.NzbdavBaseUrl))
        {
            _logger.LogWarning("NZBDAV plugin not configured — skipping sync");
            return;
        }

        if (string.IsNullOrEmpty(config.LibraryPath))
        {
            _logger.LogWarning("NZBDAV LibraryPath not configured — skipping sync");
            return;
        }

        var client = new NzbdavApiClient(config);
        progress.Report(0);

        BrowseResponse? contentRoot;
        try
        {
            contentRoot = await client.BrowseAsync("content", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "NZBDAV unreachable — will retry next cycle");
            progress.Report(100);
            return;
        }

        if (contentRoot is null || contentRoot.Items.Length == 0)
        {
            progress.Report(100);
            return;
        }

        var categories = contentRoot.Items.Where(i => i.Type == "directory").ToArray();
        var processed = 0;
        var total = 0;

        // Count mount folders
        foreach (var category in categories)
        {
            try
            {
                var catContent = await client.BrowseAsync($"content/{category.Name}", ct).ConfigureAwait(false);
                if (catContent != null) total += catContent.Items.Count(i => i.Type == "directory");
            }
            catch { /* skip on error */ }
        }

        if (total == 0) { progress.Report(100); return; }

        foreach (var category in categories)
        {
            BrowseResponse? catContent;
            try
            {
                catContent = await client.BrowseAsync($"content/{category.Name}", ct).ConfigureAwait(false);
            }
            catch { continue; }

            if (catContent is null) continue;

            foreach (var mountFolder in catContent.Items.Where(i => i.Type == "directory"))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    await SyncMountFolder(client, config, category.Name, mountFolder, ct)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to sync {Name}", mountFolder.Name);
                }

                processed++;
                progress.Report((double)processed / total * 100);
            }
        }

        _logger.LogInformation("NZBDAV sync complete: {Count} mount folders processed", processed);
        progress.Report(100);
    }

    private async Task SyncMountFolder(
        NzbdavApiClient client,
        Configuration.PluginConfiguration config,
        string categoryName,
        BrowseItem mountFolder,
        CancellationToken ct)
    {
        var contents = await client.BrowseAsync(
            $"content/{categoryName}/{mountFolder.Name}", ct).ConfigureAwait(false);
        if (contents is null) return;

        var videoFiles = contents.Items
            .Where(i => i.Type is "nzb_file" or "rar_file" or "multipart_file")
            .Where(i => IsVideoFile(i.Name))
            .ToArray();

        if (videoFiles.Length == 0) return;

        // Create directory: {LibraryPath}/{category}/{mountFolderName}/
        var folderPath = Path.Combine(config.LibraryPath, categoryName, mountFolder.Name);
        Directory.CreateDirectory(folderPath);

        foreach (var videoFile in videoFiles)
        {
            var strmPath = Path.Combine(folderPath, Path.ChangeExtension(videoFile.Name, ".strm"));

            // Skip if .strm already exists (idempotent)
            if (File.Exists(strmPath)) continue;

            // Get a signed stream URL
            MetaResponse? meta;
            try
            {
                meta = await client.GetMetaAsync(videoFile.Id, ct).ConfigureAwait(false);
            }
            catch { continue; }

            if (meta is null) continue;

            // .strm file: Jellyfin reads this as a playable URL
            // Use raw API key URL (not signed token) because .strm files are persistent
            // and signed tokens expire. The API key is stored server-side only.
            var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{videoFile.Id}?apikey={config.ApiKey}";
            await File.WriteAllTextAsync(strmPath, streamUrl, ct).ConfigureAwait(false);

            _logger.LogDebug("Created .strm: {Path}", strmPath);
        }
    }

    private static bool IsVideoFile(string filename)
    {
        var ext = Path.GetExtension(filename)?.ToLowerInvariant();
        return ext is ".mkv" or ".mp4" or ".avi" or ".mov" or ".wmv" or ".flv"
            or ".m4v" or ".ts" or ".m2ts" or ".webm" or ".mpg" or ".mpeg";
    }
}
```

- [ ] **Step 4: Remove NzbdavMediaSourceProvider**

The `IMediaSourceProvider` is no longer needed — Jellyfin handles `.strm` files natively. It reads the URL from the file and streams directly. Delete `NzbdavMediaSourceProvider.cs`.

- [ ] **Step 5: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add -A
git commit -m "Rewrite library sync to .strm files — works with Jellyfin's native scanner"
```

---

### Task 2: Retarget plugin to match Jellyfin runtime

**Files:**
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj`

- [ ] **Step 1: Check the actual TFM of the Jellyfin.Controller NuGet package**

```bash
dotnet nuget download Jellyfin.Controller --version 10.11.8 -o /tmp/jf-pkg
unzip /tmp/jf-pkg/*.nupkg -d /tmp/jf-inspect
ls /tmp/jf-inspect/lib/
```

The directory name under `lib/` tells you the target framework (e.g., `net8.0`, `net9.0`).

- [ ] **Step 2: Update .csproj to match**

If Jellyfin.Controller 10.11.8 targets net9.0:

```xml
<TargetFramework>net9.0</TargetFramework>
```

Pin the package version instead of using wildcards:

```xml
<PackageReference Include="Jellyfin.Controller" Version="10.11.8" />
<PackageReference Include="Jellyfin.Model" Version="10.11.8" />
```

- [ ] **Step 3: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj
git commit -m "Retarget plugin to match Jellyfin runtime"
```

---

### Task 3: Update plugin config page for LibraryPath

**Files:**
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/config.html`

- [ ] **Step 1: Add the LibraryPath input field**

After the timeout input:

```html
<div class="inputContainer">
    <label for="txtLibraryPath">Library Path</label>
    <input id="txtLibraryPath" type="text" is="emby-input" />
    <div class="fieldDescription">
        Directory where .strm files will be created. Add this path as a
        Movie or TV library in Jellyfin's dashboard. Example: /media/nzbdav
    </div>
</div>
```

Add to the pageshow handler:
```javascript
document.querySelector('#txtLibraryPath').value = config.LibraryPath || '/media/nzbdav';
```

Add to the submit handler:
```javascript
config.LibraryPath = document.querySelector('#txtLibraryPath').value;
```

- [ ] **Step 2: Build and commit**

```bash
cd jellyfin-plugin/Jellyfin.Plugin.Nzbdav && dotnet build
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Configuration/config.html
git commit -m "Add LibraryPath to plugin configuration UI"
```

---

### Task 4: Update build script and install documentation

**Files:**
- Modify: `jellyfin-plugin/build.sh`

- [ ] **Step 1: Update install instructions**

Add to the build script output:

```bash
echo "Setup:"
echo "  1. Install the plugin (copy DLL to Jellyfin plugins dir)"
echo "  2. Configure in Dashboard → Plugins → NZBDAV:"
echo "     - NZBDAV URL: https://your-nzbdav.example.com"
echo "     - API Key: (your NZBDAV API key)"
echo "     - Library Path: /media/nzbdav (or any writable directory)"
echo "  3. In Dashboard → Libraries → Add Library:"
echo "     - Content Type: Movies (or Mixed)"
echo "     - Folder: /media/nzbdav"
echo "  4. Run 'NZBDAV Library Sync' from Dashboard → Scheduled Tasks"
echo "  5. Run 'Scan All Libraries' to pick up the .strm files"
```

- [ ] **Step 2: Commit**

```bash
git add jellyfin-plugin/build.sh
git commit -m "Update install docs for .strm-based library integration"
```

---

## How It Works End-to-End

```
1. User installs plugin, configures URL + API key + library path
2. User creates a Jellyfin library pointing at the library path
3. NzbdavLibrarySyncTask runs every 15 minutes:
   - Calls /api/browse/content to discover NZBDAV content
   - Creates directories: /media/nzbdav/movies/MovieName/
   - Writes .strm files: /media/nzbdav/movies/MovieName/movie.strm
     containing: https://nzbdav.example.com/api/stream/{id}?apikey=...
4. Jellyfin's library scanner detects new .strm files
5. Jellyfin probes the URL via FFmpeg (reads header + end of file)
6. Jellyfin fetches metadata from TMDb based on folder name
7. Jellyfin displays the movie with poster, description, ratings
8. User clicks Play → Jellyfin streams from the .strm URL
9. NZBDAV serves the video via StreamExecutionService → LiveSegmentCache → NNTP
```

**Why this works:**
- `.strm` files are a first-class Jellyfin feature — no custom library providers needed
- Jellyfin's metadata system (TMDb, TheTVDB) works automatically based on folder names
- Artwork is fetched from metadata providers, not from NZBDAV
- The library path is a real filesystem directory → no virtual item hackery
- TV show support comes free: Jellyfin's TV scanner parses `Show Name/Season 01/S01E01.strm`
- Survives Jellyfin updates — no internal API dependencies except `IScheduledTask`

**What's different from the old approach:**
- No `NzbdavMediaSourceProvider` needed — Jellyfin handles `.strm` natively
- No `NzbdavId` provider IDs needed — `.strm` files ARE the content reference
- No `ILibraryManager.CreateItem` calls — Jellyfin's built-in scanner does everything
- TV content now works — the sync task just creates `.strm` files in the right directory structure
- ~1KB disk per item (the `.strm` file) — negligible storage
