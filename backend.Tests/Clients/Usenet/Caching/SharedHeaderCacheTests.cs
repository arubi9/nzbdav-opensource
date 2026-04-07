using System.Diagnostics;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
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
        if (!_fixture.IsAvailable) return;

        await _fixture.ResetAsync();
        var cache = new SharedHeaderCache();

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(header);
        Assert.Equal(1, cache.Misses);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsPopulatedHeaderOnHit()
    {
        if (!_fixture.IsAvailable) return;

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
        if (!_fixture.IsAvailable) return;

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
    private readonly TestcontainersContainer? _container;

    public PostgresHeaderCacheFixture()
    {
        IsAvailable = DockerAvailable();
        if (!IsAvailable)
            return;

        _container = new TestcontainersBuilder<TestcontainersContainer>()
            .WithImage("postgres:17")
            .WithName($"nzbdav-header-cache-{Guid.NewGuid():N}")
            .WithPortBinding(5432, true)
            .WithEnvironment("POSTGRES_DB", "nzbdav")
            .WithEnvironment("POSTGRES_USER", "nzbdav")
            .WithEnvironment("POSTGRES_PASSWORD", "nzbdav")
            .WithCleanUp(true)
            .Build();
    }

    public bool IsAvailable { get; }
    public string? ConnectionString => Environment.GetEnvironmentVariable("DATABASE_URL");

    public async Task InitializeAsync()
    {
        if (!IsAvailable || _container == null)
            return;

        await _container.StartAsync();
        Environment.SetEnvironmentVariable(
            "DATABASE_URL",
            $"Host={_container.Hostname};Port={_container.GetMappedPublicPort(5432)};Database=nzbdav;Username=nzbdav;Password=nzbdav");

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", _previousDatabaseUrl);
        if (_container != null)
            await _container.DisposeAsync();
    }

    public async Task ResetAsync()
    {
        if (!IsAvailable)
            return;

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.ExecuteSqlRawAsync(@"
            DELETE FROM websocket_outbox;
            DELETE FROM auth_failures;
            DELETE FROM connection_pool_claims;
            DELETE FROM yenc_header_cache;");
    }

    private static bool DockerAvailable()
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

            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}

[CollectionDefinition(nameof(SharedHeaderCacheCollection), DisableParallelization = true)]
public sealed class SharedHeaderCacheCollection;
