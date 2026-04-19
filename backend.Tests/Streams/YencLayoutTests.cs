using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

public class YencLayoutTests
{
    [Fact]
    public void SegmentForByteOffset_FirstSegmentStart()
    {
        Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 768000, 10, 500000));
    }

    [Fact]
    public void SegmentForByteOffset_LastByteOfFirstSegment()
    {
        Assert.Equal(0, YencLayout.SegmentForByteOffset(767999, 768000, 10, 500000));
    }

    [Fact]
    public void SegmentForByteOffset_FirstByteOfSecondSegment()
    {
        Assert.Equal(1, YencLayout.SegmentForByteOffset(768000, 768000, 10, 500000));
    }

    [Fact]
    public void SegmentForByteOffset_MiddleOfFile()
    {
        // 10 segments, segment 4 covers bytes 3072000..3839999
        Assert.Equal(4, YencLayout.SegmentForByteOffset(3500000, 768000, 10, 500000));
    }

    [Fact]
    public void SegmentForByteOffset_LastSegmentAnywhere()
    {
        // N=10, segment 9 is last, covers bytes 9*768000=6912000..end
        Assert.Equal(9, YencLayout.SegmentForByteOffset(7000000, 768000, 10, 500000));
        Assert.Equal(9, YencLayout.SegmentForByteOffset(7411999, 768000, 10, 500000));
    }

    [Fact]
    public void SegmentForByteOffset_SingleSegmentFile()
    {
        Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 1_000_000, 1, 1_000_000));
        Assert.Equal(0, YencLayout.SegmentForByteOffset(999_999, 1_000_000, 1, 1_000_000));
    }

    [Fact]
    public void SegmentForByteOffset_EmptyOrInvalid()
    {
        Assert.Equal(0, YencLayout.SegmentForByteOffset(0, 0, 0, 0));
    }
}
