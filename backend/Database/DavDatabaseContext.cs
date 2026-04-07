using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseContext() : DbContext(CreateOptions())
{
    public static string ConfigPath => EnvironmentUtil.GetEnvironmentVariable("CONFIG_PATH") ?? "/config";
    public static string DatabaseFilePath => Path.Join(ConfigPath, "db.sqlite");

    private static DbContextOptions<DavDatabaseContext> CreateOptions()
    {
        var builder = new DbContextOptionsBuilder<DavDatabaseContext>();
        var databaseUrl = EnvironmentUtil.GetDatabaseUrl();

        if (!string.IsNullOrEmpty(databaseUrl))
        {
            var connectionString = BuildPostgresConnectionString(databaseUrl);
            builder.UseNpgsql(connectionString);
        }
        else
        {
            builder.UseSqlite($"Data Source={DatabaseFilePath}")
                .AddInterceptors(new SqliteForeignKeyEnabler());
        }

        // Only the ingest node owns the content-index snapshot file. Streaming
        // nodes read from the shared Postgres (or their own SQLite in combined
        // mode) as the source of truth — they have nothing useful to persist
        // to a local snapshot file. Registering the interceptor on a streaming
        // node would cost a disk write on every SaveChanges for no recovery
        // benefit, because the streaming node's local snapshot would lag
        // behind whatever the ingest node actually wrote.
        if (NodeRoleConfig.RunsIngest)
            builder.AddInterceptors(new ContentIndexSnapshotInterceptor());

        return builder.Options;
    }

    private static string BuildPostgresConnectionString(string databaseUrl)
    {
        var isUriStyle =
            databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
        var connectionString =
            isUriStyle
                ? ConvertPostgresUrl(databaseUrl)
                : databaseUrl;

        return UsesPgbouncer(databaseUrl, connectionString, isUriStyle)
            ? ApplyPgbouncerCompatibilityFlags(connectionString)
            : connectionString;
    }

    private static string ApplyPgbouncerCompatibilityFlags(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = true
        };

        if (builder.MinPoolSize <= 0)
            builder.MinPoolSize = 2;

        if (builder.MaxPoolSize <= 0)
            builder.MaxPoolSize = 50;

        var normalized = builder.ConnectionString;
        if (!normalized.Contains("No Reset On Close", StringComparison.OrdinalIgnoreCase))
            normalized += ";No Reset On Close=true";
        if (!normalized.Contains("Server Compatibility Mode", StringComparison.OrdinalIgnoreCase))
            normalized += ";Server Compatibility Mode=Redshift";

        return normalized;
    }

    private static string ConvertPostgresUrl(string url)
    {
        var uri = new Uri(url);
        var userInfo = uri.UserInfo.Split(':', 2);
        var username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : string.Empty;
        var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
        return $"Host={uri.Host};Port={uri.Port};Database={uri.AbsolutePath.TrimStart('/')};Username={username};Password={password};Pooling=true;MinPoolSize=2;MaxPoolSize=50";
    }

    private static bool UsesPgbouncer(string databaseUrl, string connectionString, bool isUriStyle)
    {
        if (isUriStyle)
            return new Uri(databaseUrl).Host.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return builder.Host.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase);
    }

    // database sets
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<DavItem> Items => Set<DavItem>();
    public DbSet<DavNzbFile> NzbFiles => Set<DavNzbFile>();
    public DbSet<DavRarFile> RarFiles => Set<DavRarFile>();
    public DbSet<DavMultipartFile> MultipartFiles => Set<DavMultipartFile>();
    public DbSet<QueueItem> QueueItems => Set<QueueItem>();
    public DbSet<HistoryItem> HistoryItems => Set<HistoryItem>();
    public DbSet<QueueNzbContents> QueueNzbContents => Set<QueueNzbContents>();
    public DbSet<HealthCheckResult> HealthCheckResults => Set<HealthCheckResult>();
    public DbSet<HealthCheckStat> HealthCheckStats => Set<HealthCheckStat>();
    public DbSet<ConfigItem> ConfigItems => Set<ConfigItem>();
    public DbSet<YencHeaderCacheEntry> YencHeaderCache => Set<YencHeaderCacheEntry>();
    public DbSet<WebsocketOutboxEntry> WebsocketOutbox => Set<WebsocketOutboxEntry>();
    public DbSet<AuthFailureEntry> AuthFailures => Set<AuthFailureEntry>();
    public DbSet<ConnectionPoolClaim> ConnectionPoolClaims => Set<ConnectionPoolClaim>();
    public DbSet<BlobCleanupItem> BlobCleanupItems => Set<BlobCleanupItem>();
    public DbSet<MissingSegmentId> MissingSegmentIds => Set<MissingSegmentId>();

    // tables
    protected override void OnModelCreating(ModelBuilder b)
    {
        // Account
        b.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(i => new { i.Type, i.Username });

            e.Property(i => i.Type)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Username)
                .IsRequired()
                .HasMaxLength(255);

            e.Property(i => i.PasswordHash)
                .IsRequired();

            e.Property(i => i.RandomSalt)
                .IsRequired();
        });

        // DavItem
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

            e.HasIndex(i => new { i.ParentId, i.Name })
                .IsUnique();

            e.HasIndex(i => new { i.IdPrefix, i.Type });

            e.HasIndex(i => new { i.Type, i.NextHealthCheck, i.ReleaseDate, i.Id });
        });

        // DavNzbFile
        b.Entity<DavNzbFile>(e =>
        {
            e.ToTable("DavNzbFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.SegmentIds)
                .HasConversion(new ValueConverter<string[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<string[]>(v, (JsonSerializerOptions?)null) ?? Array.Empty<string>()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavNzbFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavRarFile
        b.Entity<DavRarFile>(e =>
        {
            e.ToTable("DavRarFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.RarParts)
                .HasConversion(new ValueConverter<DavRarFile.RarPart[], string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<DavRarFile.RarPart[]>(v, (JsonSerializerOptions?)null)
                         ?? Array.Empty<DavRarFile.RarPart>()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavRarFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // DavMultipartFile
        b.Entity<DavMultipartFile>(e =>
        {
            e.ToTable("DavMultipartFiles");
            e.HasKey(f => f.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(f => f.Metadata)
                .HasConversion(new ValueConverter<DavMultipartFile.Meta, string>
                (
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => JsonSerializer.Deserialize<DavMultipartFile.Meta>(v, (JsonSerializerOptions?)null) ??
                         new DavMultipartFile.Meta()
                ))
                .HasColumnType("TEXT") // store raw JSON
                .IsRequired();

            e.HasOne(f => f.DavItem)
                .WithOne()
                .HasForeignKey<DavMultipartFile>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // QueueItem
        b.Entity<QueueItem>(e =>
        {
            e.ToTable("QueueItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.NzbFileSize)
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.Priority)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PostProcessing)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.PauseUntil)
                .ValueGeneratedNever();

            e.Property(i => i.JobName)
                .IsRequired();

            e.HasIndex(i => new { i.FileName })
                .IsUnique();

            e.HasIndex(i => new { i.Priority })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Priority, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.Priority, i.CreatedAt })
                .IsUnique(false);
        });

        // HistoryItem
        b.Entity<HistoryItem>(e =>
        {
            e.ToTable("HistoryItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.FileName)
                .IsRequired();

            e.Property(i => i.JobName)
                .IsRequired();

            e.Property(i => i.Category)
                .IsRequired();

            e.Property(i => i.DownloadStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.TotalSegmentBytes)
                .IsRequired();

            e.Property(i => i.DownloadTimeSeconds)
                .IsRequired();

            e.Property(i => i.FailMessage)
                .IsRequired(false);

            e.Property(i => i.DownloadDirId)
                .IsRequired(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.Category, i.DownloadDirId })
                .IsUnique(false);
        });

        // QueueNzbContents
        b.Entity<QueueNzbContents>(e =>
        {
            e.ToTable("QueueNzbContents");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();

            e.Property(i => i.NzbContents)
                .IsRequired();

            e.HasOne(f => f.QueueItem)
                .WithOne()
                .HasForeignKey<QueueNzbContents>(f => f.Id)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // HealthCheckResult
        b.Entity<HealthCheckResult>(e =>
        {
            e.ToTable("HealthCheckResults");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.CreatedAt)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DavItemId)
                .ValueGeneratedNever()
                .IsRequired();

            e.Property(i => i.Path)
                .IsRequired();

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Message)
                .IsRequired(false);

            e.HasIndex(i => new { i.Result, i.RepairStatus, i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(i => new { i.CreatedAt })
                .IsUnique(false);

            e.HasIndex(h => h.DavItemId)
                .HasFilter("\"RepairStatus\" = 3")
                .IsUnique(false);
        });

        // HealthCheckStats
        b.Entity<HealthCheckStat>(e =>
        {
            e.ToTable("HealthCheckStats");
            e.HasKey(i => new { i.DateStartInclusive, i.DateEndExclusive, i.Result, i.RepairStatus });

            e.Property(i => i.DateStartInclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.DateEndExclusive)
                .ValueGeneratedNever()
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );

            e.Property(i => i.Result)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.RepairStatus)
                .HasConversion<int>()
                .IsRequired();

            e.Property(i => i.Count);
        });

        // ConfigItem
        b.Entity<ConfigItem>(e =>
        {
            e.ToTable("ConfigItems");
            e.HasKey(i => i.ConfigName);
            e.Property(i => i.ConfigValue)
                .IsRequired();
            e.Property(i => i.IsEncrypted)
                .IsRequired()
                .HasDefaultValue(false);
        });

        b.Entity<YencHeaderCacheEntry>(e =>
        {
            e.ToTable("yenc_header_cache");
            e.HasKey(x => x.SegmentId);
            e.Property(x => x.SegmentId).HasColumnName("segment_id");
            e.Property(x => x.FileName).HasColumnName("file_name");
            e.Property(x => x.FileSize).HasColumnName("file_size");
            e.Property(x => x.LineLength).HasColumnName("line_length");
            e.Property(x => x.PartNumber).HasColumnName("part_number");
            e.Property(x => x.TotalParts).HasColumnName("total_parts");
            e.Property(x => x.PartSize).HasColumnName("part_size");
            e.Property(x => x.PartOffset).HasColumnName("part_offset");
            e.Property(x => x.CachedAt)
                .HasColumnName("cached_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.HasIndex(x => x.CachedAt).HasDatabaseName("ix_yenc_header_cache_cached_at");
        });

        b.Entity<WebsocketOutboxEntry>(e =>
        {
            e.ToTable("websocket_outbox");
            e.HasKey(x => x.Seq);
            e.Property(x => x.Seq)
                .HasColumnName("seq")
                .ValueGeneratedOnAdd();
            e.Property(x => x.Topic)
                .HasColumnName("topic")
                .IsRequired();
            e.Property(x => x.Payload)
                .HasColumnName("payload")
                .IsRequired();
            e.Property(x => x.CreatedAt)
                .HasColumnName("created_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.HasIndex(x => x.CreatedAt).HasDatabaseName("ix_websocket_outbox_created_at");
        });

        b.Entity<AuthFailureEntry>(e =>
        {
            e.ToTable("auth_failures");
            e.HasKey(x => x.IpAddress);
            e.Property(x => x.IpAddress)
                .HasColumnName("ip_address")
                .IsRequired();
            e.Property(x => x.FailureCount)
                .HasColumnName("failure_count")
                .IsRequired();
            e.Property(x => x.WindowStart)
                .HasColumnName("window_start")
                .IsRequired();
            e.HasIndex(x => x.WindowStart).HasDatabaseName("ix_auth_failures_window_start");
        });

        b.Entity<ConnectionPoolClaim>(e =>
        {
            e.ToTable("connection_pool_claims");
            e.HasKey(x => new { x.NodeId, x.ProviderIndex });
            e.Property(x => x.NodeId)
                .HasColumnName("node_id")
                .IsRequired();
            e.Property(x => x.ProviderIndex)
                .HasColumnName("provider_index")
                .IsRequired();
            e.Property(x => x.ClaimedSlots)
                .HasColumnName("claimed_slots")
                .IsRequired();
            e.Property(x => x.HeartbeatAt)
                .HasColumnName("heartbeat_at")
                .HasDefaultValueSql("CURRENT_TIMESTAMP");
            e.HasIndex(x => x.HeartbeatAt).HasDatabaseName("ix_connection_pool_claims_heartbeat_at");
        });

        // BlobCleanupItem
        b.Entity<BlobCleanupItem>(e =>
        {
            e.ToTable("BlobCleanupItems");
            e.HasKey(i => i.Id);

            e.Property(i => i.Id)
                .ValueGeneratedNever();
        });

        b.Entity<MissingSegmentId>(e =>
        {
            e.ToTable("MissingSegmentIds");
            e.HasKey(i => i.SegmentId);

            e.Property(i => i.SegmentId)
                .HasMaxLength(512)
                .IsRequired();

            e.Property(i => i.DetectedAt)
                .IsRequired()
                .HasConversion(
                    x => x.ToUnixTimeSeconds(),
                    x => DateTimeOffset.FromUnixTimeSeconds(x)
                );
        });
    }
}
