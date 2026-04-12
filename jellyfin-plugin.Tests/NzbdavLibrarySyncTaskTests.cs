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

    private static string InvokeBuildStrmRelativePath(
        ManifestItem video,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        return (string)BuildRelativePathMethod.Invoke(null, [video, allItems])!;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
