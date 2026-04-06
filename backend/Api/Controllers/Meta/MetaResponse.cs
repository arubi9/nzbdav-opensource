namespace NzbWebDAV.Api.Controllers.Meta;

public class MetaResponse
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Type { get; init; }
    public long? FileSize { get; init; }
    public required DateTime CreatedAt { get; init; }
    public Guid? ParentId { get; init; }
    public string? ContentType { get; init; }
    public string? StreamToken { get; init; }
}
