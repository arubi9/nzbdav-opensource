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

                provider.Health.RegisterSuccess();
                return result;
            }
            catch (Exception e) when (!e.IsCancellationException())
            {
                provider.Health.RegisterFailure(provider.Client.ProviderType);
                lastException = ExceptionDispatchInfo.Capture(e);
            }
        }

        lastException?.Throw();
        throw new Exception("There are no usenet providers configured.");
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

    private sealed class ProviderHealth
    {
        private int _consecutiveFailures;
        private long _cooldownUntilUtcTicks;

        public bool IsInCooldown(DateTimeOffset now)
            => now.UtcTicks < Interlocked.Read(ref _cooldownUntilUtcTicks);

        public void RegisterSuccess()
        {
            Interlocked.Exchange(ref _consecutiveFailures, 0);
            Interlocked.Exchange(ref _cooldownUntilUtcTicks, DateTimeOffset.MinValue.UtcTicks);
        }

        public void RegisterFailure(ProviderType providerType)
        {
            var failures = Interlocked.Increment(ref _consecutiveFailures);
            var cooldown = TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(failures, 5))));
            var until = DateTimeOffset.UtcNow.Add(cooldown);
            Interlocked.Exchange(ref _cooldownUntilUtcTicks, until.UtcTicks);
            Log.Warning("Blocking NNTP provider {Type} for {Cooldown}", providerType, cooldown);
        }
    }
}
