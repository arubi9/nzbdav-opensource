using System.Collections.Generic;

namespace NzbWebDAV.Services.NntpLeasing;

public static class NntpLeasePolicy
{
    private const int StreamingReservePercent = 70;
    private const int IngestReservePercent = 30;

    public sealed record LeaseHeartbeat(
        string NodeId,
        NntpLeaseNodeRole NodeRole,
        bool HasDemand,
        int DesiredSlots);

    public sealed record LeaseGrant(
        string NodeId,
        NntpLeaseNodeRole NodeRole,
        int GrantedSlots,
        long Epoch);

    public static IReadOnlyList<LeaseGrant> Compute(
        int totalSlots,
        IReadOnlyList<LeaseHeartbeat> heartbeats,
        long currentEpoch)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(totalSlots);
        ArgumentNullException.ThrowIfNull(heartbeats);

        var grants = new LeaseGrant[heartbeats.Count];
        for (var i = 0; i < heartbeats.Count; i++)
        {
            var heartbeat = heartbeats[i];
            grants[i] = new LeaseGrant(heartbeat.NodeId, heartbeat.NodeRole, 0, currentEpoch);
        }

        var streamingHeartbeats = GetDemandingHeartbeats(heartbeats, NntpLeaseNodeRole.Streaming);
        var ingestHeartbeats = GetDemandingHeartbeats(heartbeats, NntpLeaseNodeRole.Ingest);

        if (streamingHeartbeats.Count > 0 && ingestHeartbeats.Count > 0)
        {
            AllocateRoleBudget(grants, streamingHeartbeats, totalSlots * StreamingReservePercent / 100, currentEpoch);
            AllocateRoleBudget(grants, ingestHeartbeats, totalSlots - totalSlots * StreamingReservePercent / 100, currentEpoch);
            return grants;
        }

        if (streamingHeartbeats.Count > 0)
        {
            AllocateRoleBudget(grants, streamingHeartbeats, totalSlots, currentEpoch);
            return grants;
        }

        if (ingestHeartbeats.Count > 0)
        {
            AllocateRoleBudget(grants, ingestHeartbeats, totalSlots, currentEpoch);
        }

        return grants;
    }

    private static List<(int Index, LeaseHeartbeat Heartbeat)> GetDemandingHeartbeats(
        IReadOnlyList<LeaseHeartbeat> heartbeats,
        NntpLeaseNodeRole role)
    {
        return heartbeats
            .Select((heartbeat, index) => (Index: index, Heartbeat: heartbeat))
            .Where(item => item.Heartbeat.HasDemand && item.Heartbeat.NodeRole == role)
            .OrderBy(item => item.Heartbeat.NodeId, StringComparer.Ordinal)
            .ThenBy(item => item.Index)
            .ToList();
    }

    private static void AllocateRoleBudget(
        LeaseGrant[] grants,
        IReadOnlyList<(int Index, LeaseHeartbeat Heartbeat)> roleHeartbeats,
        int budget,
        long currentEpoch)
    {
        if (roleHeartbeats.Count == 0)
        {
            return;
        }

        var share = budget / roleHeartbeats.Count;
        var remainder = budget % roleHeartbeats.Count;

        for (var i = 0; i < roleHeartbeats.Count; i++)
        {
            var item = roleHeartbeats[i];
            var grantedSlots = share + (i < remainder ? 1 : 0);
            grants[item.Index] = new LeaseGrant(
                item.Heartbeat.NodeId,
                item.Heartbeat.NodeRole,
                grantedSlots,
                currentEpoch);
        }
    }
}
