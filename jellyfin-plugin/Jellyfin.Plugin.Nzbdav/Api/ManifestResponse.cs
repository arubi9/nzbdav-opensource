namespace Jellyfin.Plugin.Nzbdav.Api;

public class ManifestResponse
{
    public int ItemCount { get; set; }
    public ManifestItem[] Items { get; set; } = [];
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
