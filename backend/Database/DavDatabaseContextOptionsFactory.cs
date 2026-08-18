using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NzbWebDAV.Database.Interceptors;

namespace NzbWebDAV.Database;

internal static class DavDatabaseContextOptionsFactory
{
    public static DbContextOptions<TContext> CreateSqliteOptions<TContext>(
        string databaseFile,
        bool addContentIndexSnapshotInterceptor) where TContext : Microsoft.EntityFrameworkCore.DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        var options = builder
            .UseSqlite($"Data Source={databaseFile}", sqlOptions => sqlOptions.MaxBatchSize(50))
            .AddInterceptors(new Interceptors.SqliteForeignKeyEnabler());

        if (addContentIndexSnapshotInterceptor)
            options.AddInterceptors(new ContentIndexSnapshotInterceptor());

        return options.Options;
    }

    public static DbContextOptions<TContext> CreatePostgresOptions<TContext>(
        string databaseUrl,
        bool addContentIndexSnapshotInterceptor = false) where TContext : Microsoft.EntityFrameworkCore.DbContext
    {
        var connectionString = BuildPostgresConnectionString(databaseUrl);
        var builder = new DbContextOptionsBuilder<TContext>();
        var options = builder.UseNpgsql(connectionString);

        if (addContentIndexSnapshotInterceptor)
            options.AddInterceptors(new ContentIndexSnapshotInterceptor());

        return options.Options;
    }

    public static string BuildPostgresConnectionString(string databaseUrl)
    {
        var isUriStyle =
            databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);
        var connectionString = isUriStyle
            ? ConvertPostgresUrl(databaseUrl)
            : databaseUrl;

        return IsPgbouncerConnection(databaseUrl)
            ? ApplyPgbouncerCompatibilityFlags(connectionString)
            : connectionString;
    }

    /// <summary>
    /// Returns whether a URL identifies a PgBouncer endpoint or a transaction
    /// pool. Session-scoped migration locks are not safe through either kind
    /// of pool because the next command is not guaranteed to use the same
    /// PostgreSQL backend session.
    /// </summary>
    public static bool IsPgbouncerConnection(string databaseUrl)
    {
        // Pool mode is a PgBouncer setting rather than an Npgsql connection
        // string keyword, so inspect it before parsing the rest of the string.
        if (HasTransactionPoolMarker(databaseUrl))
            return true;

        var isUriStyle =
            databaseUrl.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) ||
            databaseUrl.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        if (isUriStyle)
        {
            var uri = new Uri(databaseUrl);
            return IsPgbouncerHost(uri.Host)
                || uri.Port == 6432
                || HasTransactionPoolMarker(uri.Query);
        }

        var builder = new NpgsqlConnectionStringBuilder(databaseUrl);
        return IsPgbouncerHost(builder.Host ?? string.Empty)
            || builder.Port == 6432
            || HasTransactionPoolMarker(databaseUrl);
    }

    public static string ApplyPgbouncerCompatibilityFlags(string connectionString)
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

    private static bool IsPgbouncerHost(string host)
        => host.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase)
            || host.Contains("transaction", StringComparison.OrdinalIgnoreCase);

    private static bool HasTransactionPoolMarker(string value)
    {
        var normalized = value.Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized.Contains("pool_mode=transaction", StringComparison.Ordinal)
            || normalized.Contains("poolmode=transaction", StringComparison.Ordinal)
            || normalized.Contains("pool-mode=transaction", StringComparison.Ordinal);
    }
}
