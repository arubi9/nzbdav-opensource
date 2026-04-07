using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NzbWebDAV.Database;
using Npgsql;
using Serilog;

namespace NzbWebDAV.Api.Filters;

public sealed class PostgresAuthFailureTracker : IAuthFailureTracker
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromSeconds(5);

    private readonly Func<DavDatabaseContext> _dbContextFactory;
    private readonly AuthFailureTracker _inMemoryFallback;
    private readonly MemoryCache _negativeCache = new(new MemoryCacheOptions());

    public PostgresAuthFailureTracker(
        AuthFailureTracker inMemoryFallback,
        Func<DavDatabaseContext>? dbContextFactory = null)
    {
        _inMemoryFallback = inMemoryFallback;
        _dbContextFactory = dbContextFactory ?? (() => new DavDatabaseContext());
    }

    public async Task<bool> IsBlockedAsync(string ipAddress)
    {
        if (_negativeCache.TryGetValue(ipAddress, out _))
            return false;

        try
        {
            await using var dbContext = _dbContextFactory();
            var blocked = await dbContext.AuthFailures
                .AnyAsync(x =>
                    x.IpAddress == ipAddress &&
                    x.FailureCount >= 10 &&
                    x.WindowStart > DateTime.UtcNow.Subtract(Window))
                .ConfigureAwait(false);

            if (!blocked)
                _negativeCache.Set(ipAddress, true, NegativeCacheTtl);

            return blocked;
        }
        catch (Exception ex) when (IsTransientDbError(ex))
        {
            Log.Warning(ex, "PostgresAuthFailureTracker falling back to in-memory block check.");
            return _inMemoryFallback.IsBlocked(ipAddress);
        }
    }

    public async Task RecordFailureAsync(string ipAddress)
    {
        try
        {
            await using var dbContext = _dbContextFactory();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO auth_failures (ip_address, failure_count, window_start)
                VALUES ({ipAddress}, 1, {DateTime.UtcNow})
                ON CONFLICT (ip_address) DO UPDATE
                SET failure_count = CASE
                        WHEN auth_failures.window_start < {DateTime.UtcNow.Subtract(Window)} THEN 1
                        ELSE auth_failures.failure_count + 1
                    END,
                    window_start = CASE
                        WHEN auth_failures.window_start < {DateTime.UtcNow.Subtract(Window)} THEN {DateTime.UtcNow}
                        ELSE auth_failures.window_start
                    END;
            ").ConfigureAwait(false);

            _negativeCache.Remove(ipAddress);
        }
        catch (Exception ex) when (IsTransientDbError(ex))
        {
            Log.Warning(ex, "PostgresAuthFailureTracker falling back to in-memory failure tracking.");
            _inMemoryFallback.RecordFailure(ipAddress);
        }
    }

    private static bool IsTransientDbError(Exception ex)
    {
        return ex is NpgsqlException or TimeoutException or InvalidOperationException;
    }
}
