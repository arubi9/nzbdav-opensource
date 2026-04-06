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

    public string? StreamToken { get; set; }
}
