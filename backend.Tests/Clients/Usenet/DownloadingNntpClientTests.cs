using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Clients.Usenet.Contexts;
using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Tests.TestDoubles;
using UsenetSharp.Models;

namespace NzbWebDAV.Tests.Clients.Usenet;

public sealed class DownloadingNntpClientTests
{
    [Fact]
    public async Task DefaultPriorityBeatsExplicitLowPriorityWaiter()
    {
        var configManager = new ConfigManager();
        configManager.UpdateValues(
        [
            new ConfigItem { ConfigName = "usenet.max-download-connections", ConfigValue = "1" },
            new ConfigItem { ConfigName = "usenet.streaming-priority", ConfigValue = "100" }
        ]);

        using var client = new DownloadingNntpClient(new FakeNntpClient(), configManager);
        var activeConnection = await client.AcquireExclusiveConnectionAsync("holder", CancellationToken.None);

        using var lowPriorityCts = new CancellationTokenSource();
        using var lowPriorityContext = lowPriorityCts.Token.SetContext(
            new DownloadPriorityContext { Priority = SemaphorePriority.Low }
        );
        var lowWaiter = client.AcquireExclusiveConnectionAsync("low", lowPriorityCts.Token);
        var defaultWaiter = client.AcquireExclusiveConnectionAsync("default", CancellationToken.None);

        Assert.False(defaultWaiter.IsCompleted);
        Assert.False(lowWaiter.IsCompleted);

        activeConnection.OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);

        var firstCompleted = await Task.WhenAny(defaultWaiter, lowWaiter);
        Assert.Same(defaultWaiter, firstCompleted);

        (await defaultWaiter).OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
        (await lowWaiter).OnConnectionReadyAgain?.Invoke(ArticleBodyResult.NotRetrieved);
    }
}
