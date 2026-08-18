using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Authentication;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Utils;

namespace backend.Tests.Auth;

public sealed class ServiceCollectionAuthExtensionsTests
{
    [Fact]
    public async Task WebdavAuthVerifier_AllowsValidHashedPasswordOnly()
    {
        var harness = await CreateAuthEnvironmentAsync(PasswordUtil.Hash("backend-unit-password"));
        using var provider = harness.Provider;
        using var environment = harness.Environment;

        var options = provider.GetRequiredService<IOptionsMonitor<BasicAuthenticationOptions>>()
            .Get(BasicAuthenticationDefaults.AuthenticationScheme);

        var result = await ValidateCredentialsAsync(options, "unit-api", "backend-unit-password");
        Assert.NotNull(result.Principal);

        var invalid = await ValidateCredentialsAsync(options, "unit-api", "wrong-password");
        Assert.Null(invalid.Principal);
    }

    [Fact]
    public async Task WebdavAuthVerifier_RejectsStoredPlaintextPassword()
    {
        var harness = await CreateAuthEnvironmentAsync("backend-unit-password");
        using var provider = harness.Provider;
        using var environment = harness.Environment;

        var options = provider.GetRequiredService<IOptionsMonitor<BasicAuthenticationOptions>>()
            .Get(BasicAuthenticationDefaults.AuthenticationScheme);

        var result = await ValidateCredentialsAsync(options, "unit-api", "backend-unit-password");
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task WebdavAuthVerifier_RejectsMalformedPasswordHash()
    {
        var harness = await CreateAuthEnvironmentAsync("not-a-valid-hash");
        using var provider = harness.Provider;
        using var environment = harness.Environment;

        var options = provider.GetRequiredService<IOptionsMonitor<BasicAuthenticationOptions>>()
            .Get(BasicAuthenticationDefaults.AuthenticationScheme);

        var result = await ValidateCredentialsAsync(options, "unit-api", "backend-unit-password");
        Assert.Null(result.Principal);
    }

    [Fact]
    public async Task WebdavAuthVerifier_RejectsMissingCredentials()
    {
        var harness = await CreateAuthEnvironmentAsync(PasswordUtil.Hash("backend-unit-password"));
        using var provider = harness.Provider;
        using var environment = harness.Environment;

        var options = provider.GetRequiredService<IOptionsMonitor<BasicAuthenticationOptions>>()
            .Get(BasicAuthenticationDefaults.AuthenticationScheme);

        var missingUser = await ValidateCredentialsAsync(options, string.Empty, "backend-unit-password");
        var missingPassword = await ValidateCredentialsAsync(options, "unit-api", string.Empty);
        Assert.Null(missingUser.Principal);
        Assert.Null(missingPassword.Principal);
    }

    private static async Task<ValidateCredentialsContext> ValidateCredentialsAsync(BasicAuthenticationOptions options, string username, string password)
    {
        var httpContext = new DefaultHttpContext();
        var scheme = new AuthenticationScheme(
            BasicAuthenticationDefaults.AuthenticationScheme,
            BasicAuthenticationDefaults.AuthenticationScheme,
            typeof(TestAuthenticationHandler));

        var context = new ValidateCredentialsContext(httpContext, scheme, options)
        {
            Username = username,
            Password = password,
        };

        await options.Events.OnValidateCredentials(context);
        return context;
    }

    private static async Task<(ServiceProvider Provider, backend.Tests.Config.TemporaryEnvironment Environment)> CreateAuthEnvironmentAsync(string storedPassword)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"webdav-auth-{Guid.NewGuid():N}");
        var environment = new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", tempPath),
            ("DATABASE_URL", null),
            ("FRONTEND_BACKEND_API_KEY", "unit-api-key"));

        Directory.CreateDirectory(tempPath);

        await using (var context = new DavDatabaseContext())
        {
            await context.Database.MigrateAsync();
            context.ConfigItems.Add(new ConfigItem { ConfigName = "webdav.user", ConfigValue = "unit-api", IsEncrypted = false });
            context.ConfigItems.Add(new ConfigItem { ConfigName = "webdav.pass", ConfigValue = storedPassword, IsEncrypted = false });
            await context.SaveChangesAsync();
        }

        var services = new ServiceCollection();
        var encryption = new ConfigEncryptionService();
        var manager = new ConfigManager(encryption);
        await manager.LoadConfig();

        services.AddSingleton(manager);
        services.AddSingleton(encryption);
        services.AddAuthentication();
        services.AddWebdavBasicAuthentication(manager);

        return (services.BuildServiceProvider(), environment);
    }

    private sealed class TestAuthenticationHandler : IAuthenticationHandler
    {
        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context) => Task.CompletedTask;

        public Task<AuthenticateResult> AuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        public Task ChallengeAsync(AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(AuthenticationProperties? properties) => Task.CompletedTask;
    }
}
