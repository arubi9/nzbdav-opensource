using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Api.Controllers.Setup;
using NzbWebDAV.Api.Filters;
using NzbWebDAV.Setup.Core;

namespace NzbWebDAV.Tests.Api.Controllers.Setup;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupStatusVerifyControllerTests
{
    [Fact]
    public async Task HandleApiRequest_RejectsMissingInternalApiKeyEvenWithSetupGrant()
    {
        using var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"));
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers[SetupGrantConstants.HeaderName] = "stale-grant";
        var controller = new SetupStatusVerifyController(null!)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
        };

        var result = await controller.HandleApiRequest();

        Assert.IsType<UnauthorizedObjectResult>(result);
    }

    [Fact]
    public void Controller_DoesNotAttachSetupGrantFilter()
    {
        var filters = typeof(SetupStatusVerifyController)
            .GetCustomAttributes(typeof(ServiceFilterAttribute), inherit: true)
            .Cast<ServiceFilterAttribute>();

        Assert.DoesNotContain(filters, filter => filter.ServiceType == typeof(SetupGrantAuthFilter));
    }
}
