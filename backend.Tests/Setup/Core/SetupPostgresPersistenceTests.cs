using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.EncryptionStatus;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet.Caching;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupPostgresPersistenceTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public SetupPostgresPersistenceTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SetupGrantIdIsNeverGenerated_Postgres()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for the opt-in PostgreSQL setup test.");

        await using var context = new DavDatabaseContext();
        var property = context.Model.FindEntityType(typeof(NzbWebDAV.Database.Models.SetupGrant))!.FindProperty(nameof(NzbWebDAV.Database.Models.SetupGrant.Id));
        Assert.Equal(Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never, property!.ValueGenerated);
    }

    [Fact]
    public async Task EncryptionStatus_PostgresAcknowledgementIsIdempotent()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for the opt-in PostgreSQL setup test.");

        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        await using (var reset = new DavDatabaseContext())
        {
            await reset.Database.ExecuteSqlRawAsync(
                "DELETE FROM \"ConfigItems\" WHERE \"ConfigName\" = 'encryption.post-migration-acknowledged'");
        }

        await using var context1 = new DavDatabaseContext();
        await using var context2 = new DavDatabaseContext();
        using var encryption1 = new ConfigEncryptionService();
        using var encryption2 = new ConfigEncryptionService();
        var first = new EncryptionStatusController(new DavDatabaseClient(context1), encryption1);
        var second = new EncryptionStatusController(new DavDatabaseClient(context2), encryption2);

        await Task.WhenAll(first.AcknowledgePostMigration(), second.AcknowledgePostMigration());

        await using var verify = new DavDatabaseContext();
        Assert.Equal(1, await verify.ConfigItems.CountAsync(
            row => row.ConfigName == "encryption.post-migration-acknowledged"));
    }

    [Fact]
    public async Task SetupPersistence_PostgresConcurrentWritesUseOneLogicalRow()
    {
        Assert.SkipUnless(_fixture.IsAvailable, "Docker is required for the opt-in PostgreSQL setup test.");

        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        await using (var reset = new DavDatabaseContext())
        {
            var setupRows = (await reset.ConfigItems.ToListAsync())
                .Where(row => row.ConfigName.StartsWith("setup.", StringComparison.OrdinalIgnoreCase))
                .ToList();
            reset.ConfigItems.RemoveRange(setupRows);
            await reset.SaveChangesAsync();
        }

        await using var context1 = new DavDatabaseContext();
        await using var context2 = new DavDatabaseContext();
        using var encryption1 = new ConfigEncryptionService();
        using var encryption2 = new ConfigEncryptionService();
        var first = new SetupConfigPersistence(new ConfigManager(encryption1), context1);
        var second = new SetupConfigPersistence(new ConfigManager(encryption2), context2);

        await Task.WhenAll(
            first.SaveAsync(new SetupSecretValues
            {
                Indexers = "[]",
                JellyfinApiKey = "postgres-jellyfin-a",
                PluginApiKey = "postgres-plugin-a",
            }, completed: false),
            second.SaveAsync(new SetupSecretValues
            {
                Indexers = "[]",
                JellyfinApiKey = "postgres-jellyfin-b",
                PluginApiKey = "postgres-plugin-b",
            }, completed: false));

        await using var verify = new DavDatabaseContext();
        var rows = await verify.ConfigItems
            .Where(row => row.ConfigName.StartsWith("setup."))
            .ToListAsync();
        Assert.Equal(4, rows.Count);
        Assert.Equal(4, rows.Select(row => row.ConfigName).Distinct(StringComparer.Ordinal).Count());
    }
}
