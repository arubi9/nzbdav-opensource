using NzbWebDAV.Config;
using NzbWebDAV.Models;
using NzbWebDAV.Setup.Orchestration;

namespace NzbWebDAV.Tests.Setup.Orchestration;

public sealed class UsenetCredentialValidatorTests
{
    [Fact]
    public async Task ConnectsAndAuthenticatesWithoutAnArticleCommand()
    {
        var transport = new FakeTransport { Authenticated = true };
        var validator = new UsenetCredentialValidator(
            TimeSpan.FromSeconds(1),
            _ => transport);

        var result = await validator.ValidateAsync(Provider());

        Assert.True(result.Valid);
        Assert.Equal(UsenetCredentialValidationCode.Valid, result.Code);
        Assert.Equal(1, transport.ConnectCount);
        Assert.Equal(1, transport.AuthenticateCount);
        Assert.Equal(0, transport.ArticleCommandCount);
    }

    [Fact]
    public async Task ReportsAuthenticationFailureWithoutReturningUpstreamText()
    {
        var validator = new UsenetCredentialValidator(
            TimeSpan.FromSeconds(1),
            _ => new FakeTransport { Authenticated = false });

        var result = await validator.ValidateAsync(Provider("secret-user", "secret-pass"));

        Assert.False(result.Valid);
        Assert.Equal(UsenetCredentialValidationCode.AuthenticationFailed, result.Code);
        Assert.DoesNotContain("secret-pass", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-user", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BoundsAStalledProviderByItsOwnTimeout()
    {
        var validator = new UsenetCredentialValidator(
            TimeSpan.FromMilliseconds(20),
            _ => new FakeTransport { BlockConnect = true });

        var result = await validator.ValidateAsync(Provider());

        Assert.False(result.Valid);
        Assert.Equal(UsenetCredentialValidationCode.TimedOut, result.Code);
    }

    [Fact]
    public async Task CallerCancellationIsNotConvertedToAProviderFailure()
    {
        var cancellation = new CancellationTokenSource();
        var validator = new UsenetCredentialValidator(
            TimeSpan.FromSeconds(1),
            _ => new FakeTransport { BlockConnect = true });
        var pending = validator.ValidateAsync(Provider(), cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    private static UsenetProviderConfig.ConnectionDetails Provider(string user = "user", string pass = "pass")
        => new()
        {
            Type = ProviderType.Pooled,
            Host = "provider.example",
            Port = 563,
            UseSsl = true,
            User = user,
            Pass = pass,
            MaxConnections = 2,
        };

    private sealed class FakeTransport : IUsenetCredentialValidationTransport
    {
        public bool Authenticated { get; init; }
        public bool BlockConnect { get; init; }
        public int ConnectCount { get; private set; }
        public int AuthenticateCount { get; private set; }
        public int ArticleCommandCount { get; private set; }

        public async Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
        {
            ConnectCount++;
            if (BlockConnect)
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        public Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
        {
            AuthenticateCount++;
            return Task.FromResult(Authenticated);
        }

        public void Dispose() { }
    }
}
