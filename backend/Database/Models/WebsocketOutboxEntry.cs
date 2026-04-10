namespace NzbWebDAV.Database.Models;

public sealed class WebsocketOutboxEntry
{
    public long Seq { get; set; }
    public required string Topic { get; set; }
    public required string Payload { get; set; }
    public DateTime CreatedAt { get; set; }
}
