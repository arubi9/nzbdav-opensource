using NzbWebDAV.Utils;

namespace NzbWebDAV.Clients.Usenet.Caching;

public static class SegmentCategoryClassifier
{
    private static readonly HashSet<string> SmallFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".txt", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp",
        ".srt", ".sub", ".idx", ".ssa", ".ass", ".sfv", ".tiff"
    };

    public static SegmentCategory Classify(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        if (SmallFileExtensions.Contains(ext)) return SegmentCategory.SmallFile;
        if (FilenameUtil.IsVideoFile(fileName)) return SegmentCategory.VideoSegment;
        return SegmentCategory.Unknown;
    }
}
