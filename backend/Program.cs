using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NzbWebDAV.Api.Filters;
using NWebDav.Server;
using NWebDav.Server.Stores;
using NzbWebDAV.Api.SabControllers;
using NzbWebDAV.Auth;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Caching;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Interceptors;
using NzbWebDAV.Extensions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Metrics;
using NzbWebDAV.Queue;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;
using NzbWebDAV.WebDav;
using NzbWebDAV.WebDav.Base;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Websocket;
using Prometheus;
using Serilog;
using Serilog.Events;
using Serilog.Sinks.SystemConsole.Themes;

namespace NzbWebDAV;

public partial class Program
{
    static async Task Main(string[] args)
    {
        // Update thread-pool
        var coreCount = Environment.ProcessorCount;
        var minThreads = Math.Max(coreCount * 2, 50); // 2x cores, minimum 50
        var maxThreads = Math.Max(coreCount * 50, 1000); // 50x cores, minimum 1000
        ThreadPool.SetMinThreads(minThreads, minThreads);
        ThreadPool.SetMaxThreads(maxThreads, maxThreads);

        // Initialize logger
        var defaultLevel = LogEventLevel.Information;
        var envLevel = EnvironmentUtil.GetEnvironmentVariable("LOG_LEVEL");
        var level = Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed) ? parsed : defaultLevel;
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(level)
            .MinimumLevel.Override("NWebDAV", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Mvc", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.Routing", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.AspNetCore.DataProtection", LogEventLevel.Error)
            .Enrich.With<NzbWebDAV.Logging.ApiKeyRedactionEnricher>()
            .WriteTo.Console(theme: AnsiConsoleTheme.Code)
            .CreateLogger();

        // initialize database
        await using var databaseContext = new DavDatabaseContext();

        // run database migration, if necessary.
        if (args.Contains("--db-migration"))
        {
            var argIndex = args.ToList().IndexOf("--db-migration");
            var targetMigration = args.Length > argIndex + 1 ? args[argIndex + 1] : null;
            await databaseContext.Database
                .MigrateAsync(targetMigration, SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
            return;
        }

        if (!string.IsNullOrEmpty(EnvironmentUtil.GetEnvironmentVariable("DATABASE_URL")))
        {
            await databaseContext.Database
                .EnsureCreatedAsync(SigtermUtil.GetCancellationToken())
                .ConfigureAwait(false);
        }

        // initialize the config-manager
        var configManager = new ConfigManager();
        await configManager.LoadConfig().ConfigureAwait(false);
        ContentIndexSnapshotInterceptor.SnapshotWriter
            .SetDebounceInterval(TimeSpan.FromSeconds(configManager.GetSnapshotDebounceSeconds()));
        configManager.OnConfigChanged += (_, eventArgs) =>
        {
            if (!eventArgs.ChangedConfig.ContainsKey("cache.snapshot-debounce-seconds")) return;

            ContentIndexSnapshotInterceptor.SnapshotWriter
                .SetDebounceInterval(TimeSpan.FromSeconds(configManager.GetSnapshotDebounceSeconds()));
        };

        // initialize websocket-manager
        var websocketManager = new WebsocketManager();
        Log.Information("NZBDAV starting in {Role} mode", NodeRoleConfig.Current);

        // initialize webapp
        var builder = WebApplication.CreateBuilder(args);
        var maxRequestBodySize = EnvironmentUtil.GetLongVariable("MAX_REQUEST_BODY_SIZE") ?? 100 * 1024 * 1024;
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBodySize);
        builder.Services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(30));
        builder.Host.UseSerilog();
        builder.Services.AddControllers();
        // Rate limiting: track failed auth attempts per IP, not all requests.
        // The limiter is NOT applied globally — it's invoked manually by auth
        // filters only when credentials are invalid.
        builder.Services.AddHealthChecks()
            .AddCheck<NzbdavHealthCheck>("nzbdav");
        builder.Services
            .AddWebdavBasicAuthentication(configManager)
            .AddSingleton(configManager)
            .AddSingleton(websocketManager)
            .AddSingleton<LiveSegmentCache>()
            .AddSingleton<UsenetStreamingClient>()
            .AddSingleton<QueueManager>()
            .AddSingleton<ReadAheadWarmingService>()
            .AddSingleton<StreamExecutionService>()
            .AddSingleton<NzbdavMetricsCollector>()
            .AddScoped<DavDatabaseContext>()
            .AddScoped<DavDatabaseClient>()
            .AddScoped<DatabaseStore>()
            .AddScoped<IStore, DatabaseStore>()
            .AddScoped<GetAndHeadHandlerPatch>()
            .AddSingleton<AuthFailureTracker>()
            .AddHostedService<AuthFailureTrackerSweeper>()
            .AddSingleton<ApiKeyAuthFilter>()
            .AddScoped<SabApiController>();

        if (NodeRoleConfig.RunsIngest)
        {
            builder.Services
                .AddHostedService<ContentIndexRecoveryService>()
                .AddHostedService<HealthCheckService>()
                .AddHostedService<ArrMonitoringService>()
                .AddHostedService<BlobCleanupService>()
                .AddHostedService<SmallFilePrecacheService>()
                .AddHostedService<MediaProbeService>();
        }

        if (WebApplicationAuthExtensions.IsWebdavAuthDisabled())
            builder.Services.AddHostedService<InsecureAuthWarningService>();

        if (NodeRoleConfig.RunsStreaming)
        {
            builder.Services.AddNWebDav(opts =>
            {
                opts.Handlers["GET"] = typeof(GetAndHeadHandlerPatch);
                opts.Handlers["HEAD"] = typeof(GetAndHeadHandlerPatch);
                opts.Filter = opts.GetFilter();
                opts.RequireAuthentication = !WebApplicationAuthExtensions
                    .IsWebdavAuthDisabled();
            });
        }

        // run
        var app = builder.Build();
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UseRouting();
        app.UseHttpMetrics();
        app.UseMetricServer("/metrics");
        app.UseMiddleware<RequestTimeoutMiddleware>();
        app.UseMiddleware<ExceptionMiddleware>();
        app.UseWebSockets();
        app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
        {
            ResponseWriter = async (context, report) =>
            {
                context.Response.ContentType = "application/json";
                var result = new
                {
                    status = report.Status.ToString(),
                    checks = report.Entries.Select(e => new
                    {
                        name = e.Key,
                        status = e.Value.Status.ToString(),
                        description = e.Value.Description,
                        data = e.Value.Data
                    })
                };
                await System.Text.Json.JsonSerializer.SerializeAsync(
                    context.Response.Body, result, cancellationToken: context.RequestAborted
                ).ConfigureAwait(false);
            }
        }).AllowAnonymous();
        app.Map("/ws", websocketManager.HandleRoute);
        app.MapControllers();
        app.UseWebdavBasicAuthentication();
        if (NodeRoleConfig.RunsStreaming)
            app.UseNWebDav();
        _ = app.Services.GetRequiredService<NzbdavMetricsCollector>();
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            SigtermUtil.Cancel();
            ContentIndexSnapshotInterceptor.SnapshotWriter
                .FlushAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult();
        });
        await app.RunAsync().ConfigureAwait(false);
    }
}
