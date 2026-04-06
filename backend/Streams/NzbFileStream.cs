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
    Action<int>? onSegmentIndexChanged = null
) : FastReadOnlyStream
{
    private readonly LongRange?[] _segmentRanges = new LongRange?[fileSegmentIds.Length];
    private long _position;
    private bool _disposed;
    private bool _seekPending;
    private Stream? _innerStream;
    private Stream? _seekOverlayStream;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
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
            var overlayRead = await _seekOverlayStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (overlayRead > 0)
            {
                _position += overlayRead;
                return overlayRead;
            }

            await _seekOverlayStream.DisposeAsync().ConfigureAwait(false);
            _seekOverlayStream = null;
            _innerStream?.Dispose();
            _innerStream = null;
        }

        _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
        var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
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
