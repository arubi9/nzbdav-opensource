using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet.Caching;

public sealed class SharedHeaderCache
{
    private long _hits;
    private long _misses;
    private long _writeFailures;

    public long Hits => Interlocked.Read(ref _hits);
    public long Misses => Interlocked.Read(ref _misses);
    public long WriteFailures => Interlocked.Read(ref _writeFailures);

    public async Task<UsenetYencHeader?> TryReadAsync(string segmentId, CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var entry = await dbContext.Set<YencHeaderCacheEntry>()
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SegmentId == segmentId, cancellationToken)
                .ConfigureAwait(false);

            if (entry == null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            Interlocked.Increment(ref _hits);
            return new UsenetYencHeader
            {
                FileName = entry.FileName,
                FileSize = entry.FileSize,
                LineLength = entry.LineLength,
                PartNumber = entry.PartNumber,
                TotalParts = entry.TotalParts,
                PartSize = entry.PartSize,
                PartOffset = entry.PartOffset,
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug(ex, "SharedHeaderCache read failed for segment {SegmentId}", segmentId);
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    public async Task WriteAsync(string segmentId, UsenetYencHeader header, CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO yenc_header_cache
                    (segment_id, file_name, file_size, line_length, part_number, total_parts, part_size, part_offset, cached_at)
                VALUES
                    ({segmentId}, {header.FileName}, {header.FileSize}, {header.LineLength}, {header.PartNumber}, {header.TotalParts}, {header.PartSize}, {header.PartOffset}, now())
                ON CONFLICT (segment_id) DO UPDATE SET
                    file_name = EXCLUDED.file_name,
                    file_size = EXCLUDED.file_size,
                    line_length = EXCLUDED.line_length,
                    part_number = EXCLUDED.part_number,
                    total_parts = EXCLUDED.total_parts,
                    part_size = EXCLUDED.part_size,
                    part_offset = EXCLUDED.part_offset,
                    cached_at = now();
            ", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _writeFailures);
            Log.Debug(ex, "SharedHeaderCache write failed for segment {SegmentId}", segmentId);
        }
    }
}
