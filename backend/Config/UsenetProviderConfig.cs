using System.Text.Json.Serialization;
using NzbWebDAV.Models;

namespace NzbWebDAV.Config;

public class UsenetProviderConfig
{
    public List<ConnectionDetails> Providers { get; set; } = [];

    public int TotalPooledConnections => Math.Max(1, Providers
        .Where(x => x.Type == ProviderType.Pooled)
        .Select(x => x.MaxConnections)
        .Sum());

    public class ConnectionDetails
    {
        // Assigned by the dedicated admin-settings flow. Legacy setup rows may
        // not have an identity; the settings controller assigns one before the
        // first edit without exposing credentials.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Id { get; set; }

        public required ProviderType Type { get; set; }
        public required string Host { get; set; }
        public required int Port { get; set; }
        public required bool UseSsl { get; set; }
        public required string User { get; set; }
        public required string Pass { get; set; }
        public required int MaxConnections { get; set; }
    }
}