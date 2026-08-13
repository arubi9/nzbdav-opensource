namespace NzbWebDAV.Database.Models;

/// <summary>Durable cross-process fence for one setup orchestration run.</summary>
public sealed class SetupRunLease
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public string OwnerId { get; set; } = string.Empty;
    public string GrantHash { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public long Generation { get; set; }
    public DateTime LeaseUntilUtc { get; set; }
}
