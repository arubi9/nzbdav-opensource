using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Database;

public sealed class PostgresDavMigrationContextFactory : IDesignTimeDbContextFactory<PostgresDavMigrationContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav";

    public PostgresDavMigrationContext CreateDbContext(string[] args)
    {
        var databaseUrl = EnvironmentUtil.GetMigrationDatabaseUrl()
            ?? EnvironmentUtil.GetDatabaseUrl()
            ?? DesignTimeConnectionString;
        if (DavDatabaseContextOptionsFactory.IsPgbouncerConnection(databaseUrl))
        {
            throw new InvalidOperationException(
                "PostgreSQL migrations require a direct PostgreSQL endpoint; do not use PgBouncer or a transaction pool.");
        }

        var connectionString = databaseUrl == DesignTimeConnectionString
            ? databaseUrl
            : DavDatabaseContextOptionsFactory.BuildPostgresConnectionString(databaseUrl);
        var options = new DbContextOptionsBuilder<PostgresDavMigrationContext>()
            .UseNpgsql(connectionString)
            .Options;

        return new PostgresDavMigrationContext(options);
    }
}
