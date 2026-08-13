using NzbWebDAV.Models;

namespace NzbWebDAV.Api.Controllers.UsenetSettings;

public sealed class UsenetSettingsResponse
{
    public required IReadOnlyList<UsenetProviderResponse> Providers { get; init; }
    public required string Revision { get; init; }
}

public sealed class UsenetProviderResponse
{
    public required string Id { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public required bool Ssl { get; init; }
    public required string User { get; init; }
    public required int Max { get; init; }
    public required ProviderType Type { get; init; }
    public required bool HasPassword { get; init; }
}

public sealed class UsenetSettingsRequest
{
    public required string Revision { get; init; }
    public required List<UsenetProviderInput> Providers { get; init; }
}

public sealed class UsenetProviderInput
{
    public string? Id { get; init; }
    public string? Host { get; init; }
    public int? Port { get; init; }
    public bool? Ssl { get; init; }
    public string? User { get; init; }
    public int? Max { get; init; }
    public ProviderType? Type { get; init; }
    public string? Password { get; init; }
}
