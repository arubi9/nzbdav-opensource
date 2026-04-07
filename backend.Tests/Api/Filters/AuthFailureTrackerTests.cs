using NzbWebDAV.Api.Filters;

namespace NzbWebDAV.Tests.Api.Filters;

public class AuthFailureTrackerTests
{
    [Fact]
    public void IsBlocked_ReturnsFalseForUnknownIp()
    {
        var tracker = new AuthFailureTracker();
        Assert.False(tracker.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void RecordFailure_BlocksAfterTenFailures()
    {
        var tracker = new AuthFailureTracker();
        for (var i = 0; i < 9; i++) tracker.RecordFailure("1.2.3.4");
        Assert.False(tracker.IsBlocked("1.2.3.4"));

        tracker.RecordFailure("1.2.3.4");
        Assert.True(tracker.IsBlocked("1.2.3.4"));
    }

    [Fact]
    public void RecordFailure_DistinctIpsTrackedSeparately()
    {
        var tracker = new AuthFailureTracker();
        for (var i = 0; i < 10; i++) tracker.RecordFailure("1.2.3.4");

        Assert.True(tracker.IsBlocked("1.2.3.4"));
        Assert.False(tracker.IsBlocked("5.6.7.8"));
    }

    [Fact]
    public void Sweep_ReturnsZeroWhenNothingExpired()
    {
        var tracker = new AuthFailureTracker();
        tracker.RecordFailure("1.2.3.4");
        tracker.RecordFailure("5.6.7.8");

        Assert.Equal(0, tracker.Sweep());
        Assert.Equal(2, tracker.TrackedIpCount);
    }

    [Fact]
    public void RecordFailure_DoesNotGrowDictionaryBeyondCap()
    {
        // Spray 100k+1 distinct IPs at the tracker. The cap is 100k. After
        // RecordFailure returns, the dictionary must NEVER exceed 100k entries.
        var tracker = new AuthFailureTracker();
        for (var i = 0; i < 100_001; i++)
            tracker.RecordFailure($"10.0.{i / 256}.{i % 256}");

        Assert.True(tracker.TrackedIpCount <= 100_000,
            $"Tracked IP count {tracker.TrackedIpCount} exceeded cap of 100,000");
    }

    [Fact]
    public void RecordFailure_AtCap_StillBumpsExistingIp()
    {
        // Even when the dictionary is full of OTHER IPs, an already-tracked
        // IP must still be able to bump its counter (otherwise the cap
        // becomes a denial-of-defense — the attacker fills the dictionary
        // and a legitimate brute-forcer escapes blocking).
        var tracker = new AuthFailureTracker();

        // Pre-track a victim IP with 5 failures.
        for (var i = 0; i < 5; i++) tracker.RecordFailure("9.9.9.9");

        // Saturate the dictionary with junk.
        for (var i = 0; i < 100_001; i++)
            tracker.RecordFailure($"10.0.{i / 256}.{i % 256}");

        // Bump the victim IP past the threshold.
        for (var i = 0; i < 5; i++) tracker.RecordFailure("9.9.9.9");

        Assert.True(tracker.IsBlocked("9.9.9.9"));
    }
}
