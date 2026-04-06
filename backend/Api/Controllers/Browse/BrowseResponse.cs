using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.Browse;

public class BrowseResponse
{
    public required string Path { get; init; }
    public required BrowseItem[] Items { get; init; }
}

public class BrowseItem
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Type { get; init; }
    public long? FileSize { get; init; }
    public required DateTime CreatedAt { get; init; }

    public static BrowseItem FromDavItem(DavItem item) => new()
    {
        Id = item.Id,
        Name = item.Name,
        Type = item.Type switch
        {
            DavItem.ItemType.Directory => "directory",
            DavItem.ItemType.NzbFile => "nzb_file",
            DavItem.ItemType.RarFile => "rar_file",
            DavItem.ItemType.MultipartFile => "multipart_file",
            _ => "directory"
        },
        FileSize = item.FileSize,
        CreatedAt = item.CreatedAt
    };
}
