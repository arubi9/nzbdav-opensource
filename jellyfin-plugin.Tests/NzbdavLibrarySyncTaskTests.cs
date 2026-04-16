using System.Reflection;
using Jellyfin.Plugin.Nzbdav;
using Jellyfin.Plugin.Nzbdav.Api;
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
            Normalize(Path.Combine(".quarantine", "20260416-120000", "shows", "Series", "Episode.strm")),
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

        var result = InvokeBuildExpectedStrmRelativePaths([parent, video, directory]);

        Assert.Equal(
            [Normalize(Path.Combine("movies", "Movie", "Movie.strm"))],
            result.Select(Normalize).ToArray());
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

    private static string[] InvokeBuildExpectedStrmRelativePaths(ManifestItem[] items)
    {
        return (string[])BuildExpectedStrmRelativePathsMethod.Invoke(null, [items])!;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
