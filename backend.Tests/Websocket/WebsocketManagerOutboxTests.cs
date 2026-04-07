using System.Reflection;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database;
using NzbWebDAV.Tests.Clients.Usenet.Caching;
using NzbWebDAV.Websocket;

namespace backend.Tests.Websocket;

[Collection(nameof(NzbWebDAV.Tests.Clients.Usenet.Caching.SharedHeaderCacheCollection))]
public sealed class WebsocketManagerOutboxTests : IClassFixture<PostgresHeaderCacheFixture>
{
    private readonly PostgresHeaderCacheFixture _fixture;

    public WebsocketManagerOutboxTests(PostgresHeaderCacheFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SendMessage_InSingleNodeMode_UpdatesLocalState()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", null),
            ("DATABASE_URL_SESSION", null));

        var manager = new WebsocketManager();
        await manager.SendMessage(WebsocketTopic.QueueItemStatus, "ready");

        Assert.Equal("ready", GetLastMessage(manager, WebsocketTopic.QueueItemStatus));
    }

    [Fact]
    public async Task SendMessage_InMultiNodeMode_WritesOutboxRow()
    {
        if (!_fixture.IsAvailable) return;

        await _fixture.ResetAsync();
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("DATABASE_URL", _fixture.ConnectionString),
            ("DATABASE_URL_SESSION", _fixture.ConnectionString));

        var manager = new WebsocketManager();
        await manager.SendMessage(WebsocketTopic.QueueItemAdded, "{\"queueItem\":\"abc\"}");

        await using var dbContext = new DavDatabaseContext();
        var row = await dbContext.WebsocketOutbox
            .OrderByDescending(x => x.Seq)
            .FirstOrDefaultAsync();

        Assert.NotNull(row);
        Assert.Equal(WebsocketTopic.QueueItemAdded.Name, row!.Topic);
        Assert.Equal("{\"queueItem\":\"abc\"}", row.Payload);
    }

    private static string? GetLastMessage(WebsocketManager manager, WebsocketTopic topic)
    {
        var field = typeof(WebsocketManager).GetField("_lastMessage", BindingFlags.Instance | BindingFlags.NonPublic);
        var value = (Dictionary<WebsocketTopic, string>)field!.GetValue(manager)!;
        return value.TryGetValue(topic, out var message) ? message : null;
    }
}
