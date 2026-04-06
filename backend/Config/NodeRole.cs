using NzbWebDAV.Utils;

namespace NzbWebDAV.Config;

public enum NodeRole
{
    Combined,
    Streaming,
    Ingest
}

public static class NodeRoleConfig
{
    public static NodeRole Current { get; } = ParseRole(
        EnvironmentUtil.GetEnvironmentVariable("NZBDAV_ROLE"));

    public static bool RunsStreaming => Current is NodeRole.Combined or NodeRole.Streaming;

    public static bool RunsIngest => Current is NodeRole.Combined or NodeRole.Ingest;

    private static NodeRole ParseRole(string? value)
    {
        if (string.IsNullOrEmpty(value)) return NodeRole.Combined;
        return Enum.TryParse<NodeRole>(value, ignoreCase: true, out var role)
            ? role
            : NodeRole.Combined;
    }
}
