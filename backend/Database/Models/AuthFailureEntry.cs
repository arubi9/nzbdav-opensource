namespace NzbWebDAV.Database.Models;

public sealed class AuthFailureEntry
{
    public required string IpAddress { get; set; }
    public int FailureCount { get; set; }
    public DateTime WindowStart { get; set; }
}
