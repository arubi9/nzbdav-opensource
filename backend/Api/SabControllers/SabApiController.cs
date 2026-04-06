using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Api.SabControllers.AddUrl;
using NzbWebDAV.Api.SabControllers.GetCategories;
using NzbWebDAV.Api.SabControllers.GetConfig;
using NzbWebDAV.Api.SabControllers.GetFullStatus;
using NzbWebDAV.Api.SabControllers.GetHistory;
using NzbWebDAV.Api.SabControllers.GetQueue;
using NzbWebDAV.Api.SabControllers.GetVersion;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;
using NzbWebDAV.Api.SabControllers.RemoveFromQueue;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Extensions;
using NzbWebDAV.Queue;
using NzbWebDAV.Utils;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers;

[ApiController]
[Route("api")]
public class SabApiController(
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    QueueManager queueManager,
    WebsocketManager websocketManager
) : ControllerBase
{
    [HttpGet]
    [HttpPost]
    public async Task<IActionResult> HandleApiRequests()
    {
        try
        {
            var controller = GetController();
            return await controller.HandleRequest().ConfigureAwait(false);
        }
        catch (BadHttpRequestException e)
        {
            return BadRequest(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (UnauthorizedAccessException e)
        {
            return Unauthorized(new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
        catch (Exception e)
        {
            return StatusCode(500, new SabBaseResponse()
            {
                Status = false,
                Error = e.Message
            });
        }
    }

    public BaseController GetController()
    {
        switch (HttpContext.GetQueryParam("mode"))
        {
            case "version":
                return new GetVersionController(
                    HttpContext, configManager);
            case "get_cats":
                return new GetCategoriesController(
                    HttpContext, configManager);
            case "get_config":
                return new GetConfigController(
                    HttpContext, configManager);
            case "fullstatus":
                return new GetFullStatusController(
                    HttpContext, configManager);
            case "addfile":
                return new AddFileController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "addurl":
                return new AddUrlController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);

            case "queue" when HttpContext.GetQueryParam("name") == "delete":
                return new RemoveFromQueueController(
                    HttpContext, dbClient, queueManager, configManager, websocketManager);
            case "queue":
                return new GetQueueController(
                    HttpContext, dbClient, queueManager, configManager);

            case "history" when HttpContext.GetQueryParam("name") == "delete":
                return new RemoveFromHistoryController(
                    HttpContext, dbClient, configManager, websocketManager);
            case "history":
                return new GetHistoryController(
                    HttpContext, dbClient, configManager);

            default:
                throw new BadHttpRequestException("Invalid mode");
        }
    }

    public abstract class BaseController(HttpContext httpContext, ConfigManager configManager) : ControllerBase
    {
        public Task<IActionResult> HandleRequest()
        {
            if (RequiresAuthentication)
            {
                var apiKey = httpContext.GetRequestApiKey();
                if (string.IsNullOrEmpty(apiKey))
                    throw new UnauthorizedAccessException("API Key Required");
                if (!ValidateApiKeyConstantTime(apiKey))
                    throw new UnauthorizedAccessException("API Key Incorrect");
            }

            return Handle();
        }

        private bool ValidateApiKeyConstantTime(string providedKey)
        {
            var providedBytes = System.Text.Encoding.UTF8.GetBytes(providedKey);
            var key1Bytes = System.Text.Encoding.UTF8.GetBytes(configManager.GetApiKey());
            var key2Bytes = System.Text.Encoding.UTF8.GetBytes(
                EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY"));

            // Evaluate BOTH comparisons — do not short-circuit.
            // FixedTimeEquals handles different-length inputs safely.
            var matchesKey1 = System.Security.Cryptography.CryptographicOperations
                .FixedTimeEquals(providedBytes, key1Bytes);
            var matchesKey2 = System.Security.Cryptography.CryptographicOperations
                .FixedTimeEquals(providedBytes, key2Bytes);

            return matchesKey1 | matchesKey2;
        }

        protected virtual bool RequiresAuthentication => true;
        protected abstract Task<IActionResult> Handle();
    }
}