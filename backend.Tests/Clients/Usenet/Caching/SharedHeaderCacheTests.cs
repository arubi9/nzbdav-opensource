using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using Testcontainers.PostgreSql;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet.Caching;

[Collection(nameof(SharedHeaderCacheCollection))]
public sealed class SharedHeaderCacheTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SharedHeaderCacheTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullOnMissingRow()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        var cache = new SharedHeaderCache();

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(header);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsPopulatedHeaderOnHit()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        var cache = new SharedHeaderCache();
        await cache.WriteAsync("segment-a", CreateHeader("a.bin", 0), CancellationToken.None);

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal("a.bin", header!.FileName);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public async Task WriteAsync_UpsertsExistingRow()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        var cache = new SharedHeaderCache();
        await cache.WriteAsync("segment-a", CreateHeader("first.bin", 0), CancellationToken.None);
        await cache.WriteAsync("segment-a", CreateHeader("second.bin", 1024), CancellationToken.None);

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal("second.bin", header!.FileName);
        Assert.Equal(1024, header.PartOffset);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullAndCountsMiss_OnTransientError()
    {
        var cache = new SharedHeaderCache(() => throw new InvalidOperationException("db down"));

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(header);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public async Task WriteAsync_SwallowsTransientError_AndCountsFailure()
    {
        var cache = new SharedHeaderCache(() => throw new InvalidOperationException("db down"));

        await cache.WriteAsync("segment-a", CreateHeader("a.bin", 0), CancellationToken.None);

        Assert.Equal(1, cache.WriteFailures);
    }

    private static UsenetYencHeader CreateHeader(string fileName, long partOffset)
    {
        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = partOffset + 100,
            LineLength = 128,
            PartNumber = 1,
            TotalParts = 1,
            PartSize = 100,
            PartOffset = partOffset
        };
    }
}

public sealed class PostgresHeaderCacheFixture : IAsyncLifetime
{
    private readonly string? _previousDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
    private readonly PostgreSqlContainer? _container;
    private readonly bool _isManagedDatabase;
    private bool _isAvailable;

    public PostgresHeaderCacheFixture()
    {
        _isAvailable = false;
        _isManagedDatabase = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DATABASE_URL"));
        if (_isManagedDatabase)
        {
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithName($"nzbdav-header-cache-{Guid.NewGuid():N}")
                .WithDatabase("nzbdav")
                .WithUsername("nzbdav")
                .WithPassword("nzbdav")
                .WithCleanUp(true)
                .Build();
        }
    }

    public bool IsAvailable => _isAvailable;
    public string? ConnectionString => Environment.GetEnvironmentVariable("DATABASE_URL");

    public async ValueTask InitializeAsync()
    {
        var explicitConnectionString = Environment.GetEnvironmentVariable("DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(explicitConnectionString))
        {
            _isAvailable = true;
            await using var dbContext = new DavDatabaseContext();
            await DatabaseInitialization.InitializeAsync(dbContext, CancellationToken.None);
            return;
        }

        _isAvailable = await DockerAvailableAsync();
        if (!_isAvailable || _container == null)
            return;

        await _container.StartAsync();
        Environment.SetEnvironmentVariable("DATABASE_URL", _container.GetConnectionString());

        await using var managedContainerDbContext = new DavDatabaseContext();
        await DatabaseInitialization.InitializeAsync(managedContainerDbContext, CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", _previousDatabaseUrl);
        if (_container != null)
            await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        if (!IsAvailable || !_isManagedDatabase)
            return;

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.ExecuteSqlRawAsync(@"
            DO
            $$
            DECLARE
                r RECORD;
            BEGIN
                FOR r IN
                    SELECT tablename
                    FROM pg_tables
                    WHERE schemaname = 'public'
                      AND tablename <> '__EFMigrationsHistory'
                LOOP
                    EXECUTE format('TRUNCATE TABLE %I.%I RESTART IDENTITY CASCADE', 'public', r.tablename);
                END LOOP;
            END;
            $$;");
    }

    private static async Task<bool> DockerAvailableAsync()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "docker",
                Arguments = "version --format {{.Server.Version}}",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null)
                return false;

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(5000));
            await process.WaitForExitAsync(timeout.Token);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                return false;
            }

            await Task.WhenAll(outputTask, errorTask);
            if (process.ExitCode != 0)
                return false;

            return !string.IsNullOrWhiteSpace(outputTask.Result);
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition(nameof(SharedHeaderCacheCollection), DisableParallelization = true)]
public sealed class SharedHeaderCacheCollection : ICollectionFixture<backend.Tests.Config.ProcessEnvironmentFixture>;
