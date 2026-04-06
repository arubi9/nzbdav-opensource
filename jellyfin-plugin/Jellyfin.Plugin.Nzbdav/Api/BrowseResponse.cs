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
