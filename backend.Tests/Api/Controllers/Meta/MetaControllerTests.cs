using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NzbWebDAV.Api.Controllers.Meta;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Api.Controllers.Meta;

[Collection("Meta controller tests")]
public sealed class MetaControllerTests
{
    [Fact]
    public void Controller_ExposesExpectedRouteAndAuthAttributes()
    {
        var controllerType = typeof(MetaController);

        var apiController = controllerType.GetCustomAttribute<ApiControllerAttribute>();
        Assert.NotNull(apiController);

        var route = controllerType.GetCustomAttribute<RouteAttribute>();
        Assert.Equal("api/meta", route?.Template);

        var serviceFilter = controllerType.GetCustomAttribute<ServiceFilterAttribute>();
        Assert.NotNull(serviceFilter);
        Assert.Equal("ApiKeyAuthFilter", serviceFilter!.ServiceType.Name);

        var method = controllerType.GetMethod(nameof(MetaController.GetMeta));
        Assert.NotNull(method);

        var httpGet = method!.GetCustomAttribute<HttpGetAttribute>();
        Assert.Equal("{id:guid}", httpGet?.Template);
    }

    [Fact]
    public async Task GetMeta_ReturnsMappedMetadataForExistingItem()
    {
        using var scope = CreateTempConfigScope();
        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        var item = new DavItem
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            IdPrefix = "11111",
            CreatedAt = new DateTime(2026, 4, 6, 12, 30, 0, DateTimeKind.Utc),
            ParentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Name = "example.mkv",
            FileSize = 4242,
            Type = DavItem.ItemType.MultipartFile,
            Path = "/content/example.mkv",
        };

        dbContext.Items.Add(item);
        await dbContext.SaveChangesAsync().ConfigureAwait(false);

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem
            {
                ConfigName = "api.strm-key",
                ConfigValue = "unit-test-strm-key"
            }
        ]);

        var controller = new MetaController(new DavDatabaseClient(dbContext), configManager);

        var result = await controller.GetMeta(item.Id, CancellationToken.None).ConfigureAwait(false);
        var ok = Assert.IsType<OkObjectResult>(result);
        var response = Assert.IsType<MetaResponse>(ok.Value);

        Assert.Equal(item.Id, response.Id);
        Assert.Equal(item.Name, response.Name);
        Assert.Equal(item.Path, response.Path);
        Assert.Equal(item.Type.ToString(), response.Type);
        Assert.Equal(item.FileSize, response.FileSize);
        Assert.Equal(item.CreatedAt, response.CreatedAt);
        Assert.Equal(item.ParentId, response.ParentId);
        Assert.Equal(StreamExecutionService.GetContentType(item.Name), response.ContentType);
        Assert.Equal(StreamTokenService.GenerateToken($"/api/stream/{item.Id}", configManager), response.StreamToken);
    }

    [Fact]
    public async Task GetMeta_ReturnsNotFoundWhenItemIsMissing()
    {
        using var scope = CreateTempConfigScope();
        await using var dbContext = new DavDatabaseContext();
        await dbContext.Database.EnsureCreatedAsync().ConfigureAwait(false);

        var controller = new MetaController(new DavDatabaseClient(dbContext), new ConfigManager());

        var result = await controller.GetMeta(Guid.NewGuid(), CancellationToken.None).ConfigureAwait(false);
        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var error = notFound.Value!.GetType().GetProperty("error", BindingFlags.Instance | BindingFlags.Public)!.GetValue(notFound.Value);

        Assert.Equal("Item not found", error);
    }

    private static TempConfigScope CreateTempConfigScope()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"nzbdav-meta-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new TempConfigScope(configPath);
    }

    private sealed class TempConfigScope : IDisposable
    {
        private readonly string _configPath;

        public TempConfigScope(string configPath)
        {
            _configPath = configPath;
            Environment.SetEnvironmentVariable("CONFIG_PATH", configPath);
            Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "unit-test-api-key");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_configPath, recursive: true);
            }
            catch
            {
                // best effort cleanup for temp test data
            }
        }
    }
}

[CollectionDefinition("Meta controller tests", DisableParallelization = true)]
public sealed class MetaControllerTestsCollectionDefinition;
