namespace NzbWebDAV.Database.Models;

/// <summary>
/// Durable phase-A completion intent.  Session fields contain authenticated
/// ciphertext, never a bearer token in plaintext.  There is at most one row;
/// the operation id is the CAS token for phase C.
/// </summary>
public sealed class SetupCompletionOperation
{
    public const int SingletonId = 1;

    public int Id { get; set; }
    public string OperationId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public bool RevocationPending { get; set; }

    public string? ActiveSessionCiphertext { get; set; }
    public string? RevocationSessionCiphertext { get; set; }
    public string? CandidateSessionCiphertext { get; set; }
    public string? CandidateOperationCiphertext { get; set; }
    public string? EmergencySessionCiphertext { get; set; }
    public string? EmergencyOperationCiphertext { get; set; }
}
