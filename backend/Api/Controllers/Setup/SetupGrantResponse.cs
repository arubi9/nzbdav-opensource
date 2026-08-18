using NzbWebDAV.Api.Controllers;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Api.Controllers.Setup;

public sealed class SetupGrantResponse : BaseApiResponse
{
    public string Grant { get; init; } = string.Empty;
    public DateTime ExpiresAtUtc { get; init; }
    public bool RevocationPending { get; init; }
    public SetupGrantScope Scope { get; init; }
    public string? Warning { get; init; }
}
