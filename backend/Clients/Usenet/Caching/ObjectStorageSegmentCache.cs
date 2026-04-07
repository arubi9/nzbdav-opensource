using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Minio;
using Minio.DataModel.Args;
using NzbWebDAV.Config;
using Serilog;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class ObjectStorageSegmentCache : IDisposable
{
    private readonly Func<string, CancellationToken, Task<byte[]?>> _tryReadAsync;
    private readonly Func<WriteRequest, CancellationToken, Task> _writeAsync;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly ConcurrentQueue<WriteRequest> _writeQueue = new();
    private readonly int _queueCapacity;
    private readonly Task _writerTask;
    private volatile int _queueCount;
    private bool _disposed;

    private long _l2Hits;
    private long _l2Misses;
    private long _l2Writes;
    private long _l2WriteFailures;
    private long _l2WritesDropped;

    public long L2Hits => Interlocked.Read(ref _l2Hits);
    public long L2Misses => Interlocked.Read(ref _l2Misses);
    public long L2Writes => Interlocked.Read(ref _l2Writes);
    public long L2WriteFailures => Interlocked.Read(ref _l2WriteFailures);
    public long L2WritesDropped => Interlocked.Read(ref _l2WritesDropped);
    public string BucketName { get; }

    public ObjectStorageSegmentCache(ConfigManager configManager)
        : this(
            bucketName: configManager.GetL2BucketName(),
            queueCapacity: configManager.GetL2WriteQueueCapacity(),
            tryReadAsync: CreateReadDelegate(configManager),
            writeAsync: CreateWriteDelegate(configManager))
    {
    }

    public ObjectStorageSegmentCache(
        string bucketName,
        int queueCapacity,
        Func<string, CancellationToken, Task<byte[]?>> tryReadAsync,
        Func<WriteRequest, CancellationToken, Task> writeAsync,
        bool startWriter = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);

        BucketName = bucketName;
        _queueCapacity = queueCapacity;
        _tryReadAsync = tryReadAsync;
        _writeAsync = writeAsync;
        _writerTask = startWriter ? Task.Run(WriterLoopAsync) : Task.CompletedTask;
    }

    public static string GetObjectKey(string segmentId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segmentId));
        var hex = Convert.ToHexStringLower(hash);
        return $"segments/{hex[..2]}/{hex}";
    }

    public async Task EnsureBucketExistsAsync(CancellationToken ct)
    {
        if (_disposed)
            return;

        if (_tryReadAsync == null)
            return;

        await Task.CompletedTask.WaitAsync(ct).ConfigureAwait(false);
    }

    public async Task<Stream?> TryReadAsync(string segmentId, CancellationToken ct)
    {
        if (_disposed)
            return null;

        var body = await _tryReadAsync(segmentId, ct).ConfigureAwait(false);
        if (body is null)
        {
            Interlocked.Increment(ref _l2Misses);
            return null;
        }

        Interlocked.Increment(ref _l2Hits);
        return new MemoryStream(body, writable: false);
    }

    public void EnqueueWrite(
        string segmentId,
        byte[] body,
        SegmentCategory category,
        Guid? ownerNzbId,
        string yencFileName)
    {
        if (_disposed)
            return;

        if (Interlocked.Increment(ref _queueCount) > _queueCapacity)
        {
            Interlocked.Decrement(ref _queueCount);
            Interlocked.Increment(ref _l2WritesDropped);
            Log.Debug(
                "L2 write queue full - dropping write for segment {SegmentId}.",
                segmentId);
            return;
        }

        _writeQueue.Enqueue(new WriteRequest(segmentId, body, category, ownerNzbId, yencFileName));
        _queueSignal.Release();
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownCts.Cancel();
        _queueSignal.Release();
        try
        {
            _writerTask.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // best-effort shutdown
        }
        _shutdownCts.Dispose();
        _queueSignal.Dispose();
    }

    private async Task WriterLoopAsync()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                await _queueSignal.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            while (_writeQueue.TryDequeue(out var request))
            {
                Interlocked.Decrement(ref _queueCount);

                try
                {
                    await _writeAsync(request, _shutdownCts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref _l2Writes);
                }
                catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _l2WriteFailures);
                    Log.Warning(ex, "L2 write failed for segment {SegmentId}", request.SegmentId);
                }
            }
        }
    }

    private static Func<string, CancellationToken, Task<byte[]?>> CreateReadDelegate(ConfigManager configManager)
    {
        var client = CreateClient(configManager);
        var bucketName = configManager.GetL2BucketName();

        return async (segmentId, ct) =>
        {
            try
            {
                using var memory = new MemoryStream();
                var args = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(GetObjectKey(segmentId))
                    .WithCallbackStream(stream => stream.CopyTo(memory));
                await client.GetObjectAsync(args, ct).ConfigureAwait(false);
                return memory.ToArray();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "L2 read failed for segment {SegmentId}", segmentId);
                return null;
            }
        };
    }

    private static Func<WriteRequest, CancellationToken, Task> CreateWriteDelegate(ConfigManager configManager)
    {
        var client = CreateClient(configManager);
        var bucketName = configManager.GetL2BucketName();

        return async (request, ct) =>
        {
            using var stream = new MemoryStream(request.Body, writable: false);
            var args = new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(GetObjectKey(request.SegmentId))
                .WithStreamData(stream)
                .WithObjectSize(request.Body.Length)
                .WithContentType("application/octet-stream");
            await client.PutObjectAsync(args, ct).ConfigureAwait(false);
        };
    }

    private static IMinioClient CreateClient(ConfigManager configManager)
    {
        return new MinioClient()
            .WithEndpoint(configManager.GetL2Endpoint())
            .WithCredentials(configManager.GetL2AccessKey(), configManager.GetL2SecretKey())
            .WithSSL(configManager.GetL2UseSsl())
            .Build();
    }

    public readonly record struct WriteRequest(
        string SegmentId,
        byte[] Body,
        SegmentCategory Category,
        Guid? OwnerNzbId,
        string YencFileName);
}
