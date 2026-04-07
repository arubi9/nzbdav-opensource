using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;

namespace NzbWebDAV.Streams;

public class DavMultipartFileStream(
    DavMultipartFile.FilePart[] fileParts,
    INntpClient usenetClient,
    StreamingBufferSettings streamingBufferSettings,
    Action<int>? onSegmentIndexChanged = null
) : Stream
{
    private long _position = 0;
    private CombinedStream? _innerStream;
    private bool _disposed;

    /// <summary>
    /// Optional ambient cancellation token plumbed into the sync-read
    /// bridge. Set this to <c>HttpContext.RequestAborted</c> from the
    /// controller/handler that owns the stream so a disconnected client
    /// unblocks the threadpool thread that's waiting on the NNTP fetch.
    /// Defaults to <see cref="CancellationToken.None"/> for back-compat.
    /// </summary>
    public CancellationToken AmbientCancellationToken { get; set; } = CancellationToken.None;


    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Sync-read bridge for WebDAV clients that don't use ReadAsync (older
        // rclone, Windows WebClient, some third-party archive libs). This
        // blocks a threadpool thread for the full duration of the NNTP
        // fetch — there is no way to avoid that for a sync API. We rely on
        // the enlarged threadpool (min 50, max 1000, set in Program.cs) to
        // absorb concurrent sync reads, and on /ready backpressure to drain
        // nodes that get saturated. AmbientCancellationToken propagates
        // client-disconnect so aborted clients free their thread promptly.
        return ReadAsync(buffer, offset, count, AmbientCancellationToken).GetAwaiter().GetResult();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (_innerStream == null) _innerStream = GetFileStream(_position);
        var read = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
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
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    public override void SetLength(long value)
    {
        throw new InvalidOperationException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new InvalidOperationException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; } = fileParts.Select(x => x.FilePartByteRange.Count).Sum();

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }


    private (int filePartIndex, long filePartOffset) SeekFilePart(long byteOffset)
    {
        long offset = 0;
        for (var i = 0; i < fileParts.Length; i++)
        {
            var filePart = fileParts[i];
            var nextOffset = offset + filePart.FilePartByteRange.Count;
            if (byteOffset < nextOffset)
                return (i, offset);
            offset = nextOffset;
        }

        throw new SeekPositionNotFoundException($"Corrupt file. Cannot seek to byte position {byteOffset}.");
    }

    private CombinedStream GetFileStream(long rangeStart)
    {
        if (rangeStart == 0) return GetCombinedStream(0, 0);
        var (filePartIndex, filePartOffset) = SeekFilePart(rangeStart);
        var stream = GetCombinedStream(filePartIndex, rangeStart - filePartOffset);
        return stream;
    }

    private CombinedStream GetCombinedStream(int firstFilePartIndex, long additionalOffset)
    {
        var initialSegmentOffset = fileParts
            .Take(firstFilePartIndex)
            .Sum(part => part.SegmentIds.Length);
        onSegmentIndexChanged?.Invoke(initialSegmentOffset);

        var streams = fileParts[firstFilePartIndex..]
            .Select((x, i) =>
            {
                var offset = (i == 0) ? additionalOffset : 0;
                var segmentOffset = initialSegmentOffset
                                    + fileParts
                                        .Skip(firstFilePartIndex)
                                        .Take(i)
                                        .Sum(part => part.SegmentIds.Length);
                var stream = usenetClient.GetFileStream(
                    x.SegmentIds,
                    x.SegmentIdByteRange.Count,
                    streamingBufferSettings,
                    consumedSegments => onSegmentIndexChanged?.Invoke(segmentOffset + consumedSegments)
                );
                stream.Seek(x.FilePartByteRange.StartInclusive + offset, SeekOrigin.Begin);
                return Task.FromResult(stream.LimitLength(x.FilePartByteRange.Count - offset));
            });
        return new CombinedStream(streams);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
