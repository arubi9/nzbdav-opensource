using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using NzbWebDAV.Streams;
using SharpCompress.Archives.SevenZip;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace NzbWebDAV.Utils;

public static class SevenZipUtil
{
    public static async Task<List<SevenZipEntry>> GetSevenZipEntriesAsync
    (
        Stream stream,
        string? password,
        CancellationToken ct
    )
    {
        await using var cancellableStream = new CancellableStream(stream, ct);
        var archive = await SevenZipArchive
            .OpenAsyncArchive(cancellableStream, new ReaderOptions { Password = password }, ct)
            .ConfigureAwait(false);
        await using (archive.ConfigureAwait(false))
        {
            var entries = new List<SevenZipEntry>();
            var index = 0;
            await foreach (var rawEntry in archive.EntriesAsync.WithCancellation(ct))
            {
                if (rawEntry is not SevenZipArchiveEntry entry) continue;
                if (!entry.IsDirectory)
                    entries.Add(new SevenZipEntry(entry, (SevenZipArchive)entry.Archive, index++, password));
            }
            return entries;
        }
    }

    public static List<SevenZipEntry> GetSevenZipEntries(Stream stream, string? password = null)
    {
        using var archive = SevenZipArchive.OpenArchive(stream, new ReaderOptions { Password = password });
        return archive.Entries
            .OfType<SevenZipArchiveEntry>()
            .Where(x => !x.IsDirectory)
            .Select((entry, index) => new SevenZipEntry(entry, (SevenZipArchive)entry.Archive, index, password))
            .ToList();
    }

    public class SevenZipEntry(SevenZipArchiveEntry entry, SevenZipArchive archive, int index, string? password)
    {
        public SevenZipArchiveEntry Entry => entry;
        public string PathWithinArchive { get; } = entry.Key!;
        public CompressionType CompressionType { get; } = entry.GetCompressionType();
        public bool IsEncrypted { get; } = entry.IsEncrypted;
        public bool IsSolid { get; } = entry.IsSolid;

        public AesParams? AesParams { get; } =
            AesParams.FromCoderInfo(entry.GetAesCoderInfoProps(), password, entry.Size);

        public long FolderStartByteOffset { get; } = entry.GetFolderStartByteOffset();

        public LongRange ByteRangeWithinArchive { get; } =
            LongRange.FromStartAndSize(archive.GetEntryStartByteOffset(index), archive.GetPackSize(index));
    }
}
