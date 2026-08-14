using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

/// <summary>
/// The snapshot carries one usenet article id per ~700KB of media, so its size
/// tracks the logical size of the library: a 40TB library is a ~2.4GB document.
/// These tests pin the two properties that keeps that survivable in an 8GB
/// container -- the writer and reader never hold the document, and no id lookup
/// is issued as a single unbounded IN list -- alongside the on-disk format and
/// the validation rules, which must not drift while that is being fixed.
/// </summary>
[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ContentIndexSnapshotStoreTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ContentIndexSnapshotStoreTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private static readonly Guid DirectoryId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid NzbItemId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid RarItemId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid MultipartItemId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    /// <summary>
    /// Captured verbatim from the previous implementation, which serialized the
    /// whole snapshot object graph in one call. Recovery must keep reading files
    /// written before the streaming rewrite, so this literal is the contract.
    /// </summary>
    private const string LegacySnapshotJson =
        """
        {"Version":1,"GeneratedAtUtc":"2023-11-14T22:13:20+00:00","Items":[{"Id":"11111111-1111-1111-1111-111111111111","IdPrefix":"11111","CreatedAt":"2024-01-02T03:04:05","ParentId":"00000000-0000-0000-0000-000000000002","Name":"movies","FileSize":null,"Type":1,"Path":"/content/movies","ReleaseDate":null,"LastHealthCheck":null,"NextHealthCheck":null,"YencPartSize":null,"YencLastPartSize":null,"YencSegmentCount":null,"YencLayoutUniform":null},{"Id":"22222222-2222-2222-2222-222222222222","IdPrefix":"22222","CreatedAt":"2024-01-02T03:04:05","ParentId":"11111111-1111-1111-1111-111111111111","Name":"Example.mkv","FileSize":1234,"Type":3,"Path":"/content/movies/Example.mkv","ReleaseDate":"2023-11-14T22:13:20+00:00","LastHealthCheck":"2023-11-14T22:13:20+00:00","NextHealthCheck":null,"YencPartSize":null,"YencLastPartSize":null,"YencSegmentCount":null,"YencLayoutUniform":null},{"Id":"44444444-4444-4444-4444-444444444444","IdPrefix":"44444","CreatedAt":"2024-01-02T03:04:05","ParentId":"11111111-1111-1111-1111-111111111111","Name":"Multi.mkv","FileSize":9012,"Type":6,"Path":"/content/movies/Multi.mkv","ReleaseDate":null,"LastHealthCheck":null,"NextHealthCheck":null,"YencPartSize":null,"YencLastPartSize":null,"YencSegmentCount":null,"YencLayoutUniform":null},{"Id":"33333333-3333-3333-3333-333333333333","IdPrefix":"33333","CreatedAt":"2024-01-02T03:04:05","ParentId":"11111111-1111-1111-1111-111111111111","Name":"Rar.mkv","FileSize":5678,"Type":4,"Path":"/content/movies/Rar.mkv","ReleaseDate":null,"LastHealthCheck":null,"NextHealthCheck":null,"YencPartSize":null,"YencLastPartSize":null,"YencSegmentCount":null,"YencLayoutUniform":null}],"NzbFiles":[{"Id":"22222222-2222-2222-2222-222222222222","SegmentIds":["seg-1","seg-2"],"DavItem":null}],"RarFiles":[{"Id":"33333333-3333-3333-3333-333333333333","RarParts":[{"SegmentIds":["rar-1"],"PartSize":10,"Offset":0,"ByteCount":10}],"DavItem":null}],"MultipartFiles":[{"Id":"44444444-4444-4444-4444-444444444444","Metadata":{"AesParams":null,"FileParts":[{"SegmentIds":["multi-1"],"SegmentIdByteRange":{"StartInclusive":0,"EndExclusive":10,"Count":10},"FilePartByteRange":{"StartInclusive":0,"EndExclusive":10,"Count":10}}]},"DavItem":null}]}
        """;

    // ---------------------------------------------------------------- format --

    [Fact]
    public async Task WriteAsync_RoundTripsEveryRow()
    {
        await _fixture.ResetAsync();
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            SeedSampleLibrary(dbContext);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        var read = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);
        Assert.NotNull(read.Summary);
        Assert.Equal(ContentIndexSnapshotStore.CurrentVersion, read.Summary!.Version);
        Assert.Equal(4, read.Summary.ItemCount);
        Assert.Equal(1, read.Summary.NzbFileCount);
        Assert.Equal(1, read.Summary.RarFileCount);
        Assert.Equal(1, read.Summary.MultipartFileCount);
        Assert.Equal(DavItem.ItemType.NzbFile, read.Summary.ItemsById[NzbItemId].Type);
        Assert.Equal(DirectoryId, read.Summary.ItemsById[NzbItemId].ParentId);

        var items = await ReadAllAsync(ContentIndexSnapshotStore.ReadItemsAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(
            ["/content/movies", "/content/movies/Example.mkv", "/content/movies/Multi.mkv", "/content/movies/Rar.mkv"],
            items.Select(x => x.Path).Order(StringComparer.Ordinal)
        );
        var nzbItem = items.Single(x => x.Id == NzbItemId);
        Assert.Equal("Example.mkv", nzbItem.Name);
        Assert.Equal(1234, nzbItem.FileSize);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), nzbItem.ReleaseDate);

        var nzbFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadNzbFilesAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(["seg-1", "seg-2"], Assert.Single(nzbFiles).SegmentIds);

        var rarFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadRarFilesAsync(read.SourcePath!, CancellationToken.None));
        var rarPart = Assert.Single(Assert.Single(rarFiles).RarParts);
        Assert.Equal(["rar-1"], rarPart.SegmentIds);
        Assert.Equal(10, rarPart.PartSize);

        var multipartFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadMultipartFilesAsync(read.SourcePath!, CancellationToken.None));
        var filePart = Assert.Single(Assert.Single(multipartFiles).Metadata.FileParts);
        Assert.Equal(["multi-1"], filePart.SegmentIds);
    }

    /// <summary>
    /// Snapshots written before the streaming rewrite must still load, or every
    /// existing deployment loses its recovery artifact on upgrade.
    /// </summary>
    [Fact]
    public async Task ReadAsync_LoadsLegacyFormatSnapshot()
    {
        await _fixture.ResetAsync();
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
        await File.WriteAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath, LegacySnapshotJson);

        var read = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);

        Assert.Empty(read.Warnings);
        Assert.NotNull(read.Summary);
        Assert.Equal(1, read.Summary!.Version);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), read.Summary.GeneratedAtUtc);
        Assert.Equal(4, read.Summary.ItemCount);
        Assert.Equal(DavItem.ItemType.Directory, read.Summary.ItemsById[DirectoryId].Type);
        Assert.Equal(DavItem.ContentFolder.Id, read.Summary.ItemsById[DirectoryId].ParentId);
        Assert.Equal(1, read.Summary.NzbFileCount);
        Assert.Equal(1, read.Summary.RarFileCount);
        Assert.Equal(1, read.Summary.MultipartFileCount);

        var items = await ReadAllAsync(ContentIndexSnapshotStore.ReadItemsAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(4, items.Count);
        Assert.Equal("/content/movies/Example.mkv", items.Single(x => x.Id == NzbItemId).Path);
        Assert.Equal("22222", items.Single(x => x.Id == NzbItemId).IdPrefix);

        var nzbFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadNzbFilesAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(["seg-1", "seg-2"], Assert.Single(nzbFiles).SegmentIds);
        var rarFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadRarFilesAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(["rar-1"], Assert.Single(Assert.Single(rarFiles).RarParts).SegmentIds);
        var multipartFiles = await ReadAllAsync(ContentIndexSnapshotStore.ReadMultipartFilesAsync(read.SourcePath!, CancellationToken.None));
        Assert.Equal(["multi-1"], Assert.Single(Assert.Single(multipartFiles).Metadata.FileParts).SegmentIds);
    }

    /// <summary>
    /// The other half of format compatibility: a snapshot written by the streaming
    /// writer must still deserialize as one whole legacy document.
    /// </summary>
    [Fact]
    public async Task WriteAsync_ProducesJsonThatTheLegacyReaderAccepts()
    {
        await _fixture.ResetAsync();
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            SeedSampleLibrary(dbContext);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        var json = await File.ReadAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath);
        var legacy = JsonSerializer.Deserialize<LegacySnapshot>(json, new JsonSerializerOptions { WriteIndented = false });

        Assert.NotNull(legacy);
        Assert.Equal(1, legacy!.Version);
        Assert.NotEqual(default, legacy.GeneratedAtUtc);
        Assert.Equal(4, legacy.Items.Count);
        Assert.Equal(["seg-1", "seg-2"], Assert.Single(legacy.NzbFiles).SegmentIds);
        Assert.Equal(["rar-1"], Assert.Single(Assert.Single(legacy.RarFiles).RarParts).SegmentIds);
        Assert.Equal(["multi-1"], Assert.Single(Assert.Single(legacy.MultipartFiles).Metadata.FileParts).SegmentIds);

        // Legacy writers emitted the properties in this order; keep it stable so
        // the file stays diffable and any hand-written parser keeps working.
        Assert.StartsWith("{\"Version\":1,\"GeneratedAtUtc\":", json, StringComparison.Ordinal);
        Assert.Contains("\"Items\":[", json, StringComparison.Ordinal);
        Assert.EndsWith("]}", json, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------ validation --

    [Theory]
    [InlineData("version", "unsupported version 2")]
    [InlineData("duplicate-item", "duplicate DavItem id 11111111-1111-1111-1111-111111111111")]
    [InlineData("non-content-path", "has non-content path '/nzbs/orphan'")]
    [InlineData("missing-parent", "references missing parent")]
    [InlineData("missing-metadata", "is missing required metadata")]
    [InlineData("duplicate-nzb", "duplicate DavNzbFile ids")]
    [InlineData("orphan-nzb", "snapshot contains DavNzbFile rows without matching NzbFile items")]
    public async Task ReadAsync_RejectsMalformedSnapshots(string scenario, string expectedWarning)
    {
        await _fixture.ResetAsync();
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
        await File.WriteAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath, MalformedSnapshotJson(scenario));

        var read = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);

        Assert.Null(read.Summary);
        Assert.Contains(expectedWarning, Assert.Single(read.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_FallsBackToTheBackupSnapshot()
    {
        await _fixture.ResetAsync();
        Directory.CreateDirectory(DavDatabaseContext.ConfigPath);
        await File.WriteAllTextAsync(ContentIndexSnapshotStore.SnapshotFilePath, "{\"Version\":");
        await File.WriteAllTextAsync(ContentIndexSnapshotStore.BackupSnapshotFilePath, LegacySnapshotJson);

        var read = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);

        Assert.NotNull(read.Summary);
        Assert.Equal(ContentIndexSnapshotStore.BackupSnapshotFilePath, read.SourcePath);
        Assert.Single(read.Warnings);
    }

    // --------------------------------------------------------- bounded memory --

    /// <summary>
    /// Guards defect (A): the writer used to ToList() every /content item and every
    /// SegmentIds array before serializing. Both assertions below fail if it goes
    /// back to that -- the page count collapses to one query per table, and a single
    /// reader hands back the entire table.
    /// </summary>
    [Fact]
    public async Task WriteAsync_PagesTheDatabase_InsteadOfMaterializingEveryRow()
    {
        const int itemCount = 45;
        const int pageSize = 10;

        await _fixture.ResetAsync();
        await using var seedContext = await _fixture.CreateMigratedContextAsync();
        SeedManyNzbFiles(seedContext, itemCount);
        await seedContext.SaveChangesAsync();

        var spy = new CommandSpy();
        await using var spiedContext = CreateSpiedContext(spy);
        await using var output = new MemoryStream();
        spy.Reset();
        await ContentIndexSnapshotStore.WriteSnapshotAsync(
            spiedContext, output, pageSize, pageSize, CancellationToken.None, pageSize);

        // Never more than one page resident at a time, on any of the four queries.
        // (ReadCount includes the final Read() that reports end-of-results.)
        Assert.True(
            spy.MaxRowsPerReader <= pageSize + 1,
            $"A single query returned {spy.MaxRowsPerReader} rows; the page size is {pageSize}."
        );

        // 45 items at 10 per page: 5 pages, the last one short.
        Assert.Equal(5, spy.ItemOnlyQueryCount);

        // ...and the file rows page through the same cursor rather than a single
        // Contains() over every content id: five cursor pages, each followed by one
        // chunked fetch of the rows it named.
        Assert.Equal(10, spy.NzbFileQueryCount);
        Assert.Equal(itemCount + 1, JsonDocument.Parse(output.ToArray()).RootElement.GetProperty("Items").GetArrayLength());
    }

    /// <summary>
    /// Guards defect (C): the reader used to DeserializeAsync the whole document.
    /// Consuming one item must not pull the whole file through the stream.
    /// </summary>
    [Fact]
    public async Task ReadItemsAsync_DoesNotReadTheWholeDocumentToYieldTheFirstItem()
    {
        await _fixture.ResetAsync();
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            SeedManyNzbFiles(dbContext, 3000);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        var snapshotBytes = new FileInfo(ContentIndexSnapshotStore.SnapshotFilePath).Length;
        Assert.True(snapshotBytes > 512 * 1024, $"Snapshot fixture is only {snapshotBytes} bytes.");

        await using var fileStream = File.OpenRead(ContentIndexSnapshotStore.SnapshotFilePath);
        await using var counting = new CountingStream(fileStream);
        var enumerator = ContentIndexSnapshotStore
            .ReadArrayAsync<DavItem>(counting, "Items", CancellationToken.None)
            .GetAsyncEnumerator(CancellationToken.None);
        try
        {
            Assert.True(await enumerator.MoveNextAsync());
            Assert.StartsWith("/content/", enumerator.Current.Path, StringComparison.Ordinal);
        }
        finally
        {
            await enumerator.DisposeAsync();
        }

        Assert.True(
            counting.BytesRead <= 128 * 1024,
            $"Read {counting.BytesRead} bytes of a {snapshotBytes} byte snapshot to yield one item."
        );
    }

    /// <summary>
    /// Guards defect (C) on the recovery side: streaming the read is pointless if
    /// recovery then buffers every row into one change tracker and one SaveChanges.
    /// </summary>
    [Fact]
    public async Task Recovery_AppliesRestoreInBoundedBatches()
    {
        const int itemCount = 200;
        const int itemBatchSize = 25;
        const int fileBatchSize = 10;

        await _fixture.ResetAsync();
        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            SeedManyNzbFiles(dbContext, itemCount);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        await _fixture.RecreateDatabaseAsync();
        var read = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);
        Assert.NotNull(read.Summary);

        var spy = new CommandSpy();
        var saveSpy = new SaveSpy();
        await using var dbContext2 = CreateSpiedContext(spy, saveSpy);
        var plan = await ContentIndexRecoveryService.BuildRecoveryPlanAsync(
            dbContext2, read.Summary!, new ConfigManager(), CancellationToken.None);
        Assert.True(plan.RestoreAllContentItems);

        spy.Reset();
        saveSpy.Reset();
        await ContentIndexRecoveryService.RestoreAsync(
            dbContext2, read.SourcePath!, plan, itemBatchSize, fileBatchSize, CancellationToken.None);

        // 201 items (the parent directory included) at 25 per batch, plus 200 file
        // rows at 10 per batch: nothing may accumulate into one giant insert.
        var expectedSaves = (int)Math.Ceiling((itemCount + 1) / (double)itemBatchSize)
                            + (int)Math.Ceiling(itemCount / (double)fileBatchSize);
        Assert.Equal(expectedSaves, saveSpy.SaveCount);
        Assert.True(spy.MaxInsertRowsPerCommand <= itemBatchSize, $"One insert carried {spy.MaxInsertRowsPerCommand} rows.");

        await using var verify = await _fixture.CreateMigratedContextAsync();
        Assert.Equal(itemCount + 1, await verify.Items.CountAsync(x => x.Path.StartsWith("/content/")));
        Assert.Equal(itemCount, await verify.NzbFiles.CountAsync());
    }

    // --------------------------------------------------------------- chunking --

    /// <summary>
    /// Guards defect (B). EF turns Contains() into one SQL variable per id and
    /// SQLite caps that at 32766, so an unchunked lookup over a library this size
    /// dies with "too many SQL variables" before it restores anything.
    /// </summary>
    [Fact]
    public async Task Recovery_RestoresMoreFileRowsThanTheSqlVariableLimit()
    {
        const int itemCount = 40000;

        await _fixture.ResetAsync();
        ContentIndexSnapshotInterceptor.SetDebounceInterval(TimeSpan.FromMinutes(10));
        try
        {
            await using (var dbContext = await _fixture.CreateMigratedContextAsync())
            {
                await SeedManyNzbFilesInBatchesAsync(dbContext, itemCount);
                await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);

                // Keep every DavItem, drop every metadata row: recovery must now plan
                // and restore across 40000 ids, well past the variable cap.
                await dbContext.NzbFiles.ExecuteDeleteAsync();
            }

            var recoveryService = new ContentIndexRecoveryService(new ConfigManager());
            await recoveryService.RecoverAsync(500, 500, CancellationToken.None);

            await using var verify = await _fixture.CreateMigratedContextAsync();
            Assert.Equal(itemCount, await verify.NzbFiles.CountAsync());
            Assert.Equal(itemCount + 1, await verify.Items.CountAsync(x => x.Path.StartsWith("/content/")));
            var probe = await verify.NzbFiles.SingleAsync(x => x.Id == ItemIdForIndex(itemCount - 1));
            Assert.Equal([$"segment-{itemCount - 1}"], probe.SegmentIds);
        }
        finally
        {
            ContentIndexSnapshotInterceptor.SetDebounceInterval(TimeSpan.FromSeconds(5));
        }
    }

    // ---------------------------------------------------------------- helpers --

    private static void SeedSampleLibrary(DavDatabaseContext dbContext)
    {
        var created = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var release = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        dbContext.Items.AddRange(
            new DavItem
            {
                Id = DirectoryId, IdPrefix = "11111", CreatedAt = created, ParentId = DavItem.ContentFolder.Id,
                Name = "movies", Type = DavItem.ItemType.Directory, Path = "/content/movies",
            },
            new DavItem
            {
                Id = NzbItemId, IdPrefix = "22222", CreatedAt = created, ParentId = DirectoryId,
                Name = "Example.mkv", FileSize = 1234, Type = DavItem.ItemType.NzbFile,
                Path = "/content/movies/Example.mkv", ReleaseDate = release, LastHealthCheck = release,
            },
            new DavItem
            {
                Id = RarItemId, IdPrefix = "33333", CreatedAt = created, ParentId = DirectoryId,
                Name = "Rar.mkv", FileSize = 5678, Type = DavItem.ItemType.RarFile, Path = "/content/movies/Rar.mkv",
            },
            new DavItem
            {
                Id = MultipartItemId, IdPrefix = "44444", CreatedAt = created, ParentId = DirectoryId,
                Name = "Multi.mkv", FileSize = 9012, Type = DavItem.ItemType.MultipartFile,
                Path = "/content/movies/Multi.mkv",
            }
        );
        dbContext.NzbFiles.Add(new DavNzbFile { Id = NzbItemId, SegmentIds = ["seg-1", "seg-2"] });
        dbContext.RarFiles.Add(new DavRarFile
        {
            Id = RarItemId,
            RarParts = [new DavRarFile.RarPart { SegmentIds = ["rar-1"], PartSize = 10, Offset = 0, ByteCount = 10 }],
        });
        dbContext.MultipartFiles.Add(new DavMultipartFile
        {
            Id = MultipartItemId,
            Metadata = new DavMultipartFile.Meta
            {
                FileParts =
                [
                    new DavMultipartFile.FilePart
                    {
                        SegmentIds = ["multi-1"],
                        SegmentIdByteRange = NzbWebDAV.Models.LongRange.FromStartAndSize(0, 10),
                        FilePartByteRange = NzbWebDAV.Models.LongRange.FromStartAndSize(0, 10),
                    }
                ]
            },
        });
    }

    private static Guid ItemIdForIndex(int index)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), index + 1);
        return new Guid(bytes);
    }

    private static void SeedManyNzbFiles(DavDatabaseContext dbContext, int count, int startIndex = 0)
    {
        if (startIndex == 0)
        {
            dbContext.Items.Add(new DavItem
            {
                Id = DirectoryId,
                IdPrefix = "11111",
                CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ParentId = DavItem.ContentFolder.Id,
                Name = "movies",
                Type = DavItem.ItemType.Directory,
                Path = "/content/movies",
            });
        }

        for (var index = startIndex; index < startIndex + count; index++)
        {
            var id = ItemIdForIndex(index);
            dbContext.Items.Add(new DavItem
            {
                Id = id,
                IdPrefix = id.ToString()[..5],
                CreatedAt = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                ParentId = DirectoryId,
                Name = $"Movie-{index:D6}.mkv",
                FileSize = 1024,
                Type = DavItem.ItemType.NzbFile,
                Path = $"/content/movies/Movie-{index:D6}.mkv",
            });
            dbContext.NzbFiles.Add(new DavNzbFile { Id = id, SegmentIds = [$"segment-{index}"] });
        }
    }

    private static async Task SeedManyNzbFilesInBatchesAsync(DavDatabaseContext dbContext, int count)
    {
        const int batchSize = 5000;
        for (var offset = 0; offset < count; offset += batchSize)
        {
            SeedManyNzbFiles(dbContext, Math.Min(batchSize, count - offset), offset);
            await dbContext.SaveChangesAsync();
            dbContext.ChangeTracker.Clear();
        }
    }

    private static DavDatabaseContext CreateSpiedContext(CommandSpy spy, SaveSpy? saveSpy = null)
    {
        var interceptors = saveSpy == null
            ? new IInterceptor[] { new SqliteForeignKeyEnabler(), spy }
            : [new SqliteForeignKeyEnabler(), spy, saveSpy];
        var options = new DbContextOptionsBuilder<DavDatabaseContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}", x => x.MaxBatchSize(50))
            .AddInterceptors(interceptors)
            .Options;
        return new DavDatabaseContext(options);
    }

    private static async Task<List<T>> ReadAllAsync<T>(IAsyncEnumerable<T> rows)
    {
        var result = new List<T>();
        await foreach (var row in rows) result.Add(row);
        return result;
    }

    private static string MalformedSnapshotJson(string scenario)
    {
        var directory = ItemJson(DirectoryId, DavItem.ContentFolder.Id, "/content/movies", DavItem.ItemType.Directory);
        var nzbItem = ItemJson(NzbItemId, DirectoryId, "/content/movies/Example.mkv", DavItem.ItemType.NzbFile);
        var nzbRow = $$"""{"Id":"{{NzbItemId}}","SegmentIds":["seg-1"],"DavItem":null}""";

        return scenario switch
        {
            "version" => Snapshot(2, [directory], []),
            "duplicate-item" => Snapshot(1, [directory, directory], []),
            "non-content-path" => Snapshot(
                1,
                [directory, ItemJson(NzbItemId, DirectoryId, "/nzbs/orphan", DavItem.ItemType.Directory)],
                []
            ),
            "missing-parent" => Snapshot(
                1,
                [ItemJson(NzbItemId, Guid.Parse("99999999-9999-9999-9999-999999999999"), "/content/movies/x.mkv", DavItem.ItemType.Directory)],
                []
            ),
            "missing-metadata" => Snapshot(1, [directory, nzbItem], []),
            "duplicate-nzb" => Snapshot(1, [directory, nzbItem], [nzbRow, nzbRow]),
            "orphan-nzb" => Snapshot(1, [directory], [nzbRow]),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, null)
        };
    }

    private static string Snapshot(int version, string[] items, string[] nzbFiles)
    {
        return $$"""
                 {"Version":{{version}},"GeneratedAtUtc":"2023-11-14T22:13:20+00:00","Items":[{{string.Join(",", items)}}],"NzbFiles":[{{string.Join(",", nzbFiles)}}],"RarFiles":[],"MultipartFiles":[]}
                 """;
    }

    private static string ItemJson(Guid id, Guid parentId, string path, DavItem.ItemType type)
    {
        return $$"""
                 {"Id":"{{id}}","IdPrefix":"{{id.ToString()[..5]}}","CreatedAt":"2024-01-02T03:04:05","ParentId":"{{parentId}}","Name":"{{path[(path.LastIndexOf('/') + 1)..]}}","FileSize":null,"Type":{{(int)type}},"Path":"{{path}}","ReleaseDate":null,"LastHealthCheck":null,"NextHealthCheck":null,"YencPartSize":null,"YencLastPartSize":null,"YencSegmentCount":null,"YencLayoutUniform":null}
                 """;
    }

    private sealed class LegacySnapshot
    {
        public int Version { get; init; }
        public DateTimeOffset GeneratedAtUtc { get; init; }
        public List<DavItem> Items { get; init; } = [];
        public List<DavNzbFile> NzbFiles { get; init; } = [];
        public List<DavRarFile> RarFiles { get; init; } = [];
        public List<DavMultipartFile> MultipartFiles { get; init; } = [];
    }

    private sealed class CountingStream(Stream inner) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            BytesRead += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            return read;
        }

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Records how much data each query actually returned.</summary>
    private sealed class CommandSpy : DbCommandInterceptor
    {
        private readonly object _lock = new();

        public int MaxRowsPerReader { get; private set; }
        public int ItemOnlyQueryCount { get; private set; }
        public int NzbFileQueryCount { get; private set; }
        public int MaxInsertRowsPerCommand { get; private set; }

        public void Reset()
        {
            lock (_lock)
            {
                MaxRowsPerReader = 0;
                ItemOnlyQueryCount = 0;
                NzbFileQueryCount = 0;
                MaxInsertRowsPerCommand = 0;
            }
        }

        public override InterceptionResult DataReaderDisposing(
            DbCommand command,
            DataReaderDisposingEventData eventData,
            InterceptionResult result)
        {
            lock (_lock)
            {
                MaxRowsPerReader = Math.Max(MaxRowsPerReader, eventData.ReadCount);
                var text = command.CommandText;
                if (text.Contains("\"DavItems\"", StringComparison.Ordinal)
                    && !text.Contains("\"DavNzbFiles\"", StringComparison.Ordinal)
                    && !text.Contains("\"DavRarFiles\"", StringComparison.Ordinal)
                    && !text.Contains("\"DavMultipartFiles\"", StringComparison.Ordinal))
                    ItemOnlyQueryCount++;
                if (text.Contains("\"DavNzbFiles\"", StringComparison.Ordinal))
                    NzbFileQueryCount++;
            }

            return base.DataReaderDisposing(command, eventData, result);
        }

        public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
        {
            RecordWrite(command);
            return base.NonQueryExecuted(command, eventData, result);
        }

        public override ValueTask<int> NonQueryExecutedAsync(
            DbCommand command,
            CommandExecutedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            RecordWrite(command);
            return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
        }

        private void RecordWrite(DbCommand command)
        {
            if (!command.CommandText.Contains("INSERT INTO", StringComparison.Ordinal)) return;
            lock (_lock)
            {
                MaxInsertRowsPerCommand = Math.Max(MaxInsertRowsPerCommand, CountValueTuples(command.CommandText));
            }
        }

        private static int CountValueTuples(string commandText)
        {
            // EF batches multi-row inserts as one statement per 50 rows; count the
            // parameter tuples so an unbatched restore is visible.
            var count = 0;
            foreach (var line in commandText.Split('\n'))
            {
                if (line.Contains("INSERT INTO", StringComparison.Ordinal)) count++;
            }

            return Math.Max(count, 1);
        }
    }

    /// <summary>Counts how many times recovery committed.</summary>
    private sealed class SaveSpy : SaveChangesInterceptor
    {
        private int _saveCount;

        public int SaveCount => Volatile.Read(ref _saveCount);

        public void Reset() => Interlocked.Exchange(ref _saveCount, 0);

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _saveCount);
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
