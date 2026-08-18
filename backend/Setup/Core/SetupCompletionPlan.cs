namespace NzbWebDAV.Setup.Core;

/// <summary>Bearer identities captured by completion phase A.</summary>
internal sealed record SetupCompletionPlan(
    string OperationId,
    string? ActiveSession,
    string? RevocationSession,
    bool RevocationPending,
    string? CandidateSession,
    string? CandidateOperation,
    string? EmergencySession,
    string? EmergencyOperation)
{
    internal IEnumerable<string?> Sessions =>
        [ActiveSession, RevocationSession, CandidateSession, EmergencySession];
}
