using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
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

    public async Task<UsenetYencHeader?> TryReadAsync(
        string segmentId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            var row = await dbContext.YencHeaderCache
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.SegmentId == segmentId, cancellationToken)
                .ConfigureAwait(false);

            if (row == null)
            {
                Interlocked.Increment(ref _misses);
                return null;
            }

            Interlocked.Increment(ref _hits);
            return new UsenetYencHeader
            {
                FileName = row.FileName,
                FileSize = row.FileSize,
                LineLength = row.LineLength,
                PartNumber = row.PartNumber,
                TotalParts = row.TotalParts,
                PartSize = row.PartSize,
                PartOffset = row.PartOffset,
            };
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Debug(ex,
                "SharedHeaderCache read failed for segment {SegmentId} - falling back to NNTP",
                segmentId);
            Interlocked.Increment(ref _misses);
            return null;
        }
    }

    public async Task WriteAsync(
        string segmentId,
        UsenetYencHeader header,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = new DavDatabaseContext();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO yenc_header_cache
                    (segment_id, file_name, file_size, line_length,
                     part_number, total_parts, part_size, part_offset, cached_at)
                VALUES
                    ({segmentId}, {header.FileName}, {header.FileSize}, {header.LineLength},
                     {header.PartNumber}, {header.TotalParts}, {header.PartSize}, {header.PartOffset}, CURRENT_TIMESTAMP)
                ON CONFLICT (segment_id) DO UPDATE SET
                    file_name = excluded.file_name,
                    file_size = excluded.file_size,
                    line_length = excluded.line_length,
                    part_number = excluded.part_number,
                    total_parts = excluded.total_parts,
                    part_size = excluded.part_size,
                    part_offset = excluded.part_offset,
                    cached_at = CURRENT_TIMESTAMP;
            ", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Interlocked.Increment(ref _writeFailures);
            Log.Debug(ex, "SharedHeaderCache write failed for segment {SegmentId}", segmentId);
        }
    }
}
