using Serilog;
using System.Text.Json;

namespace NzbWebDAV.Clients.Usenet.Caching;

/// <summary>
/// Filesystem-backed L2 segment store, for deployments whose deep cache tier is
/// a mounted NAS rather than an object store.
///
/// S3 is pure overhead once the bytes already live on a mounted filesystem, and
/// the existing object key is already filesystem-shaped
/// (<c>segments/{2-hex}/{full-hex}</c>, 256-way fan-out) so it maps to
/// directories unchanged.
///
/// Metadata goes into a <c>.meta</c> sidecar next to the body - the same pattern
/// L1 already uses. That sidecar is the entire point of this backend: an S3
/// gateway such as `rclone serve s3` keeps <c>x-amz-meta-*</c> in memory only,
/// so every yEnc header is lost on restart. The read path then gets a body back,
/// counts an L2 hit, fails to parse the missing header, and silently falls back
/// to NNTP - a cache that reports success while doing nothing.
///
/// Writes are body-then-sidecar via temp file + atomic rename. A crash between
/// the two leaves an orphaned body, which reads treat as a miss and the next
/// write overwrites.
/// </summary>
internal static class FilesystemSegmentStore
{
    private const string MetaSuffix = ".meta";
    private const string OwnersDirectory = "owners";

    /// <summary>
    /// Owner markers let <see cref="CreateDeleteByOwnerDelegate"/> stay
    /// proportional to one NZB's segments. The S3 implementation lists the
    /// whole bucket and filters client-side, which would mean walking the
    /// entire NAS tree for every deletion.
    /// </summary>
    private static string OwnerMarkerPath(string root, Guid ownerNzbId, string hex)
        => Path.Combine(root, OwnersDirectory, ownerNzbId.ToString("N"), hex);

    private static string BodyPath(string root, string objectKey)
        => Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Recovers the sharded body path from an owner marker's file name.</summary>
    private static string BodyPathFromHex(string root, string hex)
        => Path.Combine(root, "segments", hex[..2], hex);

    public static Func<CancellationToken, Task> CreateEnsureRootDelegate(string root)
    {
        return _ =>
        {
            Directory.CreateDirectory(root);
            WarnIfNotAMountPoint(root);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// A NAS-backed L2 root that is not actually a mount point is dangerous, not
    /// merely slow: Docker materializes a missing bind-mount source as an empty
    /// local directory, so L2 would quietly fill the host's own disk instead of
    /// the NAS. Warning rather than throwing, because pointing L2 at a large
    /// local disk is a legitimate configuration.
    /// </summary>
    private static void WarnIfNotAMountPoint(string root)
    {
        if (!OperatingSystem.IsLinux())
            return;

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

            // Field 5 of each /proc/self/mountinfo line is the mount point.
            var isMountPoint = File.ReadLines("/proc/self/mountinfo")
                .Select(line => line.Split(' '))
                .Any(fields => fields.Length > 4 &&
                               Path.TrimEndingDirectorySeparator(fields[4]) == full);

            if (!isMountPoint)
            {
                Log.Warning(
                    "L2 filesystem root {Path} is not a mount point. If this should be a NAS " +
                    "share, it is not mounted and L2 will fill the host disk instead. Ignore " +
                    "this if the L2 tier is intentionally on local storage.",
                    root);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not determine whether L2 root {Path} is a mount point.", root);
        }
    }

    public static Func<string, CancellationToken, Task<ObjectStorageSegmentCache.ReadResult?>>
        CreateReadDelegate(string root)
    {
        return async (segmentId, ct) =>
        {
            var bodyPath = BodyPath(root, ObjectStorageSegmentCache.GetObjectKey(segmentId));
            var metaPath = bodyPath + MetaSuffix;

            // Both halves are required. A body without its sidecar cannot
            // reconstruct the yEnc header, so it is a miss rather than a
            // half-usable hit.
            if (!File.Exists(bodyPath) || !File.Exists(metaPath))
                return null;

            var metaJson = await File.ReadAllTextAsync(metaPath, ct).ConfigureAwait(false);
            var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(metaJson);
            if (metadata is null || metadata.Count == 0)
                return null;

            var body = await File.ReadAllBytesAsync(bodyPath, ct).ConfigureAwait(false);
            return new ObjectStorageSegmentCache.ReadResult(
                body,
                ObjectStorageSegmentCache.NormalizeMetadata(metadata));
        };
    }

    public static Func<ObjectStorageSegmentCache.WriteRequest, CancellationToken, Task>
        CreateWriteDelegate(string root, string storageClass)
    {
        return async (request, ct) =>
        {
            var objectKey = ObjectStorageSegmentCache.GetObjectKey(request.SegmentId);
            var bodyPath = BodyPath(root, objectKey);
            Directory.CreateDirectory(Path.GetDirectoryName(bodyPath)!);

            await WriteAtomicAsync(
                bodyPath,
                stream => stream.WriteAsync(request.Body, ct).AsTask(),
                ct).ConfigureAwait(false);

            var headers = ObjectStorageSegmentCache.BuildWriteHeadersInternal(request, storageClass);
            var metaJson = JsonSerializer.Serialize(headers);
            await WriteAtomicAsync(
                bodyPath + MetaSuffix,
                async stream =>
                {
                    await using var writer = new StreamWriter(stream, leaveOpen: true);
                    await writer.WriteAsync(metaJson.AsMemory(), ct).ConfigureAwait(false);
                },
                ct).ConfigureAwait(false);

            if (!request.OwnerNzbId.HasValue)
                return;

            var hex = objectKey[(objectKey.LastIndexOf('/') + 1)..];
            var markerPath = OwnerMarkerPath(root, request.OwnerNzbId.Value, hex);
            Directory.CreateDirectory(Path.GetDirectoryName(markerPath)!);
            if (!File.Exists(markerPath))
                await File.WriteAllBytesAsync(markerPath, [], ct).ConfigureAwait(false);
        };
    }

    public static Func<Guid, CancellationToken, Task> CreateDeleteByOwnerDelegate(string root)
    {
        return (ownerNzbId, ct) =>
        {
            var ownerDirectory = Path.Combine(root, OwnersDirectory, ownerNzbId.ToString("N"));
            if (!Directory.Exists(ownerDirectory))
                return Task.CompletedTask;

            foreach (var markerPath in Directory.EnumerateFiles(ownerDirectory))
            {
                ct.ThrowIfCancellationRequested();

                var hex = Path.GetFileName(markerPath);
                if (hex.Length >= 2)
                {
                    var bodyPath = BodyPathFromHex(root, hex);
                    DeleteQuietly(bodyPath);
                    DeleteQuietly(bodyPath + MetaSuffix);
                }

                DeleteQuietly(markerPath);
            }

            try
            {
                Directory.Delete(ownerDirectory, recursive: false);
            }
            catch (IOException)
            {
                // Either already gone, or a concurrent write re-populated it;
                // the next deletion sweeps it.
            }

            return Task.CompletedTask;
        };
    }

    private static async Task WriteAtomicAsync(
        string destinationPath,
        Func<Stream, Task> writeBody,
        CancellationToken ct)
    {
        // Unique temp name: concurrent writers for the same segment must not
        // share a staging file.
        var tempPath = $"{destinationPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             tempPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None))
            {
                await writeBody(stream).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }

            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                       && File.Exists(destinationPath))
            {
                // Lost a race, or a reader holds the destination open. Both are
                // benign here: the store is content-addressed, so a concurrent
                // writer for this key is writing identical bytes. POSIX rename
                // would have replaced it silently; Windows refuses to replace an
                // open file. Either way the cached content is already correct,
                // so drop our copy instead of failing the write.
                DeleteQuietly(tempPath);
            }
        }
        catch
        {
            DeleteQuietly(tempPath);
            throw;
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
