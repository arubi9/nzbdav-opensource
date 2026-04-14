namespace NzbWebDAV.Services.NntpLeasing;

public sealed class NntpLeaseState
{
    private readonly Lock _lock = new();
    private readonly Dictionary<int, ProviderLeaseState> _leases = [];

    public void Apply(
        int providerIndex,
        int grantedSlots,
        long epoch,
        DateTime leaseUntil,
        int? reservedSlots = null,
        int borrowedSlots = 0)
    {
        lock (_lock)
        {
            _leases[providerIndex] = new ProviderLeaseState(
                grantedSlots,
                reservedSlots ?? grantedSlots,
                borrowedSlots,
                epoch,
                leaseUntil);
        }
    }

    public int GetProviderGrant(int providerIndex)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(providerIndex, out var lease)
                ? lease.GrantedSlots
                : 0;
        }
    }

    public long GetProviderEpoch(int providerIndex)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(providerIndex, out var lease)
                ? lease.Epoch
                : 0;
        }
    }

    public DateTime GetProviderLeaseUntil(int providerIndex)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(providerIndex, out var lease)
                ? lease.LeaseUntil
                : DateTime.MinValue;
        }
    }

    public bool IsLeaseFresh(int providerIndex, DateTime utcNow)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(providerIndex, out var lease)
                   && lease.LeaseUntil > utcNow;
        }
    }

    public int GetTotalGrantedSlots()
    {
        lock (_lock)
        {
            return _leases.Values.Sum(x => x.GrantedSlots);
        }
    }

    public int GetFreshProviderGrant(int providerIndex, DateTime utcNow)
    {
        lock (_lock)
        {
            return _leases.TryGetValue(providerIndex, out var lease) && lease.LeaseUntil > utcNow
                ? lease.GrantedSlots
                : 0;
        }
    }

    public int GetFreshTotalGrantedSlots(IEnumerable<int> providerIndexes, DateTime utcNow)
    {
        lock (_lock)
        {
            return providerIndexes.Sum(providerIndex =>
                _leases.TryGetValue(providerIndex, out var lease) && lease.LeaseUntil > utcNow
                    ? lease.GrantedSlots
                    : 0);
        }
    }

    public ProviderLeaseObservation[] GetProviderLeaseObservations(DateTime utcNow)
    {
        lock (_lock)
        {
            return _leases
                .OrderBy(x => x.Key)
                .Select(x =>
                {
                    var lease = x.Value;
                    var secondsUntilExpiry = (int)Math.Ceiling((lease.LeaseUntil - utcNow).TotalSeconds);
                    return new ProviderLeaseObservation(
                        x.Key,
                        lease.GrantedSlots,
                        lease.ReservedSlots,
                        lease.BorrowedSlots,
                        lease.Epoch,
                        lease.LeaseUntil,
                        lease.LeaseUntil > utcNow,
                        Math.Max(0, secondsUntilExpiry));
                })
                .ToArray();
        }
    }

    public readonly record struct ProviderLeaseObservation(
        int ProviderIndex,
        int GrantedSlots,
        int ReservedSlots,
        int BorrowedSlots,
        long Epoch,
        DateTime LeaseUntil,
        bool IsFresh,
        int SecondsUntilExpiry);

    private readonly record struct ProviderLeaseState(
        int GrantedSlots,
        int ReservedSlots,
        int BorrowedSlots,
        long Epoch,
        DateTime LeaseUntil);
}
