using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NWebDav.Server.Authentication;
using NzbWebDAV.Auth;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;

namespace backend.Tests.Auth;

[Collection(nameof(backend.Tests.Services.ConfigEncryptionDatabaseCollection))]
public sealed class ServiceCollectionAuthExtensionsTests
{
    private static readonly MethodInfo ValidateCredentialsMethod =
        typeof(ServiceCollectionAuthExtensions)
            .GetMethod("ValidateCredentials", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find the ValidateCredentials method.");

    private readonly backend.Tests.Services.ConfigEncryptionDatabaseFixture _fixture;

    public ServiceCollectionAuthExtensionsTests(backend.Tests.Services.ConfigEncryptionDatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task ValidateCredentials_WithHashedPassword_Authenticates()
    {
        var plainPassword = "correct-password";
        var configManager = await CreateConfigManagerAsync(PasswordUtil.Hash(plainPassword));

        var context = await InvokeValidateCredentialsAsync(configManager, "admin", plainPassword);

        var identity = Assert.IsType<ClaimsIdentity>(context.Principal?.Identity);
        Assert.True(identity.IsAuthenticated);
        Assert.Equal("admin", context.Principal!.Identity!.Name);
    }

    [Fact]
    public async Task ValidateCredentials_WithWrongPassword_Fails()
    {
        var configManager = await CreateConfigManagerAsync(PasswordUtil.Hash("correct-password"));

        var context = await InvokeValidateCredentialsAsync(configManager, "admin", "wrong-password");

        Assert.Null(context.Principal);
    }

    [Theory]
    [InlineData("correct-password")]
    [InlineData("malformed-or-plaintext")]
    public async Task ValidateCredentials_WithMalformedOrPlaintextPassword_FailsWithoutThrowing(string storedPassword)
    {
        var configManager = await CreateConfigManagerAsync(storedPassword);

        var context = await InvokeValidateCredentialsAsync(configManager, "admin", "correct-password");

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidateCredentials_MissingUser_IsRejected()
    {
        var configManager = await CreateConfigManagerAsync(PasswordUtil.Hash("correct-password"));

        var context = await InvokeValidateCredentialsAsync(configManager, null, "correct-password");

        Assert.Null(context.Principal);
    }

    [Fact]
    public async Task ValidateCredentials_MissingPassword_IsRejected()
    {
        var configManager = await CreateConfigManagerAsync(PasswordUtil.Hash("correct-password"));

        var context = await InvokeValidateCredentialsAsync(configManager, "admin", null);

        Assert.Null(context.Principal);
    }

    private async Task<ConfigManager> CreateConfigManagerAsync(string passwordHash)
    {
        await _fixture.ResetAsync();
        await using var setupContext = await _fixture.CreateMigratedContextAsync();
        setupContext.ConfigItems.AddRange(
        [
            new ConfigItem
            {
                ConfigName = "webdav.user",
                ConfigValue = "admin",
                IsEncrypted = false
            },
            new ConfigItem
            {
                ConfigName = "webdav.pass",
                ConfigValue = passwordHash,
                IsEncrypted = false
            }
        ]);
        await setupContext.SaveChangesAsync();

        var configManager = new ConfigManager();
        await configManager.LoadConfig();
        return configManager;
    }

    private static async Task<ValidateCredentialsContext> InvokeValidateCredentialsAsync(
        ConfigManager configManager,
        string? username,
        string? password)
    {
        var handlerType = typeof(ValidateCredentialsContext).Assembly.GetType(
            "NWebDav.Server.Authentication.BasicAuthenticationHandler",
            throwOnError: false);

        if (handlerType is null)
            throw new InvalidOperationException("Could not find the BasicAuthenticationHandler type.");

        var authContext = new ValidateCredentialsContext(
            new DefaultHttpContext(),
            new AuthenticationScheme("Basic", "Basic", handlerType),
            new BasicAuthenticationOptions())
        {
            Username = username!,
            Password = password!
        };

        var task = ValidateCredentialsMethod.Invoke(null, [authContext, configManager])
                   ?? throw new InvalidOperationException("ValidateCredentials did not return a task.");

        await (Task)task;
        return authContext;
    }
}
