using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Npgsql;
using NzbWebDAV.Database;
using NzbWebDAV.Websocket;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class WebsocketOutboxListener(WebsocketManager websocketManager) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static volatile bool _sessionUrlWarningLogged;
    private long _lastSeenSeq;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunListenerLoop(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "WebsocketOutboxListener connection loop failed; retrying.");
                await Task.Delay(ReconnectDelay, stoppingToken).ConfigureAwait(false);
            }
        }
    }

    public async Task CatchUpOnce(CancellationToken cancellationToken)
    {
        await using var dbContext = new DavDatabaseContext();
        var rows = await dbContext.WebsocketOutbox
            .Where(x => x.Seq > _lastSeenSeq)
            .OrderBy(x => x.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var row in rows)
        {
            _lastSeenSeq = row.Seq;

            if (!WebsocketTopic.TryFromName(row.Topic, out var topic) || topic is null)
            {
                Log.Warning("Skipping websocket outbox row {Seq} with unknown topic '{Topic}'", row.Seq, row.Topic);
                continue;
            }

            await websocketManager.FanoutToLocalSockets(topic, row.Payload).ConfigureAwait(false);
        }
    }

    public async Task InitializeStateFromOutbox(CancellationToken cancellationToken)
    {
        await using var dbContext = new DavDatabaseContext();
        var tailSeq = await dbContext.WebsocketOutbox
            .OrderByDescending(x => x.Seq)
            .Select(x => x.Seq)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var stateRows = await dbContext.WebsocketOutbox
            .Where(x => x.Seq <= tailSeq)
            .OrderByDescending(x => x.Seq)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var seenTopics = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in stateRows)
        {
            if (!WebsocketTopic.TryFromName(row.Topic, out var topic) || topic is null)
                continue;
            if (topic.Type != WebsocketTopic.TopicType.State)
                continue;
            if (!seenTopics.Add(topic.Name))
                continue;

            websocketManager.RememberLastMessage(topic, row.Payload);
        }

        _lastSeenSeq = tailSeq;
    }

    private async Task RunListenerLoop(CancellationToken cancellationToken)
    {
        var sessionConnectionString = EnvironmentUtil.GetDatabaseUrlSession();
        if (string.IsNullOrEmpty(sessionConnectionString))
        {
            if (!_sessionUrlWarningLogged)
            {
                _sessionUrlWarningLogged = true;
                Log.Warning("DATABASE_URL_SESSION is not configured; WebsocketOutboxListener is running in poll-only mode.");
            }
            await InitializeStateFromOutbox(cancellationToken).ConfigureAwait(false);
            using var pollOnlyTimer = new PeriodicTimer(PollInterval);
            while (await pollOnlyTimer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                await CatchUpOnce(cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var connection = new NpgsqlConnection(sessionConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await InitializeStateFromOutbox(cancellationToken).ConfigureAwait(false);

        await using (var command = new NpgsqlCommand("LISTEN websocket;", connection))
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(PollInterval);

        while (!cancellationToken.IsCancellationRequested)
        {
            var notifyTask = connection.WaitAsync(cancellationToken);
            var pollTask = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            var completedTask = await Task.WhenAny(notifyTask, pollTask).ConfigureAwait(false);

            if (completedTask == pollTask && !await pollTask.ConfigureAwait(false))
                break;

            if (completedTask == notifyTask)
                await notifyTask.ConfigureAwait(false);

            await CatchUpOnce(cancellationToken).ConfigureAwait(false);
        }
    }
}
