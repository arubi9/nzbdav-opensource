using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NzbWebDAV.Database;

public sealed class DavDatabaseContextDesignTimeFactory : IDesignTimeDbContextFactory<DavDatabaseContext>
{
    private const string DesignTimeDatabaseFile = "nzbdav-design-time.db";

    public DavDatabaseContext CreateDbContext(string[] args)
    {
        var options = DavDatabaseContextOptionsFactory.CreateSqliteOptions<DavDatabaseContext>(
            DesignTimeDatabaseFile,
            addContentIndexSnapshotInterceptor: false);

        return new DavDatabaseContext(options);
    }
}
