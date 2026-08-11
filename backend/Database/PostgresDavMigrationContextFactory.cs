using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NzbWebDAV.Database;

public sealed class PostgresDavMigrationContextFactory : IDesignTimeDbContextFactory<PostgresDavMigrationContext>
{
    private const string DesignTimeConnectionString =
        "Host=localhost;Port=5432;Database=nzbdav;Username=nzbdav;Password=nzbdav";

    public PostgresDavMigrationContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PostgresDavMigrationContext>()
            .UseNpgsql(DesignTimeConnectionString)
            .Options;

        return new PostgresDavMigrationContext(options);
    }
}
