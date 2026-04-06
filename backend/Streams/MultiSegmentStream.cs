using System.Threading.Channels;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class MultiSegmentStream : FastReadOnlyNonSeekableStream
{
    private readonly Memory<string> _segmentIds;
    private readonly INntpClient _usenetClient;
    private readonly Channel<Task<Stream>> _streamTasks;
    private readonly ContextualCancellationTokenSource _cts;
    private readonly StreamingBufferSettings _streamingBufferSettings;
    private readonly Action<int>? _onSegmentConsumedCallback;
    private readonly SemaphoreSlim _prefetchWindow;
    private readonly object _disposeLock = new();
    private readonly Task _downloadSegmentsTask;
    private Task? _disposeTask;
    private Stream? _stream;
    private int _consumedSegments;
    private int _windowExpanded;
    private bool _disposed;

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken cancellationToken
    )
    {
        return Create(segmentIds, usenetClient, StreamingBufferSettings.Fixed(articleBufferSize), cancellationToken);
    }

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        StreamingBufferSettings streamingBufferSettings,
        Action<int>? onSegmentConsumedCallback,
        CancellationToken cancellationToken
    )
    {
        return streamingBufferSettings.MaxBufferedSegments == 0
            ? new UnbufferedMultiSegmentStream(segmentIds, usenetClient)
            : new MultiSegmentStream(
                segmentIds,
                usenetClient,
                streamingBufferSettings,
                onSegmentConsumedCallback,
                cancellationToken
            );
    }

    public static Stream Create
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        StreamingBufferSettings streamingBufferSettings,
        CancellationToken cancellationToken
    )
    {
        return Create(
            segmentIds,
            usenetClient,
            streamingBufferSettings,
            onSegmentConsumedCallback: null,
            cancellationToken
        );
    }

    private MultiSegmentStream
    (
        Memory<string> segmentIds,
        INntpClient usenetClient,
        StreamingBufferSettings streamingBufferSettings,
        Action<int>? onSegmentConsumedCallback,
        CancellationToken cancellationToken
    )
    {
        _segmentIds = segmentIds;
        _usenetClient = usenetClient;
        _streamingBufferSettings = streamingBufferSettings;
        _onSegmentConsumedCallback = onSegmentConsumedCallback;
        _streamTasks = Channel.CreateBounded<Task<Stream>>(streamingBufferSettings.MaxBufferedSegments);
        _cts = ContextualCancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _prefetchWindow = new SemaphoreSlim(
            streamingBufferSettings.StartupBufferedSegments,
            streamingBufferSettings.MaxBufferedSegments
        );

        if (streamingBufferSettings.RampAfterConsumedSegments == 0)
            ExpandWindowToMax();

        _downloadSegmentsTask = DownloadSegments(_cts.Token);
    }

    private async Task DownloadSegments(CancellationToken cancellationToken)
    {
        try
        {
            for (var i = 0; i < _segmentIds.Length; i++)
            {
                await _prefetchWindow.WaitAsync(cancellationToken).ConfigureAwait(false);
                var permitTransferred = false;

                try
                {
                    if (!await _streamTasks.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        ReleaseWindowPermit();
                        break;
                    }

                    var segmentId = _segmentIds.Span[i];
                    var streamTask = DownloadSegment(segmentId, cancellationToken);

                    if (_streamTasks.Writer.TryWrite(streamTask))
                    {
                        permitTransferred = true;
                        continue;
                    }

                    await DisposePendingStreamAsync(streamTask).ConfigureAwait(false);
                    ReleaseWindowPermit();
                    break;
                }
                catch
                {
                    if (!permitTransferred)
                        ReleaseWindowPermit();
                    throw;
                }
            }
        }
        finally
        {
            _streamTasks.Writer.TryComplete();
        }
    }

    private async Task<Stream> DownloadSegment
    (
        string segmentId,
        CancellationToken cancellationToken
    )
    {
        var bodyResponse = await _usenetClient
            .DecodedBodyWithFallbackAsync(
                segmentId,
                cancellationToken,
                (candidateSegmentId, ct) => _usenetClient.AcquireExclusiveConnectionAsync(candidateSegmentId, ct)
            )
            .ConfigureAwait(false);
        return bodyResponse.Stream;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        while (!cancellationToken.IsCancellationRequested)
        {
            // if the stream is null, get the next stream.
            if (_stream == null)
            {
                if (!await _streamTasks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)) return 0;
                if (!_streamTasks.Reader.TryRead(out var streamTask)) return 0;
                _stream = await streamTask.ConfigureAwait(false);
            }

            // read from the stream
            var read = await _stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read > 0) return read;

            // if the stream ended, continue to the next stream.
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
            OnSegmentConsumed();
        }

        return 0;
    }

    private void OnSegmentConsumed()
    {
        ReleaseWindowPermit();
        var consumedSegments = Interlocked.Increment(ref _consumedSegments);
        _onSegmentConsumedCallback?.Invoke(consumedSegments);

        if (_windowExpanded == 1) return;
        if (consumedSegments < _streamingBufferSettings.RampAfterConsumedSegments) return;
        ExpandWindowToMax();
    }

    private void ExpandWindowToMax()
    {
        if (Interlocked.Exchange(ref _windowExpanded, 1) == 1) return;

        var additionalPermits = _streamingBufferSettings.MaxBufferedSegments
                                - _streamingBufferSettings.StartupBufferedSegments;
        if (additionalPermits > 0)
            _prefetchWindow.Release(additionalPermits);
    }

    private void ReleaseWindowPermit()
    {
        try
        {
            _prefetchWindow.Release();
        }
        catch (SemaphoreFullException)
        {
            // Disposal can release more permits than remain useful; ignore that.
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing) return;
        _ = DisposeAsyncCore(disposeCurrentStreamSynchronously: true);
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await DisposeAsyncCore(disposeCurrentStreamSynchronously: false).ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private Task DisposeAsyncCore(bool disposeCurrentStreamSynchronously)
    {
        lock (_disposeLock)
        {
            if (_disposeTask != null) return _disposeTask;

            if (_disposed)
            {
                _disposeTask = Task.CompletedTask;
                return _disposeTask;
            }

            _disposed = true;
            _cts.Cancel();
            _streamTasks.Writer.TryComplete();

            if (disposeCurrentStreamSynchronously)
            {
                _stream?.Dispose();
                _stream = null;
            }

            _disposeTask = DisposeAsyncCoreInternal();
            return _disposeTask;
        }
    }

    private async Task DisposeAsyncCoreInternal()
    {
        try
        {
            await _downloadSegmentsTask.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation or failed in-flight downloads should not block cleanup.
        }

        if (_stream != null)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
            _stream = null;
        }

        while (_streamTasks.Reader.TryRead(out var streamTask))
            await DisposePendingStreamAsync(streamTask).ConfigureAwait(false);

        _prefetchWindow.Dispose();
        _cts.Dispose();
        GC.SuppressFinalize(this);
    }

    private static async Task DisposePendingStreamAsync(Task<Stream> streamTask)
    {
        try
        {
            var stream = await streamTask.ConfigureAwait(false);
            await stream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Ignore failed/cancelled segment downloads during cleanup.
        }
    }
}
