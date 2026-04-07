using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Database;
using UsenetSharp.Models;

namespace backend.Tests.Clients.Usenet.Caching;

[Collection(nameof(SharedHeaderCacheCollection))]
public sealed class SharedHeaderCacheTests
{
    private readonly SharedHeaderCacheFixture _fixture;

    public SharedHeaderCacheTests(SharedHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TryReadAsync_ReturnsNullOnMissingRow()
    {
        await _fixture.ResetAsync();

        var cache = _fixture.CreateCache();
        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.Null(header);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public async Task TryReadAsync_ReturnsPopulatedHeaderOnHit()
    {
        await _fixture.ResetAsync();

        var cache = _fixture.CreateCache();
        var expectedHeader = CreateHeader("a.bin", fileSize: 1234, lineLength: 2048, partNumber: 3);

        await cache.WriteAsync("segment-a", expectedHeader, CancellationToken.None);

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal(expectedHeader.FileName, header!.FileName);
        Assert.Equal(expectedHeader.FileSize, header.FileSize);
        Assert.Equal(expectedHeader.LineLength, header.LineLength);
        Assert.Equal(expectedHeader.PartNumber, header.PartNumber);
        Assert.Equal(expectedHeader.TotalParts, header.TotalParts);
        Assert.Equal(expectedHeader.PartSize, header.PartSize);
        Assert.Equal(expectedHeader.PartOffset, header.PartOffset);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(0, cache.Misses);
    }

    [Fact]
    public async Task WriteAsync_UpsertsExistingRow()
    {
        await _fixture.ResetAsync();

        var cache = _fixture.CreateCache();

        await cache.WriteAsync("segment-a", CreateHeader("first.bin", 100, 200, 1), CancellationToken.None);
        await cache.WriteAsync("segment-a", CreateHeader("second.bin", 1024, 4096, 7), CancellationToken.None);

        var header = await cache.TryReadAsync("segment-a", CancellationToken.None);

        Assert.NotNull(header);
        Assert.Equal("second.bin", header!.FileName);
        Assert.Equal(1024, header.FileSize);
        Assert.Equal(4096, header.LineLength);
        Assert.Equal(7, header.PartNumber);
    }

    private static UsenetYencHeader CreateHeader(
        string fileName,
        long fileSize,
        int lineLength,
        int partNumber)
    {
        return new UsenetYencHeader
        {
            FileName = fileName,
            FileSize = fileSize,
            LineLength = lineLength,
            PartNumber = partNumber,
            TotalParts = 11,
            PartSize = 512,
            PartOffset = 256,
        };
    }
}

public sealed class SharedHeaderCacheFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:16-alpine";
    private const string DatabaseName = "postgres";
    private const string Username = "postgres";
    private const string Password = "postgres";

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage(PostgresImage)
        .WithEnvironment("POSTGRES_DB", DatabaseName)
        .WithEnvironment("POSTGRES_USER", Username)
        .WithEnvironment("POSTGRES_PASSWORD", Password)
        .WithPortBinding(5432, true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(5432))
        .Build();

    private string? _originalDatabaseUrl;

    public static async Task<SharedHeaderCacheFixture> StartAsync()
    {
        var fixture = new SharedHeaderCacheFixture();
        await fixture.InitializeAsync();
        return fixture;
    }

    public async Task InitializeAsync()
    {
        _originalDatabaseUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
        await _container.StartAsync().ConfigureAwait(false);

        var host = _container.Hostname;
        var port = _container.GetMappedPublicPort(5432);
        var connectionString = $"Host={host};Port={port};Database={DatabaseName};Username={Username};Password={Password};Pooling=true;MinPoolSize=0;MaxPoolSize=5";
        Environment.SetEnvironmentVariable("DATABASE_URL", connectionString);

        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.MigrateAsync().ConfigureAwait(false);
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable("DATABASE_URL", _originalDatabaseUrl);
        await _container.DisposeAsync().ConfigureAwait(false);
    }

    public SharedHeaderCache CreateCache() => new();

    public async Task ResetAsync()
    {
        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.ExecuteSqlRawAsync("DELETE FROM yenc_header_cache;").ConfigureAwait(false);
    }
}

[CollectionDefinition(nameof(SharedHeaderCacheCollection), DisableParallelization = true)]
public sealed class SharedHeaderCacheCollection : ICollectionFixture<SharedHeaderCacheFixture>
{
}
