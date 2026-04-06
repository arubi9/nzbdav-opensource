namespace NzbWebDAV.Streams;

public enum StreamClassification { Unknown, Probe, Playback }
public enum RequestHint { Unknown, SuspectedProbe, SuspectedPlayback }

/// <summary>
/// Per-stream classifier that observes read/seek patterns to determine
/// whether the consumer is an FFmpeg probe or a video player.
/// Immutable after commit — once Probe or Playback, never changes.
/// </summary>
public struct StreamClassifier
{
    private const int SequentialReadsForPlayback = 5;
    private const int MaxReadsBeforeFallback = 10;
    private const double LargeSeekThreshold = 0.5; // 50% of file length

    private readonly long _fileSize;
    private readonly RequestHint _hint;
    private int _readCount;
    private long _lastReadOffset;
    private StreamClassification _classification;

    public StreamClassifier(RequestHint hint, long fileSize)
    {
        _hint = hint;
        _fileSize = fileSize;
        _readCount = 0;
        _lastReadOffset = 0;
        _classification = StreamClassification.Unknown;
    }

    public readonly StreamClassification Classification => _classification;

    public void ObserveRead(long offset, int length)
    {
        if (_classification != StreamClassification.Unknown) return;

        _readCount++;
        _lastReadOffset = offset + length;

        // Fallback: 10 reads with no large seek → Playback
        if (_readCount >= MaxReadsBeforeFallback)
            _classification = StreamClassification.Playback;

        // 5 sequential reads → Playback
        else if (_readCount >= SequentialReadsForPlayback)
            _classification = StreamClassification.Playback;
    }

    public void ObserveSeek(long fromOffset, long toOffset)
    {
        if (_classification != StreamClassification.Unknown) return;
        if (_fileSize <= 0) return;

        var seekDistance = Math.Abs(toOffset - fromOffset);
        var seekRatio = (double)seekDistance / _fileSize;

        if (seekRatio > LargeSeekThreshold)
        {
            // Large seek detected
            if (_hint == RequestHint.SuspectedProbe)
            {
                // SuspectedProbe + one large seek → immediate Probe
                _classification = StreamClassification.Probe;
            }
            else if (_readCount <= SequentialReadsForPlayback)
            {
                // Large seek within first 5 reads → Probe
                _classification = StreamClassification.Probe;
            }
        }
    }
}
