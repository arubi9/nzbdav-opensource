using System.Runtime.ExceptionServices;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Extensions;
using NzbWebDAV.Models;
using Serilog;
using UsenetSharp.Models;

namespace NzbWebDAV.Clients.Usenet;

public class MultiProviderNntpClient(List<MultiConnectionNntpClient> providers) : NntpClient
{
    private readonly List<ProviderEntry> _providers = providers
        .Select((client, index) => new ProviderEntry(index, client))
        .ToList();

    public bool HasAvailableProvider()
        => _providers.Any(p => p.Client.ProviderType != ProviderType.Disabled && !p.Health.IsInCooldown(DateTimeOffset.UtcNow));

    public int HealthyProviderCount
        => _providers.Count(p => p.Client.ProviderType != ProviderType.Disabled && !p.Health.IsInCooldown(DateTimeOffset.UtcNow));

    public int TotalProviderCount
        => _providers.Count(p => p.Client.ProviderType != ProviderType.Disabled);

    public override Task ConnectAsync(string host, int port, bool useSsl, CancellationToken ct)
    {
        throw new NotSupportedException("Please connect within the connectionFactory");
    }

    public override Task<UsenetResponse> AuthenticateAsync(string user, string pass, CancellationToken ct)
    {
        throw new NotSupportedException("Please authenticate within the connectionFactory");
    }

    public override Task<UsenetStatResponse> StatAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.StatAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetHeadResponse> HeadAsync(SegmentId segmentId, CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.HeadAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DecodedBodyAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DecodedArticleAsync(segmentId, cancellationToken), cancellationToken);
    }

    public override Task<UsenetDateResponse> DateAsync(CancellationToken cancellationToken)
    {
        return RunFromPoolWithBackup(x => x.DateAsync(cancellationToken), cancellationToken);
    }

    public override async Task<UsenetDecodedBodyResponse> DecodedBodyAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        UsenetDecodedBodyResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedBodyAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedBodyFollows)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    public override async Task<UsenetDecodedArticleResponse> DecodedArticleAsync(
        SegmentId segmentId,
        Action<ArticleBodyResult>? onConnectionReadyAgain,
        CancellationToken cancellationToken)
    {
        UsenetDecodedArticleResponse? result;
        try
        {
            result = await RunFromPoolWithBackup(
                x => x.DecodedArticleAsync(segmentId, OnConnectionReadyAgain, cancellationToken),
                cancellationToken
            ).ConfigureAwait(false);
        }
        catch
        {
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
            throw;
        }

        if (result.ResponseType != UsenetResponseType.ArticleRetrievedHeadAndBodyFollow)
            onConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        return result;

        void OnConnectionReadyAgain(ArticleBodyResult articleBodyResult)
        {
            if (articleBodyResult == ArticleBodyResult.Retrieved)
                onConnectionReadyAgain?.Invoke(ArticleBodyResult.Retrieved);
        }
    }

    private async Task<T> RunFromPoolWithBackup<T>(
        Func<INntpClient, Task<T>> task,
        CancellationToken cancellationToken
    ) where T : UsenetResponse
    {
        ExceptionDispatchInfo? lastException = null;
        var orderedProviders = GetOrderedProviders();
        if (orderedProviders.Count == 0)
            throw new CouldNotConnectToUsenetException("All NNTP providers are currently in cooldown.");

        for (var i = 0; i < orderedProviders.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provider = orderedProviders[i];
            var isLastProvider = i == orderedProviders.Count - 1;

            if (lastException is not null)
            {
                var msg = lastException.SourceException.Message;
                Log.Debug($"Encountered error during NNTP Operation: `{msg}`. Trying another provider.");
            }

            try
            {
                var result = await task.Invoke(provider.Client).ConfigureAwait(false);

                if (!isLastProvider && result.ResponseType == UsenetResponseType.NoArticleWithThatMessageId)
                    continue;

                provider.Health.RegisterSuccess(provider.Client.ProviderType);
                return result;
            }
            catch (UsenetArticleNotFoundException)
            {
                // Missing articles are content errors, not provider failures.
                // Do not trip the circuit breaker for DMCA'd or expired articles.
                provider.Health.RegisterSuccess(provider.Client.ProviderType);
                throw;
            }
            catch (Exception e) when (IsStaleConnectionError(e))
            {
                // Stale/corrupt NNTP connections (e.g. "Invalid NNTP Response")
                // are transient — the connection was idle too long and the server
                // dropped it.  Don't trip the circuit breaker; the retry in
                // RunWithConnection already replaced the bad connection.
                Log.Debug(e, "NNTP stale connection error (not a provider failure)");
                lastException = ExceptionDispatchInfo.Capture(e);
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                Log.Warning(e, "NNTP provider {Type} operation failed", provider.Client.ProviderType);
                provider.Health.RegisterFailure(provider.Client.ProviderType);
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
    }

    private static bool IsStaleConnectionError(Exception e)
    {
        // Walk the exception chain looking for UsenetProtocolException or
        // common stale-connection indicators (reset, broken pipe, EOF).
        for (var ex = e; ex != null; ex = ex.InnerException)
        {
            var typeName = ex.GetType().Name;
            if (typeName.Contains("UsenetProtocol", StringComparison.OrdinalIgnoreCase))
                return true;
            var msg = ex.Message;
            if (msg.Contains("Invalid NNTP Response", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("connection was reset", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("broken pipe", StringComparison.OrdinalIgnoreCase)
                || msg.Contains("end of stream", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private List<ProviderEntry> GetOrderedProviders()
    {
        var now = DateTimeOffset.UtcNow;
        return _providers
            .Where(x => x.Client.ProviderType != ProviderType.Disabled)
            .Where(x => !x.Health.IsInCooldown(now))
            .OrderBy(x => x.Client.ProviderType)
            .ThenByDescending(x => x.Client.AvailableConnections)
            .ToList();
    }

    public override void Dispose()
    {
        foreach (var provider in _providers)
            provider.Client.Dispose();
        GC.SuppressFinalize(this);
    }

    private sealed class ProviderEntry(int index, MultiConnectionNntpClient client)
    {
        public int Index { get; } = index;
        public MultiConnectionNntpClient Client { get; } = client;
        public ProviderHealth Health { get; } = new();
    }

    /// <summary>
    /// Per-provider circuit breaker with exponential-backoff cooldown.
    ///
    /// State model (informal — not a full textbook Closed/Open/HalfOpen):
    /// <list type="bullet">
    /// <item><b>Closed</b> — <c>_consecutiveFailures == 0</c>. Provider is
    /// used normally. <see cref="IsInCooldown"/> returns false.</item>
    /// <item><b>Open</b> — <c>IsInCooldown(now) == true</c>. Provider is
    /// skipped by <see cref="GetOrderedProviders"/>. Cooldown doubles on
    /// each subsequent failure up to a 60s ceiling.</item>
    /// <item><b>HalfOpen</b> (implicit) — the first request that arrives
    /// AFTER <c>_cooldownUntilUtcTicks</c> expires acts as the probe.
    /// Success → Closed (counter reset). Failure → Open with a longer
    /// cooldown.</item>
    /// </list>
    ///
    /// Known limitation: no HalfOpen concurrency limit. If N requests
    /// arrive in the first ~100ms after cooldown expiry, all N hit the
    /// provider before the first one's failure updates the cooldown.
    /// In practice this is bounded — the first failure re-extends the
    /// cooldown, and subsequent in-flight requests either succeed (good,
    /// we're back online) or fail fast. Worst case: a handful of wasted
    /// connection slots during each recovery probe window. Fixable with
    /// an Interlocked.CompareExchange gate if it becomes a real problem.
    /// </summary>
    private sealed class ProviderHealth
    {
        private int _consecutiveFailures;
        private long _cooldownUntilUtcTicks;

        public bool IsInCooldown(DateTimeOffset now)
            => now.UtcTicks < Interlocked.Read(ref _cooldownUntilUtcTicks);

        public void RegisterSuccess(ProviderType providerType)
        {
            // NOTE on thread-safety: RegisterSuccess and RegisterFailure
            // are not mutually atomic. If a success and a failure race,
            // the success can wipe the failure's cooldown. Example:
            //   1. Failure thread:   Interlocked.Exchange cooldown = now+8s
            //   2. Success thread:   Interlocked.Exchange cooldown = MinValue
            //   3. Failure thread:   returns — but cooldown was cleared
            // Result: one extra probe request per race, then the next
            // failure re-opens the circuit with the correct cooldown. The
            // race is self-correcting within one NNTP round-trip and the
            // old code had the same shape, so this isn't a regression.
            // Fix-if-needed: CompareExchange loop that only clears the
            // cooldown when the current value matches what the caller
            // sampled. Not worth the complexity for a self-healing race.
            var previousFailures = Interlocked.Exchange(ref _consecutiveFailures, 0);
            var wasInCooldown = Interlocked.Exchange(
                ref _cooldownUntilUtcTicks, DateTimeOffset.MinValue.UtcTicks);

            // Emit a recovery log line ONLY on the transition from
            // failing-or-cooling-down back to healthy. Normal healthy
            // operation would otherwise log this on every successful
            // request, which is noise.
            if (previousFailures > 0 || wasInCooldown > DateTimeOffset.UtcNow.UtcTicks)
            {
                Log.Information(
                    "NNTP provider {Type} recovered after {Failures} consecutive failures",
                    providerType,
                    previousFailures);
            }
        }

        public void RegisterFailure(ProviderType providerType)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            var cooldown = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 5))));
            var until = DateTimeOffset.UtcNow.Add(cooldown);
            Interlocked.Exchange(ref _cooldownUntilUtcTicks, until.UtcTicks);
            Log.Warning("Blocking NNTP provider {Type} for {Cooldown} ({Failures} consecutive failures)",
                providerType, cooldown, failures);
        }
    }
}
