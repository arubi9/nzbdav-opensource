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

        return UsesPgbouncer(databaseUrl, connectionString, isUriStyle)
            ? ApplyPgbouncerCompatibilityFlags(connectionString)
            : connectionString;
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

    private static bool UsesPgbouncer(string databaseUrl, string connectionString, bool isUriStyle)
    {
        if (isUriStyle)
            return new Uri(databaseUrl).Host.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase);

        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return builder.Host.Contains("pgbouncer", StringComparison.OrdinalIgnoreCase);
    }
}
