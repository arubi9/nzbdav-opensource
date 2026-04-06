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
}
