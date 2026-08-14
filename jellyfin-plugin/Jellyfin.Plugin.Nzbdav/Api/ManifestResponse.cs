namespace Jellyfin.Plugin.Nzbdav.Api;

public class ManifestResponse
{
    public int ItemCount { get; set; }
    public ManifestItem[] Items { get; set; } = [];

    /// <summary>Cursor for the next page. Null or empty means this was the last page.</summary>
    public string? NextCursor { get; set; }

    /// <summary>
    /// Identifies the tree this page was read from. Absent from servers that predate
    /// paging, which answer any request with the whole manifest in one response.
    /// </summary>
    public string? Version { get; set; }
}

public class ManifestItem
{
    public Guid Id { get; set; }
    public Guid? ParentId { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
    public long? FileSize { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool HasProbeData { get; set; }
}
