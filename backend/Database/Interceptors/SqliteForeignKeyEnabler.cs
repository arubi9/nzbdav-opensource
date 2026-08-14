using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NzbWebDAV.Database.Interceptors;

/// <summary>
/// Applies the per-connection SQLite pragmas this application depends on.
///
/// Both the sync and async hooks are implemented on purpose. EF Core calls
/// <see cref="ConnectionOpenedAsync"/> for connections opened by async query
/// paths, which is nearly all of them here; overriding only the sync hook left
/// those connections without `foreign_keys`, so cascade deletes silently did
/// not cascade.
/// </summary>
public class SqliteForeignKeyEnabler : DbConnectionInterceptor
{
    // Only per-connection pragmas belong here. journal_mode=WAL is deliberately
    // NOT set on this path: it is persisted in the database header (so it only
    // needs setting once) and applying it is itself a write, which fails with
    // "attempt to write a readonly database" while EF is still probing whether
    // the database file exists. It is set in DatabaseInitialization instead.
    //
    // busy_timeout stops concurrent writers from failing instantly with
    // SQLITE_BUSY; they wait for the lock instead.
    private const string Pragmas =
        "PRAGMA foreign_keys = ON;" +
        "PRAGMA busy_timeout = 10000;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        if (connection is not SqliteConnection) return;

        using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (connection is not SqliteConnection) return;

        await using var command = connection.CreateCommand();
        command.CommandText = Pragmas;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
