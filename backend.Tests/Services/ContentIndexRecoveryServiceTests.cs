using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Services;

[Collection(nameof(ContentIndexDatabaseCollection))]
public sealed class ContentIndexRecoveryServiceTests
{
    private readonly ContentIndexDatabaseFixture _fixture;

    public ContentIndexRecoveryServiceTests(ContentIndexDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task StartupRecovery_RestoresContentItems_WhenDatabaseComesUpEmpty()
    {
        await _fixture.ResetAsync();
        var expectedItemId = Guid.NewGuid();

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var category = DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "movies",
                null,
                DavItem.ItemType.Directory,
                null,
                null
            );
            var file = DavItem.New(
                expectedItemId,
                category,
                "Example.mkv",
                1234,
                DavItem.ItemType.NzbFile,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow
            );

            dbContext.Items.Add(category);
            dbContext.Items.Add(file);
            dbContext.NzbFiles.Add(new DavNzbFile
            {
                Id = file.Id,
                SegmentIds = ["segment-1", "segment-2"],
            });
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        var snapshotReadResult = await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None);
        var snapshot = snapshotReadResult.Snapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Items.Count);
        Assert.Single(snapshot.NzbFiles);

        await _fixture.RecreateDatabaseAsync();

        var recoveryService = new ContentIndexRecoveryService(new ConfigManager());
        await recoveryService.StartAsync(CancellationToken.None);

        await using var restoredContext = await _fixture.CreateMigratedContextAsync();
        var restoredItems = await restoredContext.Items
            .Where(x => x.Path.StartsWith("/content/"))
            .OrderBy(x => x.Path)
            .ToListAsync();
        var restoredFile = await restoredContext.NzbFiles.SingleOrDefaultAsync(x => x.Id == expectedItemId);

        Assert.Equal(["/content/movies", "/content/movies/Example.mkv"], restoredItems.Select(x => x.Path));
        Assert.NotNull(restoredFile);
        Assert.Equal(["segment-1", "segment-2"], restoredFile!.SegmentIds);
    }

    [Fact]
    public async Task StartupRecovery_DoesNothing_WhenContentAlreadyExists()
    {
        await _fixture.ResetAsync();

        await using (var dbContext = await _fixture.CreateMigratedContextAsync())
        {
            var category = DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "movies",
                null,
                DavItem.ItemType.Directory,
                null,
                null
            );
            dbContext.Items.Add(category);
            await dbContext.SaveChangesAsync();
            await ContentIndexSnapshotStore.WriteAsync(dbContext, CancellationToken.None);
        }

        var recoveryService = new ContentIndexRecoveryService(new ConfigManager());
        await recoveryService.StartAsync(CancellationToken.None);

        await using var verifyContext = await _fixture.CreateMigratedContextAsync();
        var contentItems = await verifyContext.Items
            .Where(x => x.Path.StartsWith("/content/"))
            .ToListAsync();

        Assert.Single(contentItems);
        Assert.Equal("/content/movies", contentItems[0].Path);
    }

    [Fact]
    public async Task SnapshotInterceptor_PersistsCommittedContent_WhenSaveOutlastsDebounceWindow()
    {
        await _fixture.ResetAsync();
        await _fixture.RecreateDatabaseAsync();

        var originalDebounce = 5;
        GetSnapshotWriter()
            .SetDebounceInterval(TimeSpan.FromMilliseconds(50));

        var saveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using var dbContext = _fixture.CreateDelayedSaveContext(
                onSavingChangesAsync: () =>
                {
                    saveStarted.TrySetResult();
                    return allowCommit.Task;
                });

            var category = DavItem.New(
                Guid.NewGuid(),
                DavItem.ContentFolder,
                "movies",
                null,
                DavItem.ItemType.Directory,
                null,
                null
            );

            dbContext.Items.Add(category);
            var saveTask = dbContext.SaveChangesAsync();

            await saveStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitForConditionAsync(() => File.Exists(ContentIndexSnapshotStore.SnapshotFilePath));

            allowCommit.TrySetResult();
            await saveTask;
            await Task.Delay(150);

            var snapshot = (await ContentIndexSnapshotStore.ReadAsync(CancellationToken.None)).Snapshot;
            Assert.NotNull(snapshot);
            Assert.Contains(snapshot!.Items, x => x.Id == category.Id);
        }
        finally
        {
            GetSnapshotWriter()
                .SetDebounceInterval(TimeSpan.FromSeconds(originalDebounce));
        }
    }

    private static DebouncedSnapshotWriter GetSnapshotWriter()
    {
        var field = typeof(ContentIndexSnapshotInterceptor)
            .GetField("SnapshotWriter", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        return field?.GetValue(null) as DebouncedSnapshotWriter
               ?? throw new InvalidOperationException("Could not access ContentIndexSnapshotInterceptor.SnapshotWriter.");
    }

    private static async Task WaitForConditionAsync(Func<bool> condition)
    {
        var timeoutAt = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < timeoutAt)
        {
            if (condition()) return;
            await Task.Delay(25);
        }

        Assert.True(condition(), "Timed out waiting for condition.");
    }
}

public sealed class ContentIndexDatabaseFixture : IAsyncLifetime
{
    private readonly string _configPath = Path.Join(Path.GetTempPath(), "nzbdav-tests", "content-index-recovery");

    public ContentIndexDatabaseFixture()
    {
        Environment.SetEnvironmentVariable("CONFIG_PATH", _configPath);
    }

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_configPath);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        return ResetAsync();
    }

    public async Task ResetAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath);
        DeleteIfExists(ContentIndexSnapshotStore.SnapshotFilePath);
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-wal");
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    public async Task RecreateDatabaseAsync()
    {
        await ResetDatabaseFilesAsync();
        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        Directory.CreateDirectory(_configPath);
        var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
        return dbContext;
    }

    public DelayedSnapshotTestContext CreateDelayedSaveContext(Func<Task> onSavingChangesAsync)
    {
        Directory.CreateDirectory(_configPath);
        var options = new DbContextOptionsBuilder<DelayedSnapshotTestContext>()
            .UseSqlite($"Data Source={DavDatabaseContext.DatabaseFilePath}")
            .AddInterceptors(
                new SqliteForeignKeyEnabler(),
                new ContentIndexSnapshotInterceptor(),
                new DelaySaveChangesInterceptor(onSavingChangesAsync))
            .Options;
        return new DelayedSnapshotTestContext(options);
    }

    private async Task ResetDatabaseFilesAsync()
    {
        await Task.Yield();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath);
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-wal");
        DeleteIfExists(DavDatabaseContext.DatabaseFilePath + "-shm");
    }

    private static void DeleteIfExists(string path)
    {
        if (!File.Exists(path)) return;

        const int maxAttempts = 10;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (IOException)
            {
                if (attempt >= maxAttempts - 1) throw;
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt >= maxAttempts - 1) throw;
                Thread.Sleep(50);
            }
        }
    }
}

public sealed class DelayedSnapshotTestContext(DbContextOptions<DelayedSnapshotTestContext> options) : DbContext(options)
{
    public DbSet<DavItem> Items => Set<DavItem>();
    public DbSet<DavNzbFile> NzbFiles => Set<DavNzbFile>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<DavItem>(e =>
        {
            e.ToTable("DavItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Name)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.IdPrefix)
                .IsRequired();

            e.Property(i => i.ReleaseDate)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.LastHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.Property(i => i.NextHealthCheck)
                .ValueGeneratedNever()
                .HasConversion(
                    x => x.HasValue ? x.Value.ToUnixTimeSeconds() : (long?)null,
                    x => x.HasValue ? DateTimeOffset.FromUnixTimeSeconds(x.Value) : null
                );

            e.HasOne(i => i.Parent)
                .WithMany(p => p.Children)
                .HasForeignKey(i => i.ParentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DavNzbFile>(e =>
        {
            e.ToTable("DavNzbFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.SegmentIds)
                .HasConversion(new ValueConverter<string[], string>
                (
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<string[]>(v, (System.Text.Json.JsonSerializerOptions?)null)
                         ?? Array.Empty<string>()
                ))
                .HasColumnType("TEXT")
                .IsRequired();
        });
    }
}

public sealed class DelaySaveChangesInterceptor(Func<Task> onSavingChangesAsync) : SaveChangesInterceptor
{
    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        await onSavingChangesAsync().WaitAsync(cancellationToken);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }
}

[CollectionDefinition(nameof(ContentIndexDatabaseCollection), DisableParallelization = true)]
public sealed class ContentIndexDatabaseCollection : ICollectionFixture<ContentIndexDatabaseFixture>
{
}
