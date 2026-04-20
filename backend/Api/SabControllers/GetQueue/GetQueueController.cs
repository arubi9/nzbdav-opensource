using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Queue;

namespace NzbWebDAV.Api.SabControllers.GetQueue;

public class GetQueueController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    QueueManager queueManager,
    ConfigManager configManager
) : SabApiController.BaseController(httpContext, configManager)
{
    private async Task<GetQueueResponse> GetQueueAsync(GetQueueRequest request)
    {
        // get all in-progress items (queue may process N items concurrently)
        var inProgressItems = queueManager.GetInProgressQueueItems();
        var inProgressById = inProgressItems.ToDictionary(x => x.queueItem.Id);
        var inProgressIds = inProgressById.Keys.ToHashSet();

        // get total count
        var ct = request.CancellationToken;
        var totalCount = await dbClient.GetQueueItemsCount(request.Category, ct).ConfigureAwait(false);

        // get queued items (exclude any currently being processed)
        var getQueueItemsTask = dbClient.GetQueueItems(request.Category, request.Start, request.Limit, ct);
        var queueItems = (await getQueueItemsTask.ConfigureAwait(false))
            .Where(x => !inProgressIds.Contains(x.Id))
            .ToArray();

        // get slots — prepend all in-progress items at the top of the first page
        var prependedInProgress = request is { Start: 0, Limit: > 0 }
            ? inProgressItems.Select(x => (QueueItem?)x.queueItem).ToList()
            : new List<QueueItem?>();

        var slots = prependedInProgress
            .Concat(queueItems.Select(q => (QueueItem?)q))
            .Where(queueItem => queueItem != null)
            .Select((queueItem, index) =>
            {
                var isInProgress = inProgressById.TryGetValue(queueItem!.Id, out var inProgress);
                var percentage = isInProgress ? inProgress.progress : 0;
                var status = isInProgress ? "Downloading" : "Queued";
                return GetQueueResponse.QueueSlot.FromQueueItem(queueItem, index, percentage, status);
            })
            .ToList();

        // return response
        return new GetQueueResponse()
        {
            Queue = new GetQueueResponse.QueueObject()
            {
                Paused = false,
                Slots = slots,
                TotalCount = totalCount,
            }
        };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = new GetQueueRequest(httpContext);
        return Ok(await GetQueueAsync(request).ConfigureAwait(false));
    }
}