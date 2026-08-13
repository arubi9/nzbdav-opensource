# Jellyfin Quarantine Reconciliation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reconcile stale Jellyfin mirror files by quarantining obsolete NZBDAV-managed `.strm` and `.mediainfo.json` files when the manifest changes.

**Architecture:** Keep the manifest contract unchanged. The plugin computes the expected active relative `.strm` paths from the manifest, updates active files, scans the library tree for NZBDAV-managed `.strm` files, and moves any stale entries into `LibraryPath/.quarantine/<run-id>/...` while preserving relative structure.

**Tech Stack:** C#, Jellyfin plugin task, xUnit

---

### Task 1: Add failing tests for reconciliation helpers

**Files:**
- Modify: `jellyfin-plugin.Tests/NzbdavLibrarySyncTaskTests.cs`

- [ ] **Step 1: Write the failing tests**

Add tests for new helper behavior:

```csharp
[Fact]
public void IsNzbdavManagedStrmContent_ReturnsTrue_ForMatchingBaseUrlAndApiStream()
{
    var result = InvokeIsNzbdavManagedStrmContent(
        "https://nzbdav.example/api/stream/abc?apikey=secret",
        "https://nzbdav.example");

    Assert.True(result);
}

[Fact]
public void IsNzbdavManagedStrmContent_ReturnsFalse_ForForeignUrl()
{
    var result = InvokeIsNzbdavManagedStrmContent(
        "https://other.example/video.strm",
        "https://nzbdav.example");

    Assert.False(result);
}

[Fact]
public void GetQuarantinePath_PreservesRelativeStructureUnderQuarantineRoot()
{
    var result = InvokeGetQuarantineRelativePath(
        Path.Combine("shows", "Series", "Episode.strm"),
        "20260416-120000");

    Assert.Equal(Normalize(Path.Combine(".quarantine", "20260416-120000", "shows", "Series", "Episode.strm")), Normalize(result));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NzbdavLibrarySyncTaskTests"`

Expected: FAIL because the helper methods do not exist yet.

- [ ] **Step 3: Commit**

```bash
git add jellyfin-plugin.Tests/NzbdavLibrarySyncTaskTests.cs
git commit -m "test: cover jellyfin quarantine reconciliation helpers"
```

---

### Task 2: Implement helper methods and remove dead token-refresh logic

**Files:**
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs`

- [ ] **Step 1: Implement the helper methods**

Add private static helpers for:
- `IsNzbdavManagedStrmContent(string content, string baseUrl)`
- `GetQuarantineRelativePath(string relativePath, string runId)`

Keep them small and deterministic.

- [ ] **Step 2: Remove dead token-refresh logic from SyncVideoFile**

Delete the existing `ExtractToken` / `IsTokenStale` usage path and always write the current API-key URL for active items:

```csharp
var streamUrl = $"{config.NzbdavBaseUrl.TrimEnd('/')}/api/stream/{videoFile.Id}?apikey={config.ApiKey}";
File.WriteAllText(strmPath, streamUrl);
```

Remove helper methods that are no longer used.

- [ ] **Step 3: Run test to verify it passes**

Run: `dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NzbdavLibrarySyncTaskTests"`

Expected: PASS

- [ ] **Step 4: Commit**

```bash
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs jellyfin-plugin.Tests/NzbdavLibrarySyncTaskTests.cs
git commit -m "refactor: remove dead token refresh logic from jellyfin sync"
```

---

### Task 3: Implement stale-file quarantine reconciliation

**Files:**
- Modify: `jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs`

- [ ] **Step 1: Add failing tests for active-path and quarantine decisions**

Extend `NzbdavLibrarySyncTaskTests` with helper-level tests for:

```csharp
[Fact]
public void BuildExpectedStrmRelativePaths_ReturnsOnlyVideoItems()
{
    // Arrange manifest items with one video and one directory
    // Assert only the video .strm relative path is returned
}

[Fact]
public void MatchingProbeSidecar_IsDerivedFromStrmPath()
{
    var strmPath = Path.Combine("movies", "Movie", "Movie.strm");
    var expected = Path.Combine("movies", "Movie", "Movie.mediainfo.json");

    Assert.Equal(Normalize(expected), Normalize(Path.ChangeExtension(strmPath, ".mediainfo.json")));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~NzbdavLibrarySyncTaskTests"`

Expected: FAIL because the reconciliation helpers do not exist yet.

- [ ] **Step 3: Implement reconciliation flow in ExecuteAsync**

After fetching a changed manifest:
- compute the expected active relative `.strm` path set
- sync active files
- call a new reconciliation helper that:
  - scans `LibraryPath` recursively for `.strm`
  - skips anything already under `.quarantine`
  - skips non-NZBDAV-managed `.strm`
  - moves stale `.strm` and matching `.mediainfo.json` into `.quarantine/<run-id>/...`

- [ ] **Step 4: Run plugin tests**

Run: `dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj -c Release --no-restore`

Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add jellyfin-plugin/Jellyfin.Plugin.Nzbdav/NzbdavLibrarySyncTask.cs jellyfin-plugin.Tests/NzbdavLibrarySyncTaskTests.cs
git commit -m "feat: quarantine stale jellyfin strm mirror files"
```

---

### Task 4: Final verification

**Files:**
- Reuse existing suites

- [ ] **Step 1: Run plugin tests**

Run: `dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj -c Release --no-restore`

Expected: PASS

- [ ] **Step 2: Run backend tests to ensure no accidental repo-wide regression**

Run: `dotnet test backend.Tests/backend.Tests.csproj -c Release --no-restore`

Expected: PASS

