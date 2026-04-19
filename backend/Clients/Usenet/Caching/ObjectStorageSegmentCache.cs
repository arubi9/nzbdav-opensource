using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Minio;
using Minio.DataModel.Args;
using NzbWebDAV.Config;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class ObjectStorageSegmentCache : IDisposable
{
    // See LiveSegmentCache.YencHeaderJsonOptions for the why — kept in sync.
    private static readonly JsonSerializerOptions YencHeaderJsonOptions = new()
    {
        IncludeFields = true
    };

    public sealed record ReadResult(byte[] Body, IReadOnlyDictionary<string, string> Metadata);
    private sealed record ConfigBinding(
        string BucketName,
        int QueueCapacity,
        int WriterParallelism,
        TimeSpan ReadTimeout,
        TimeSpan WriteTimeout,
        Func<CancellationToken, Task> EnsureBucketExistsAsync,
        Func<string, CancellationToken, Task<ReadResult?>> TryReadAsync,
        Func<WriteRequest, CancellationToken, Task> WriteAsync,
        Func<Guid, CancellationToken, Task>? DeleteByOwnerAsync,
        bool StartWriter);

    private readonly Func<CancellationToken, Task> _ensureBucketExistsAsync;
    private readonly Func<string, CancellationToken, Task<ReadResult?>> _tryReadAsync;
    private readonly Func<WriteRequest, CancellationToken, Task> _writeAsync;
    private readonly Func<Guid, CancellationToken, Task>? _deleteByOwnerAsync;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly ConcurrentQueue<WriteRequest> _writeQueue = new();
    private readonly int _queueCapacity;
    private readonly int _writerParallelism;
    private readonly TimeSpan _readTimeout;
    private readonly TimeSpan _writeTimeout;
    private readonly IReadOnlyList<Task> _writerTasks;
    private volatile int _queueCount;
    private volatile bool _shutdownRequested;
    private bool _disposed;

    private long _l2Hits;
    private long _l2Misses;
    private long _l2Writes;
    private long _l2WriteFailures;
    private long _l2WritesDropped;
    private long _l2ReadTimeouts;
    private long _lastWriteUnixtime;

    public long L2Hits => Interlocked.Read(ref _l2Hits);
    public long L2Misses => Interlocked.Read(ref _l2Misses);
    public long L2Writes => Interlocked.Read(ref _l2Writes);
    public long L2WriteFailures => Interlocked.Read(ref _l2WriteFailures);
    public long L2WritesDropped => Interlocked.Read(ref _l2WritesDropped);
    public long L2ReadTimeouts => Interlocked.Read(ref _l2ReadTimeouts);
    public long LastWriteUnixtime => Interlocked.Read(ref _lastWriteUnixtime);
    public int QueueDepth => _queueCount;
    public int WriterParallelism => _writerParallelism;
    public string BucketName { get; }

    public ObjectStorageSegmentCache(ConfigManager configManager)
        : this(CreateFromConfig(configManager))
    {
    }

    private ObjectStorageSegmentCache(ConfigBinding binding)
        : this(
            binding.BucketName,
            binding.QueueCapacity,
            binding.EnsureBucketExistsAsync,
            binding.TryReadAsync,
            binding.WriteAsync,
            binding.DeleteByOwnerAsync,
            binding.StartWriter,
            binding.ReadTimeout,
            binding.WriteTimeout,
            binding.WriterParallelism)
    {
    }

    public ObjectStorageSegmentCache(
        string bucketName,
        int queueCapacity,
        Func<CancellationToken, Task> ensureBucketExistsAsync,
        Func<string, CancellationToken, Task<ReadResult?>> tryReadAsync,
        Func<WriteRequest, CancellationToken, Task> writeAsync,
        Func<Guid, CancellationToken, Task>? deleteByOwnerAsync = null,
        bool startWriter = true,
        TimeSpan? readTimeout = null,
        TimeSpan? writeTimeout = null,
        int writerParallelism = 4)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(writerParallelism);
        if (writerParallelism > 32)
            throw new ArgumentOutOfRangeException(nameof(writerParallelism), "Must be 1-32");

        BucketName = bucketName;
        _queueCapacity = queueCapacity;
        _writerParallelism = writerParallelism;
        _readTimeout = readTimeout ?? TimeSpan.FromSeconds(30);
        _writeTimeout = writeTimeout ?? TimeSpan.FromSeconds(60);
        _ensureBucketExistsAsync = ensureBucketExistsAsync;
        _tryReadAsync = tryReadAsync;
        _writeAsync = writeAsync;
        _deleteByOwnerAsync = deleteByOwnerAsync;
        _lastWriteUnixtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        if (startWriter)
        {
            var tasks = new Task[writerParallelism];
            for (var i = 0; i < writerParallelism; i++)
                tasks[i] = Task.Run(WriterLoopAsync);
            _writerTasks = tasks;
        }
        else
        {
            _writerTasks = Array.Empty<Task>();
        }
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

        await _ensureBucketExistsAsync(ct).ConfigureAwait(false);
    }

    public async Task<Stream?> TryReadAsync(string segmentId, CancellationToken ct)
    {
        var result = await TryReadWithMetadataAsync(segmentId, ct).ConfigureAwait(false);
        return result is null ? null : new MemoryStream(result.Body, writable: false);
    }

    public async Task<ReadResult?> TryReadWithMetadataAsync(string segmentId, CancellationToken ct)
    {
        if (_disposed)
            return null;

        ReadResult? result;
        using var timeoutCts = new CancellationTokenSource(_readTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            result = await _tryReadAsync(segmentId, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            Interlocked.Increment(ref _l2ReadTimeouts);
            Log.Warning("L2 read timed out after {Timeout:c} for segment {SegmentId}", _readTimeout, segmentId);
            result = null;
        }
        catch (OperationCanceledException)
        {
            // Caller-initiated cancellation — let it propagate.
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "L2 read failed for segment {SegmentId}", segmentId);
            result = null;
        }

        if (result is null)
        {
            Interlocked.Increment(ref _l2Misses);
            return null;
        }

        Interlocked.Increment(ref _l2Hits);
        return result with
        {
            Metadata = new Dictionary<string, string>(result.Metadata, StringComparer.OrdinalIgnoreCase)
        };
    }

    public void EnqueueWrite(
        string segmentId,
        byte[] body,
        SegmentCategory category,
        Guid? ownerNzbId,
        UsenetYencHeader yencHeaders)
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

        _writeQueue.Enqueue(new WriteRequest(segmentId, body, category, ownerNzbId, yencHeaders));
        _queueSignal.Release();
    }

    public async Task DeleteByOwnerAsync(Guid ownerNzbId, CancellationToken ct)
    {
        if (_disposed || _deleteByOwnerAsync is null)
            return;

        await _deleteByOwnerAsync(ownerNzbId, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _shutdownRequested = true;
        // Wake every writer so they observe the shutdown flag.
        if (_writerTasks.Count > 0)
            _queueSignal.Release(_writerTasks.Count);

        try
        {
            var writerArray = _writerTasks.ToArray();
            if (writerArray.Length > 0 && !Task.WaitAll(writerArray, TimeSpan.FromSeconds(10)))
            {
                _shutdownCts.Cancel();
                _queueSignal.Release(writerArray.Length);
                Task.WaitAll(writerArray, TimeSpan.FromSeconds(1));
            }
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
        // Single-item-per-wait pattern: each Release() wakes exactly one
        // writer. N workers drain N items concurrently; each handles one
        // PUT at a time so slow backends don't block the whole pool.
        while (true)
        {
            try
            {
                await _queueSignal.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!_writeQueue.TryDequeue(out var request))
            {
                // Empty wake — typically a shutdown signal release.
                if (_shutdownRequested && _writeQueue.IsEmpty)
                    break;
                continue;
            }

            Interlocked.Decrement(ref _queueCount);
            await ProcessOneWriteAsync(request).ConfigureAwait(false);

            if (_shutdownRequested && _writeQueue.IsEmpty)
                break;
        }
    }

    private async Task ProcessOneWriteAsync(WriteRequest request)
    {
        // Bound every PUT with _writeTimeout to prevent half-open sockets
        // parking a writer indefinitely (stall fix from 2026-04-17 spec).
        using var timeoutCts = new CancellationTokenSource(_writeTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCts.Token, timeoutCts.Token);

        var sw = Stopwatch.StartNew();
        try
        {
            await _writeAsync(request, linkedCts.Token).ConfigureAwait(false);
            sw.Stop();
            Interlocked.Increment(ref _l2Writes);
            Interlocked.Exchange(ref _lastWriteUnixtime, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (sw.Elapsed > _writeTimeout / 2)
            {
                Log.Warning(
                    "Slow L2 write for segment {SegmentId} took {Elapsed:c} (threshold {Threshold:c})",
                    request.SegmentId, sw.Elapsed, _writeTimeout / 2);
            }
        }
        catch (OperationCanceledException)
            when (timeoutCts.IsCancellationRequested && !_shutdownCts.IsCancellationRequested)
        {
            Interlocked.Increment(ref _l2WriteFailures);
            Log.Warning(
                "L2 write timed out after {Timeout:c} for segment {SegmentId}",
                _writeTimeout, request.SegmentId);
        }
        catch (OperationCanceledException)
        {
            // Shutdown coincident with operation — swallow, outer loop
            // will exit on next iteration.
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _l2WriteFailures);
            Log.Warning(ex, "L2 write failed for segment {SegmentId}", request.SegmentId);
        }
    }

    private static ConfigBinding CreateFromConfig(ConfigManager configManager)
    {
        var bucketName = configManager.GetL2BucketName();
        var endpoint = configManager.GetL2Endpoint();
        var accessKey = configManager.GetL2AccessKey();
        var secretKey = configManager.GetL2SecretKey();

        if (string.IsNullOrWhiteSpace(endpoint) ||
            string.IsNullOrWhiteSpace(accessKey) ||
            string.IsNullOrWhiteSpace(secretKey))
        {
            Log.Warning("L2 cache is enabled but incomplete configuration was provided. Continuing without shared L2.");
            return new ConfigBinding(
                bucketName,
                configManager.GetL2WriteQueueCapacity(),
                configManager.GetL2WriterParallelism(),
                configManager.GetL2ReadTimeout(),
                configManager.GetL2WriteTimeout(),
                _ => Task.CompletedTask,
                (_, _) => Task.FromResult<ReadResult?>(null),
                (_, _) => Task.CompletedTask,
                null,
                false);
        }

        var client = CreateClient(endpoint, accessKey, secretKey, configManager.IsL2SslEnabled());
        return new ConfigBinding(
            bucketName,
            configManager.GetL2WriteQueueCapacity(),
            configManager.GetL2WriterParallelism(),
            configManager.GetL2ReadTimeout(),
            configManager.GetL2WriteTimeout(),
            CreateEnsureBucketDelegate(client, bucketName),
            CreateReadDelegate(client, bucketName),
            CreateWriteDelegate(client, bucketName),
            CreateDeleteByOwnerDelegate(client, bucketName),
            true);
    }

    private static Func<CancellationToken, Task> CreateEnsureBucketDelegate(IMinioClient client, string bucketName)
    {
        return async ct =>
        {
            var existsArgs = new BucketExistsArgs()
                .WithBucket(bucketName);
            var exists = await client.BucketExistsAsync(existsArgs, ct).ConfigureAwait(false);
            if (exists)
                return;

            var makeArgs = new MakeBucketArgs()
                .WithBucket(bucketName);
            await client.MakeBucketAsync(makeArgs, ct).ConfigureAwait(false);
        };
    }

    private static Func<string, CancellationToken, Task<ReadResult?>> CreateReadDelegate(IMinioClient client, string bucketName)
    {
        return async (segmentId, ct) =>
        {
            try
            {
                using var memory = new MemoryStream();
                var args = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(GetObjectKey(segmentId))
                    .WithCallbackStream(stream => stream.CopyTo(memory));
                var stat = await client.GetObjectAsync(args, ct).ConfigureAwait(false);
                return new ReadResult(memory.ToArray(), NormalizeMetadata(stat.MetaData));
            }
            catch (OperationCanceledException)
            {
                // Let the caller distinguish timeout vs caller-cancel.
                throw;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "L2 read failed for segment {SegmentId}", segmentId);
                return null;
            }
        };
    }

    private static Func<WriteRequest, CancellationToken, Task> CreateWriteDelegate(IMinioClient client, string bucketName)
    {
        return async (request, ct) =>
        {
            using var stream = new MemoryStream(request.Body, writable: false);
            var headers = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["x-amz-meta-segment-id"] = request.SegmentId,
                ["x-amz-meta-yenc-filename"] = request.YencHeaders.FileName,
                // UsenetYencHeader exposes its data via public fields; default
                // JsonSerializer options skip fields and would store only the
                // IsFilePart property, round-tripping every other value to
                // zero and poisoning L2 promotions. See IsCorruptYencHeader /
                // YencHeaderJsonOptions in LiveSegmentCache for the symptom.
                ["x-amz-meta-yenc-header"] = JsonSerializer.Serialize(
                    request.YencHeaders,
                    YencHeaderJsonOptions),
                ["x-amz-meta-category"] = request.Category switch
                {
                    SegmentCategory.SmallFile => "small_file",
                    SegmentCategory.VideoSegment => "video",
                    _ => "unknown"
                }
            };
            if (request.OwnerNzbId.HasValue)
                headers["x-amz-meta-owner-nzb-id"] = request.OwnerNzbId.Value.ToString();

            var args = new PutObjectArgs()
                .WithBucket(bucketName)
                .WithObject(GetObjectKey(request.SegmentId))
                .WithStreamData(stream)
                .WithObjectSize(request.Body.Length)
                .WithContentType("application/octet-stream")
                .WithHeaders(headers);
            await client.PutObjectAsync(args, ct).ConfigureAwait(false);
        };
    }

    private static Func<Guid, CancellationToken, Task> CreateDeleteByOwnerDelegate(IMinioClient client, string bucketName)
    {
        return async (ownerNzbId, ct) =>
        {
            var listArgs = new ListObjectsArgs()
                .WithBucket(bucketName)
                .WithPrefix("segments/")
                .WithRecursive(true)
                .WithIncludeUserMetadata(true);

            await foreach (var item in client.ListObjectsEnumAsync(listArgs, ct).ConfigureAwait(false))
            {
                if (!item.UserMetadata.TryGetValue("X-Amz-Meta-Owner-Nzb-Id", out var metadataValue) &&
                    !item.UserMetadata.TryGetValue("x-amz-meta-owner-nzb-id", out metadataValue))
                {
                    continue;
                }

                if (!Guid.TryParse(metadataValue, out var parsedOwner) || parsedOwner != ownerNzbId)
                    continue;

                var removeArgs = new RemoveObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(item.Key);
                await client.RemoveObjectAsync(removeArgs, ct).ConfigureAwait(false);
            }
        };
    }

    private static IReadOnlyDictionary<string, string> NormalizeMetadata(IReadOnlyDictionary<string, string> metadata)
    {
        var normalized = new Dictionary<string, string>(metadata, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, value) in metadata)
        {
            if (key.StartsWith("x-amz-meta-", StringComparison.OrdinalIgnoreCase))
                continue;

            normalized[$"x-amz-meta-{key}"] = value;
        }

        return normalized;
    }

    private static IMinioClient CreateClient(
        string endpoint,
        string accessKey,
        string secretKey,
        bool useSsl)
    {
        return new MinioClient()
            .WithEndpoint(endpoint)
            .WithCredentials(accessKey, secretKey)
            .WithSSL(useSsl)
            .Build();
    }

    public readonly record struct WriteRequest(
        string SegmentId,
        byte[] Body,
        SegmentCategory Category,
        Guid? OwnerNzbId,
        UsenetYencHeader YencHeaders);
}
