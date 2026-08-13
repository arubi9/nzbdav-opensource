namespace NzbWebDAV.Database.Models;

/// <summary>
/// The single database-wide serialization point for setup mutations.  The
/// row is deliberately boring: a row lock is the fence and Epoch is a
/// portable SQLite write/no-op fallback.
/// </summary>
public sealed class SetupMutationFence
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public long Epoch { get; set; }
    /// <summary>
    /// Durable pre-auth/setup-candidate operation intent. It is reserved before
    /// Jellyfin authentication and transferred/cleared only by the matching
    /// encrypted candidate or repair handoff.
    /// </summary>
    public string? ReservedCandidateOperationId { get; set; }
}
