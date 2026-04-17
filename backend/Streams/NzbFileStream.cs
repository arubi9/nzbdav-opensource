using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    StreamingBufferSettings streamingBufferSettings,
    Action<int>? onSegmentIndexChanged = null,
    RequestHint requestHint = RequestHint.Unknown
) : FastReadOnlyStream
{
    private readonly LongRange?[] _segmentRanges = new LongRange?[fileSegmentIds.Length];
    private StreamClassifier _classifier = new(requestHint, fileSize);
    private long _position;
    private bool _disposed;
    private bool _seekPending;
    private Stream? _innerStream;
    private Stream? _seekOverlayStream;

    // Classifier callbacks — populated by BindClassifierCallbacks. Fired exactly
    // once when the classifier commits to Playback. These are owned by the stream
    // (not the store file) to sidestep the closure-timing and AsyncLocal-upward-
    // propagation problems that plagued the previous implementation.
    private SegmentFetchContext? _segmentContext;
    private Action? _onClassifiedAsPlayback;
    private bool _playbackCallbacksFired;

    public StreamClassification Classification => _classifier.Classification;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    /// <summary>
    /// Wire the classifier to the stream's ambient cache-tier context and a
    /// one-shot playback callback. Both are invoked exactly once, at the moment
    /// the classifier commits to Playback.
    ///
    /// Call this BEFORE the first ReadAsync. The stream captures the refs and
    /// fires them inline when classification commits, so effects land in the
    /// same async frame as the read that triggered them.
    /// </summary>
    public void BindClassifierCallbacks(
        SegmentFetchContext? segmentContext,
        Action? onClassifiedAsPlayback)
    {
        _segmentContext = segmentContext;
        _onClassifiedAsPlayback = onClassifiedAsPlayback;
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_position >= fileSize) return 0;
        if (_seekPending)
        {
            _seekPending = false;
            _seekOverlayStream = await TryCreateCachedSeekOverlayAsync(_position, cancellationToken).ConfigureAwait(false);
            if (_seekOverlayStream == null)
            {
                _innerStream?.Dispose();
                _innerStream = null;
            }
        }

        if (_seekOverlayStream != null)
        {
            var overlayReadPosition = _position;
            var overlayRead = await _seekOverlayStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (overlayRead > 0)
            {
                _position += overlayRead;
                // I8 fix: observe cache-served reads too — otherwise the
                // classifier never fires for content that hits the seek overlay.
                _classifier.ObserveRead(overlayReadPosition, overlayRead);
                MaybeFirePlaybackCallbacks();
                return overlayRead;
            }

            await _seekOverlayStream.DisposeAsync().ConfigureAwait(false);
            _seekOverlayStream = null;
            _innerStream?.Dispose();
            _innerStream = null;
        }

        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var readPosition = _position;
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        if (read > 0)
        {
            _classifier.ObserveRead(readPosition, read);
            MaybeFirePlaybackCallbacks();
        }
        _position += read;
        return read;
    }

    private void MaybeFirePlaybackCallbacks()
    {
        if (_playbackCallbacksFired) return;
        if (_classifier.Classification != StreamClassification.Playback) return;

        _playbackCallbacksFired = true;

        // Upgrade cache tier. The SegmentFetchContext instance flows via AsyncLocal,
        // so mutating its Category here propagates to all callers holding the same
        // reference — no need to re-set AsyncLocal.Value (which would not flow
        // upward out of the async frame we're currently on).
        _segmentContext?.UpgradeCategory(SegmentCategory.VideoSegment);

        // Fire user callback (e.g. start read-ahead warming). Swallow exceptions
        // so a broken callback doesn't abort the stream.
        try
        {
            _onClassifiedAsPlayback?.Invoke();
        }
        catch
        {
            // Intentionally ignored — callback failures must not break playback.
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _classifier.ObserveSeek(_position, absoluteOffset);
        _position = absoluteOffset;
        _seekPending = true;
        return _position;
    }

    private Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            guess => GetSegmentRangeAsync(guess, ct),
            ct
        );
    }

    private ValueTask<LongRange> GetSegmentRangeAsync(int index, CancellationToken ct)
    {
        if (_segmentRanges[index] is { } cachedRange)
            return ValueTask.FromResult(cachedRange);

        return FetchSegmentRangeAsync(index, ct);
    }

    private async ValueTask<LongRange> FetchSegmentRangeAsync(int index, CancellationToken ct)
    {
        var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[index], ct).ConfigureAwait(false);
        Serilog.Log.Warning(
            "FetchSegmentRange: idx={Idx} segId={SegId} PartOffset={Off} PartSize={Size} FileSize={FS} PartNumber={PN}/{TP} FileName={FN}",
            index, fileSegmentIds[index], header.PartOffset, header.PartSize, header.FileSize,
            header.PartNumber, header.TotalParts, header.FileName);
        var range = LongRange.FromStartAndSize(header.PartOffset, header.PartSize);
        _segmentRanges[index] = range;
        return range;
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0)
        {
            onSegmentIndexChanged?.Invoke(0);
            return GetMultiSegmentStream(0, cancellationToken);
        }

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        onSegmentIndexChanged?.Invoke(foundSegment.FoundIndex);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, cancellationToken);
        await stream.DiscardBytesAsync(rangeStart - foundSegment.FoundByteRange.StartInclusive, cancellationToken)
            .ConfigureAwait(false);
        return stream;
    }

    private async Task<Stream?> TryCreateCachedSeekOverlayAsync(long rangeStart, CancellationToken cancellationToken)
    {
        if (usenetClient is not ICachedSegmentReader cachedSegmentReader)
            return null;

        if (rangeStart == 0)
        {
            if (!cachedSegmentReader.HasCachedBody(fileSegmentIds[0]))
                return null;

            onSegmentIndexChanged?.Invoke(0);
            return new CachedSegmentStream(fileSegmentIds.AsMemory(), cachedSegmentReader);
        }

        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        if (!cachedSegmentReader.HasCachedBody(fileSegmentIds[foundSegment.FoundIndex]))
            return null;

        onSegmentIndexChanged?.Invoke(foundSegment.FoundIndex);
        return new CachedSegmentStream(
            fileSegmentIds.AsMemory()[foundSegment.FoundIndex..],
            cachedSegmentReader,
            rangeStart - foundSegment.FoundByteRange.StartInclusive
        );
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(
            segmentIds,
            usenetClient,
            streamingBufferSettings,
            consumedSegments => onSegmentIndexChanged?.Invoke(firstSegmentIndex + consumedSegments),
            cancellationToken
        );
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (!disposing) return;
        _seekOverlayStream?.Dispose();
        _seekOverlayStream = null;
        _innerStream?.Dispose();
        _innerStream = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_seekOverlayStream != null)
            await _seekOverlayStream.DisposeAsync().ConfigureAwait(false);
        if (_innerStream != null)
            await _innerStream.DisposeAsync().ConfigureAwait(false);
        _seekOverlayStream = null;
        _innerStream = null;
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
