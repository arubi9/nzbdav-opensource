namespace NzbWebDAV.Database.Models;

public sealed class SetupGrant
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public string GrantedTokenHash { get; set; } = string.Empty;
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? IssuedByUsername { get; set; }
    /// <summary>Normal or narrow repair purpose; never inferred from completion.</summary>
    public string Purpose { get; set; } = "setup";
    /// <summary>Authenticated Jellyfin repair session, encrypted at rest.</summary>
    public string? RepairSessionCiphertext { get; set; }
    public string? RepairSessionOperationId { get; set; }
}
