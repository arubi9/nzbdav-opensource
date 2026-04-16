using System.Reflection;
using Jellyfin.Plugin.Nzbdav;
using Jellyfin.Plugin.Nzbdav.Api;
using Jellyfin.Plugin.Nzbdav.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavLibrarySyncTaskTests
{
    private static readonly MethodInfo BuildRelativePathMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("BuildStrmRelativePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildStrmRelativePath.");
    private static readonly MethodInfo IsNzbdavManagedStrmContentMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("IsNzbdavManagedStrmContent", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find IsNzbdavManagedStrmContent.");
    private static readonly MethodInfo GetQuarantineRelativePathMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("GetQuarantineRelativePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find GetQuarantineRelativePath.");
    private static readonly MethodInfo BuildExpectedStrmRelativePathsMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("BuildExpectedStrmRelativePaths", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildExpectedStrmRelativePaths.");
    private static readonly MethodInfo ReconcileStaleFilesMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("ReconcileStaleFiles", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not find ReconcileStaleFiles.");

    [Fact]
    public void BuildStrmRelativePath_UsesParentDirectoryName_ForObfuscatedVideoFile()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Family.Guy.S24E07.1080p.WEB.h264-EDITH",
            Path = "/content/uncategorized/Family.Guy.S24E07.1080p.WEB.h264-EDITH",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "W6Ss3ROn1dPrVxlU916rJLYwTk6QbtDe.mkv",
            Path = "/content/uncategorized/Family.Guy.S24E07.1080p.WEB.h264-EDITH/W6Ss3ROn1dPrVxlU916rJLYwTk6QbtDe.mkv",
            Type = "nzb_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(
            Normalize(Path.Combine("uncategorized", parent.Name, parent.Name + ".mkv")),
            Normalize(result));
    }

    [Fact]
    public void BuildStrmRelativePath_KeepsReadableReleaseName()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR",
            Path = "/content/uncategorized/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR.mkv",
            Path = "/content/uncategorized/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR.mkv",
            Type = "multipart_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(
            Normalize(Path.Combine("uncategorized", parent.Name, video.Name)),
            Normalize(result));
    }

    [Fact]
    public void BuildStrmRelativePath_DoesNotTreatPlainSingleWordTitleAsObfuscated()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Interstellar",
            Path = "/content/movies/Interstellar",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "Interstellar.mkv",
            Path = "/content/movies/Interstellar/Interstellar.mkv",
            Type = "nzb_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(Normalize(Path.Combine("movies", parent.Name, video.Name)), Normalize(result));
    }

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
    public void GetQuarantineRelativePath_PreservesRelativeStructureUnderQuarantineRoot()
    {
        var result = InvokeGetQuarantineRelativePath(
            Path.Combine("shows", "Series", "Episode.strm"),
            "20260416-120000");

        Assert.Equal(
            Normalize(Path.Combine(".quarantine", "20260416-120000", "shows", "Series", "Episode.strm.quarantined")),
            Normalize(result));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_ReturnsOnlyVideoItems()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Movie",
            Path = "/content/movies/Movie",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };
        var directory = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "extras",
            Path = "/content/movies/Movie/extras",
            Type = "directory"
        };

        var result = InvokeBuildExpectedStrmRelativePaths(
            [parent, video, directory],
            new Dictionary<Guid, ManifestItem>
            {
                [parent.Id] = parent,
                [video.Id] = video,
                [directory.Id] = directory
            });

        Assert.Equal(
            [Normalize(Path.Combine("movies", "Movie", "Movie.strm"))],
            result.Select(Normalize).ToArray());
    }

    [Fact]
    public void ReconcileStaleFiles_QuarantinesOnlyStaleManagedFiles_AndDropsNomedia()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);

        try
        {
            var activeRelative = Path.Combine("movies", "Movie", "Movie.strm");
            var staleRelative = Path.Combine("movies", "OldMovie", "OldMovie.strm");
            var foreignRelative = Path.Combine("foreign", "Foreign.strm");
            var quarantinedRelative = Path.Combine(".quarantine", "older-run", "movies", "Ghost.strm.quarantined");

            WriteFile(libraryPath, activeRelative, "https://nzbdav.example/api/stream/active?apikey=secret");
            WriteFile(libraryPath, staleRelative, "https://nzbdav.example/api/stream/stale?apikey=secret");
            WriteFile(libraryPath, Path.ChangeExtension(staleRelative, ".mediainfo.json"), "{ }");
            WriteFile(libraryPath, foreignRelative, "https://other.example/video.strm");
            WriteFile(libraryPath, quarantinedRelative, "https://nzbdav.example/api/stream/ghost?apikey=secret");

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example"
            };

            InvokeReconcileStaleFiles(task, config, [activeRelative], "20260416-120000");

            Assert.True(File.Exists(Path.Combine(libraryPath, activeRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, foreignRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, quarantinedRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", ".nomedia")));
            Assert.False(File.Exists(Path.Combine(libraryPath, staleRelative)));
            Assert.False(File.Exists(Path.Combine(libraryPath, Path.ChangeExtension(staleRelative, ".mediainfo.json"))));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", "20260416-120000", "movies", "OldMovie", "OldMovie.strm.quarantined")));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", "20260416-120000", "movies", "OldMovie", "OldMovie.mediainfo.json.quarantined")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    private static string InvokeBuildStrmRelativePath(
        ManifestItem video,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        return (string)BuildRelativePathMethod.Invoke(null, [video, allItems])!;
    }

    private static bool InvokeIsNzbdavManagedStrmContent(string content, string baseUrl)
    {
        return (bool)IsNzbdavManagedStrmContentMethod.Invoke(null, [content, baseUrl])!;
    }

    private static string InvokeGetQuarantineRelativePath(string relativePath, string runId)
    {
        return (string)GetQuarantineRelativePathMethod.Invoke(null, [relativePath, runId])!;
    }

    private static string[] InvokeBuildExpectedStrmRelativePaths(
        ManifestItem[] items,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        return (string[])BuildExpectedStrmRelativePathsMethod.Invoke(null, [items, allItems])!;
    }

    private static void InvokeReconcileStaleFiles(
        NzbdavLibrarySyncTask task,
        PluginConfiguration config,
        string[] expectedRelativePaths,
        string runId)
    {
        ReconcileStaleFilesMethod.Invoke(task, [config, expectedRelativePaths, runId]);
    }

    private static void WriteFile(string libraryPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(libraryPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
