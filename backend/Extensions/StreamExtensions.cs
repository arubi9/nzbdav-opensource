using System.Buffers;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Extensions;

public static class StreamExtensions
{
    public static Stream LimitLength(this Stream stream, long length)
    {
        return new LimitedLengthStream(stream, length);
    }

    public static async Task DiscardBytesAsync(this Stream stream, long count, CancellationToken ct = default)
    {
        if (count == 0) return;
        var remaining = count;
        var throwaway = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            while (remaining > 0)
            {
                var toRead = (int)Math.Min(remaining, throwaway.Length);
                var read = await stream.ReadAsync(throwaway.AsMemory(0, toRead), ct).ConfigureAwait(false);
                if (read == 0) break;
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(throwaway);
        }
    }

    public static async Task CopyToPooledAsync
    (
        this Stream source,
        Stream destination,
        int bufferSize = 81_920,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (true)
            {
                var bytesRead = await source
                    .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0) return;

                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task CopyRangeToPooledAsync
    (
        this Stream source,
        Stream destination,
        long start,
        long? end,
        int bufferSize = 64 * 1024,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);

        if (start > 0)
        {
            if (!source.CanSeek)
                throw new IOException("Cannot use range, because the source stream isn't seekable");

            source.Seek(start, SeekOrigin.Begin);
        }

        var bytesToRead = end - start + 1 ?? long.MaxValue;
        var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            while (bytesToRead > 0)
            {
                var requestedBytes = (int)Math.Min(bytesToRead, buffer.Length);
                var bytesRead = await source
                    .ReadAsync(buffer.AsMemory(0, requestedBytes), cancellationToken)
                    .ConfigureAwait(false);
                if (bytesRead == 0) return;

                await destination
                    .WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken)
                    .ConfigureAwait(false);

                bytesToRead -= bytesRead;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static Stream OnDispose(this Stream stream, Action onDispose)
    {
        return new DisposableCallbackStream(stream, onDispose, async () => onDispose?.Invoke());
    }

    public static Stream OnDisposeAsync(this Stream stream, Func<ValueTask> onDisposeAsync)
    {
        return new DisposableCallbackStream(stream, onDisposeAsync: onDisposeAsync);
    }
}
