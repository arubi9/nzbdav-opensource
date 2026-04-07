using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class StreamClassifierTests
{
    private const long FileSize = 100_000_000; // 100MB
    private const int SegmentSize = 750_000;   // 750KB typical segment

    [Fact]
    public void FiveSequentialReads_CommitsToPlayback()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        for (var i = 0; i < 5; i++)
            classifier.ObserveRead(i * SegmentSize, SegmentSize);

        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }

    [Fact]
    public void ReadThenLargeSeek_CommitsToProbe()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, 65536); // Read header
        classifier.ObserveSeek(65536, FileSize - 65536); // Seek to near end (>50% jump)

        Assert.Equal(StreamClassification.Probe, classifier.Classification);
    }

    [Fact]
    public void SuspectedProbeHint_OneSeek_ImmediateProbe()
    {
        var classifier = new StreamClassifier(RequestHint.SuspectedProbe, FileSize);

        classifier.ObserveSeek(0, FileSize - 1000); // Large seek

        Assert.Equal(StreamClassification.Probe, classifier.Classification);
    }

    [Fact]
    public void TenReadsNoSeek_FallbackToPlayback()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        for (var i = 0; i < 10; i++)
            classifier.ObserveRead(i * SegmentSize, SegmentSize);

        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }

    [Fact]
    public void ClassificationIsImmutableAfterCommit()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, 65536);
        classifier.ObserveSeek(65536, FileSize - 1000); // Commits to Probe
        Assert.Equal(StreamClassification.Probe, classifier.Classification);

        // Further reads don't change it
        for (var i = 0; i < 100; i++)
            classifier.ObserveRead(i * SegmentSize, SegmentSize);

        Assert.Equal(StreamClassification.Probe, classifier.Classification);
    }

    [Fact]
    public void SmallSeek_DoesNotTriggerProbe()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, SegmentSize);
        classifier.ObserveSeek(SegmentSize, SegmentSize * 3); // Small jump (2 segments, <50%)
        classifier.ObserveRead(SegmentSize * 3, SegmentSize);
        classifier.ObserveRead(SegmentSize * 4, SegmentSize);
        classifier.ObserveRead(SegmentSize * 5, SegmentSize);
        classifier.ObserveRead(SegmentSize * 6, SegmentSize);

        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }

    [Fact]
    public void UnknownBeforeFiveReads()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        for (var i = 0; i < 4; i++)
            classifier.ObserveRead(i * SegmentSize, SegmentSize);

        Assert.Equal(StreamClassification.Unknown, classifier.Classification);
    }

    [Fact]
    public void ZeroFileSize_NeverCommitsToProbe()
    {
        var classifier = new StreamClassifier(RequestHint.Unknown, 0);

        classifier.ObserveSeek(0, 1000000); // Would be >50% but fileSize is 0

        Assert.Equal(StreamClassification.Unknown, classifier.Classification);
    }

    // --- I7: sequentiality tracking ---

    [Fact]
    public void FiveNonSequentialReads_DoesNotCommitViaSequentialPath()
    {
        // Reads at widely-spaced offsets (well beyond the 4MB tolerance) — each
        // one resets the sequential counter to 1. After 5 reads we should NOT
        // have committed via the sequential path.
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, 1000);                  // run=1
        classifier.ObserveRead(10_000_000, 1000);         // gap=10M → run=1
        classifier.ObserveRead(20_000_000, 1000);         // gap=10M → run=1
        classifier.ObserveRead(30_000_000, 1000);         // gap=10M → run=1
        classifier.ObserveRead(40_000_000, 1000);         // gap=10M → run=1

        // Still Unknown at 5 reads — fallback only kicks in at 10.
        Assert.Equal(StreamClassification.Unknown, classifier.Classification);
    }

    [Fact]
    public void NonSequentialReadAfterFourSequentialReads_ResetsCounter()
    {
        // The 5th read jumps backward — that breaks the sequential run, so we
        // must do 5 more sequential reads before committing.
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, SegmentSize);           // seq=1
        classifier.ObserveRead(SegmentSize, SegmentSize); // seq=2
        classifier.ObserveRead(SegmentSize * 2, SegmentSize); // seq=3
        classifier.ObserveRead(SegmentSize * 3, SegmentSize); // seq=4
        classifier.ObserveRead(90_000_000, SegmentSize);  // huge jump → seq=1

        // Not committed yet — run reset.
        Assert.Equal(StreamClassification.Unknown, classifier.Classification);

        classifier.ObserveRead(90_000_000 + SegmentSize, SegmentSize); // seq=2
        classifier.ObserveRead(90_000_000 + SegmentSize * 2, SegmentSize); // seq=3
        classifier.ObserveRead(90_000_000 + SegmentSize * 3, SegmentSize); // seq=4

        // Total reads = 8 now, still under the 10-read fallback and not enough
        // sequential reads (4) to commit.
        Assert.Equal(StreamClassification.Unknown, classifier.Classification);

        classifier.ObserveRead(90_000_000 + SegmentSize * 4, SegmentSize); // seq=5

        // Now the sequential path fires.
        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }

    [Fact]
    public void SmallForwardSkip_StaysWithinSequentialTolerance()
    {
        // A 2MB forward skip is within the 4MB tolerance — subsequent reads
        // should still count as part of the sequential run.
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        classifier.ObserveRead(0, 1_000_000);             // end=1M, seq=1
        classifier.ObserveRead(3_000_000, 1_000_000);     // gap=2M → seq=2
        classifier.ObserveRead(4_000_000, 1_000_000);     // seq=3
        classifier.ObserveRead(5_000_000, 1_000_000);     // seq=4
        classifier.ObserveRead(6_000_000, 1_000_000);     // seq=5 → Playback

        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }

    [Fact]
    public void TenNonSequentialReads_StillFallsBackToPlayback()
    {
        // The fallback path should fire for high-volume readers even when
        // every read is non-sequential — we assume they're a player that's
        // seeking aggressively rather than a probe (which gives up sooner).
        var classifier = new StreamClassifier(RequestHint.Unknown, FileSize);

        for (var i = 0; i < 10; i++)
            classifier.ObserveRead(i * 10_000_000L, 1000);

        Assert.Equal(StreamClassification.Playback, classifier.Classification);
    }
}
