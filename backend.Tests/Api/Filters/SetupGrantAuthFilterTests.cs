using System.Collections.Generic;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Utils;

namespace NzbWebDAV.Tests.Api.Filters;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupGrantAuthFilterTests
{
    [Fact]
    public async Task Filter_DeniesRequestWithoutGrantHeader()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-filter-missing-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var filter = new SetupGrantAuthFilter(new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(true)));

        var requestContext = new DefaultHttpContext();
        requestContext.Request.Method = HttpMethods.Post;
        var actionContext = new ActionContext(requestContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var called = false;
        ActionExecutionDelegate next = () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(new ActionExecutedContext(actionContext, [], new object()));
        };

        await filter.OnActionExecutionAsync(executingContext, next);

        Assert.False(called);
        Assert.IsType<UnauthorizedObjectResult>(executingContext.Result);
    }

    [Fact]
    public async Task Filter_RejectsValidGrantWhileRevocationCleanupIsPending()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-filter-pending-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        await using var context = await CreateMigratedContextAsync();
        var configManager = new ConfigManager(new ConfigEncryptionService());
        await configManager.LoadConfig();
        var handler = new PendingCleanupHandler();
        var service = new SetupGrantService(
            context,
            configManager,
            TimeProvider.System,
            () => new HttpClient(handler, disposeHandler: false));
        await service.IssueAsync("admin", "secret");
        handler.FailPreviousLogout = true;
        var rotated = await service.RenewAsync("admin", "secret");
        Assert.True(rotated.RevocationPending);

        var filter = new SetupGrantAuthFilter(service);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Headers[SetupGrantConstants.HeaderName] = rotated.Grant;
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var called = false;
        ActionExecutionDelegate next = () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(new ActionExecutedContext(actionContext, [], new object()));
        };

        await filter.OnActionExecutionAsync(executingContext, next);

        Assert.False(called);
        Assert.IsType<ConflictObjectResult>(executingContext.Result);
        handler.Dispose();
    }

    [Fact]
    public async Task Filter_AllowsRequestWithValidGrantHeader()
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-grant-filter-valid-{Guid.NewGuid():N}");
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        Directory.CreateDirectory(configPath);

        SetupGrantResult issued;
        await using (var context = await CreateMigratedContextAsync())
        {
            var configManager = new ConfigManager(new ConfigEncryptionService());
            await configManager.LoadConfig();
            var service = new SetupGrantService(context, configManager, TimeProvider.System, () => CreateJellyfinClient(true));
            issued = await service.IssueAsync("admin", "secret", CancellationToken.None);
        }

        await using var readContext = await CreateMigratedContextAsync();
        var readManager = new ConfigManager(new ConfigEncryptionService());
        await readManager.LoadConfig();
        var filter = new SetupGrantAuthFilter(new SetupGrantService(readContext, readManager, TimeProvider.System, () => CreateJellyfinClient(true)));

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.Headers[SetupGrantConstants.HeaderName] = issued.Grant;

        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        var executingContext = new ActionExecutingContext(actionContext, [], new Dictionary<string, object?>(), new object());
        var called = false;
        ActionExecutionDelegate next = () =>
        {
            called = true;
            return Task.FromResult<ActionExecutedContext>(new ActionExecutedContext(actionContext, [], new object()));
        };

        await filter.OnActionExecutionAsync(executingContext, next);

        Assert.True(called);
        Assert.Null(executingContext.Result);
    }

    private static HttpClient CreateJellyfinClient(bool administrator)
    {
        return new HttpClient(new JellyfinAuthenticationHandler(administrator), disposeHandler: true);
    }

    private static async Task<DavDatabaseContext> CreateMigratedContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    private sealed class PendingCleanupHandler : HttpMessageHandler
    {
        private int _authenticationCalls;
        public bool FailPreviousLogout { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/Users/AuthenticateByName")
            {
                var token = Interlocked.Increment(ref _authenticationCalls) == 1 ? "token-a" : "token-b";
                var body = JsonSerializer.Serialize(new
                {
                    AccessToken = token,
                    User = new { Policy = new { IsAdministrator = true } },
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                });
            }

            if (request.RequestUri?.AbsolutePath == "/Sessions/Logout")
            {
                var token = request.Headers.GetValues("X-Emby-Token").Single();
                if (FailPreviousLogout && token == "token-a")
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class JellyfinAuthenticationHandler(bool administrator) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonSerializer.Serialize(new
            {
                AccessToken = "token",
                User = new
                {
                    Policy = new
                    {
                        IsAdministrator = administrator,
                    },
                },
            });

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
