namespace NzbWebDAV.Streams;

/// <summary>
/// O(1) arithmetic helper for mapping a byte offset to a segment index
/// when all yEnc segments share a uniform part size (all except the last).
/// </summary>
internal static class YencLayout
{
    /// <summary>
    /// Returns the zero-based segment index that contains <paramref name="offset"/>.
    /// </summary>
    /// <param name="offset">Byte offset within the file (0-based).</param>
    /// <param name="partSize">Uniform part size for segments 0 .. N-2.</param>
    /// <param name="segmentCount">Total number of segments (N).</param>
    /// <param name="lastPartSize">Part size of the final segment (index N-1).</param>
    internal static int SegmentForByteOffset(
        long offset,
        long partSize,
        int segmentCount,
        long lastPartSize)
    {
        if (segmentCount <= 0) return 0;
        if (segmentCount == 1) return 0;

        var regularBytes = (long)(segmentCount - 1) * partSize;
        if (offset < regularBytes)
            return (int)(offset / partSize);

        return segmentCount - 1;
    }
}
