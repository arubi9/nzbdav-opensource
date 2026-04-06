using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Extensions;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

internal sealed class CachedSegmentStream(
    ReadOnlyMemory<string> segmentIds,
    ICachedSegmentReader cachedSegmentReader,
    long initialDiscardBytes = 0
) : FastReadOnlyNonSeekableStream
{
    private readonly ReadOnlyMemory<string> _segmentIds = segmentIds;
    private readonly ICachedSegmentReader _cachedSegmentReader = cachedSegmentReader;
    private long _initialDiscardBytes = initialDiscardBytes;
    private Stream? _stream;
    private int _currentIndex;
    private bool _disposed;

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_stream == null)
            {
                if (_currentIndex >= _segmentIds.Length) return 0;
                if (!_cachedSegmentReader.TryReadCachedBody(_segmentIds.Span[_currentIndex++], out var response)) return 0;
                _stream = response.Stream;

                if (_initialDiscardBytes > 0)
                {
                    await _stream.DiscardBytesAsync(_initialDiscardBytes, cancellationToken).ConfigureAwait(false);
                    _initialDiscardBytes = 0;
                }
            }

            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;

            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        return 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed || !disposing) return;
        _disposed = true;
        _stream?.Dispose();
        _stream = null;
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_stream != null)
            await _stream.DisposeAsync().ConfigureAwait(false);
        _stream = null;
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
