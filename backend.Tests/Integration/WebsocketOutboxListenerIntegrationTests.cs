using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Clients.Usenet.Caching;
using NzbWebDAV.Websocket;

namespace backend.Tests.Integration;

[Collection(nameof(NzbWebDAV.Tests.Clients.Usenet.Caching.SharedHeaderCacheCollection))]
public sealed class WebsocketOutboxListenerIntegrationTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public WebsocketOutboxListenerIntegrationTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task CatchUpOnce_ReplaysRowsNewerThanLastSeen()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", _fixture.ConnectionString));

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemProgress.Name,
                Payload = "42"
            });
            await dbContext.SaveChangesAsync();
        }

        var manager = new WebsocketManager();
        var listener = new WebsocketOutboxListener(manager);
        await listener.CatchUpOnce(CancellationToken.None);

        Assert.Equal("42", GetLastMessage(manager, WebsocketTopic.QueueItemProgress));
    }

    [SkippableFact]
    public async Task CatchUpOnce_AdvancesPastUnknownTopics()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", _fixture.ConnectionString));

        long poisonSeq;
        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = "unknown.topic",
                Payload = "poison"
            });
            await dbContext.SaveChangesAsync();
            poisonSeq = await dbContext.WebsocketOutbox
                .OrderByDescending(x => x.Seq)
                .Select(x => x.Seq)
                .FirstAsync();
        }

        var manager = new WebsocketManager();
        var listener = new WebsocketOutboxListener(manager);
        await listener.CatchUpOnce(CancellationToken.None);

        Assert.Equal(poisonSeq, GetLastSeenSeq(listener));
    }

    [SkippableFact]
    public async Task InitializeStateFromOutbox_RestoresLatestStatefulMessages_WithoutReplayingEvents()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", _fixture.ConnectionString));

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemStatus.Name,
                Payload = "item-1|Queued"
            });
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemStatus.Name,
                Payload = "item-1|Downloading"
            });
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemAdded.Name,
                Payload = "{\"queueItem\":\"event-only\"}"
            });
            await dbContext.SaveChangesAsync();
        }

        var manager = new WebsocketManager();
        var listener = new WebsocketOutboxListener(manager);

        await listener.InitializeStateFromOutbox(CancellationToken.None);

        Assert.Equal("item-1|Downloading", GetLastMessage(manager, WebsocketTopic.QueueItemStatus));
        Assert.Null(GetLastMessage(manager, WebsocketTopic.QueueItemAdded));
    }

    [SkippableFact]
    public async Task RunListenerLoop_WithoutSessionDatabase_RunsInPollOnlyMode()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", null));

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemStatus.Name,
                Payload = "item-1|Queued"
            });
            await dbContext.SaveChangesAsync();
        }

        var manager = new WebsocketManager();
        var listener = new WebsocketOutboxListener(manager);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeRunListenerLoopAsync(listener, cancellation.Token));

        Assert.Equal("item-1|Queued", GetLastMessage(manager, WebsocketTopic.QueueItemStatus));
    }

    [SkippableFact]
    public async Task SweepOnce_RemovesExpiredRows()
    {
        Skip.IfNot(_fixture.IsAvailable, "Docker is required for this integration test.");

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", _fixture.ConnectionString));

        await using (var dbContext = new DavDatabaseContext())
        {
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemAdded.Name,
                Payload = "old",
                CreatedAt = DateTime.UtcNow.AddMinutes(-30)
            });
            dbContext.WebsocketOutbox.Add(new NzbWebDAV.Database.Models.WebsocketOutboxEntry
            {
                Topic = WebsocketTopic.QueueItemAdded.Name,
                Payload = "new",
                CreatedAt = DateTime.UtcNow
            });
            await dbContext.SaveChangesAsync();
        }

        var sweeper = new WebsocketOutboxSweeper();
        await sweeper.SweepOnce(CancellationToken.None);

        await using var verifyContext = new DavDatabaseContext();
        var payloads = await verifyContext.WebsocketOutbox
            .OrderBy(x => x.Seq)
            .Select(x => x.Payload)
            .ToListAsync();

        Assert.DoesNotContain("old", payloads);
        Assert.Contains("new", payloads);
    }

    private static string? GetLastMessage(WebsocketManager manager, WebsocketTopic topic)
    {
        var field = typeof(WebsocketManager).GetField("_lastMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = (Dictionary<WebsocketTopic, string>)field!.GetValue(manager)!;
        return value.TryGetValue(topic, out var message) ? message : null;
    }

    private static long GetLastSeenSeq(WebsocketOutboxListener listener)
    {
        var field = typeof(WebsocketOutboxListener).GetField("_lastSeenSeq", BindingFlags.Instance | BindingFlags.NonPublic);
        return (long)field!.GetValue(listener)!;
    }

    private static Task InvokeRunListenerLoopAsync(WebsocketOutboxListener listener, CancellationToken cancellationToken)
    {
        var method = typeof(WebsocketOutboxListener).GetMethod("RunListenerLoop", BindingFlags.Instance | BindingFlags.NonPublic);
        return (Task)method!.Invoke(listener, new object?[] { cancellationToken })!;
    }
}
