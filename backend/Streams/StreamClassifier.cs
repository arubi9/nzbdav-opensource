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
    // Tolerance for "sequential" — reads that land within 4 MB past the end
    // of the previous read still count as sequential. Players with buffered
    // HTTP clients may skip ahead by a few chunks; we want to consider that
    // sequential. Probe seeks are typically much larger (half the file).
    private const long SequentialToleranceBytes = 4 * 1_048_576;

    private readonly long _fileSize;
    private readonly RequestHint _hint;
    private int _readCount;
    private int _sequentialReadCount;
    private long _lastReadEndOffset;
    private bool _hasObservedRead;
    private StreamClassification _classification;

    public StreamClassifier(RequestHint hint, long fileSize)
    {
        _hint = hint;
        _fileSize = fileSize;
        _readCount = 0;
        _sequentialReadCount = 0;
        _lastReadEndOffset = 0;
        _hasObservedRead = false;
        _classification = StreamClassification.Unknown;
    }

    public readonly StreamClassification Classification => _classification;

    public void ObserveRead(long offset, int length)
    {
        if (_classification != StreamClassification.Unknown) return;

        _readCount++;

        // Check sequentiality: was this read at (or just past) the previous read's end?
        if (!_hasObservedRead)
        {
            // First read — counts as sequential by convention.
            _sequentialReadCount = 1;
        }
        else
        {
            var gap = offset - _lastReadEndOffset;
            if (gap >= 0 && gap <= SequentialToleranceBytes)
                _sequentialReadCount++;
            else
                _sequentialReadCount = 1; // Reset — this is a new sequential run.
        }

        _hasObservedRead = true;
        _lastReadEndOffset = offset + length;

        // 5 SEQUENTIAL reads → Playback
        if (_sequentialReadCount >= SequentialReadsForPlayback)
        {
            _classification = StreamClassification.Playback;
            return;
        }

        // Fallback: 10 total reads with no large seek → Playback
        // (Non-sequential but high-volume readers are still likely players.)
        if (_readCount >= MaxReadsBeforeFallback)
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
