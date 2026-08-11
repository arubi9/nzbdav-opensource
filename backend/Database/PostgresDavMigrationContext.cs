using Microsoft.EntityFrameworkCore;

namespace NzbWebDAV.Database;

public sealed class PostgresDavMigrationContext : DavDatabaseContext
{
    public PostgresDavMigrationContext(DbContextOptions<PostgresDavMigrationContext> options)
        : base(options)
    {
    }
}
