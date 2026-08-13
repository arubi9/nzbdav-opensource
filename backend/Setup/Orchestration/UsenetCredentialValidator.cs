using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Exceptions;

namespace NzbWebDAV.Setup.Orchestration;

/// <summary>A deliberately small transport seam for setup-only NNTP validation.</summary>
public interface IUsenetCredentialValidationTransport : IDisposable
{
    Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken);
    Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken);
}

public delegate IUsenetCredentialValidationTransport UsenetCredentialValidationTransportFactory(
    UsenetProviderConfig.ConnectionDetails provider);

public enum UsenetCredentialValidationCode
{
    Valid,
    ConnectionFailed,
    AuthenticationFailed,
    TimedOut,
}

/// <summary>Only safe validation state is returned; credentials and upstream messages are discarded.</summary>
public sealed record UsenetCredentialValidationResult(bool Valid, UsenetCredentialValidationCode Code)
{
    public static UsenetCredentialValidationResult Success { get; } = new(true, UsenetCredentialValidationCode.Valid);
}

/// <summary>
/// Performs only the NNTP greeting/TLS connection and AUTH exchange. It never
/// issues an article command and bounds each provider independently.
/// </summary>
public sealed class UsenetCredentialValidator : IUsenetCredentialValidator
{
    public const int DefaultTimeoutSeconds = 5;
    private readonly TimeSpan _timeout;
    private readonly UsenetCredentialValidationTransportFactory _transportFactory;

    public UsenetCredentialValidator(
        TimeSpan? timeout = null,
        UsenetCredentialValidationTransportFactory? transportFactory = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        if (_timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _transportFactory = transportFactory ?? (provider => new BaseNntpValidationTransport());
    }

    public async Task<UsenetCredentialValidationResult> ValidateAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        IUsenetCredentialValidationTransport? transport = null;
        try
        {
            transport = _transportFactory(provider);
            await transport.ConnectAsync(provider.Host, provider.Port, provider.UseSsl, timeout.Token).ConfigureAwait(false);
            var authenticated = await transport.AuthenticateAsync(provider.User, provider.Pass, timeout.Token).ConfigureAwait(false);
            return authenticated
                ? UsenetCredentialValidationResult.Success
                : new(false, UsenetCredentialValidationCode.AuthenticationFailed);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, UsenetCredentialValidationCode.TimedOut);
        }
        catch (CouldNotLoginToUsenetException)
        {
            return new(false, UsenetCredentialValidationCode.AuthenticationFailed);
        }
        catch (CouldNotConnectToUsenetException)
        {
            return new(false, UsenetCredentialValidationCode.ConnectionFailed);
        }
        catch (HttpRequestException)
        {
            return new(false, UsenetCredentialValidationCode.ConnectionFailed);
        }
        catch (IOException)
        {
            return new(false, UsenetCredentialValidationCode.ConnectionFailed);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Upstream exception text can contain host/user/provider details;
            // expose only the bounded safe code.
            return new(false, UsenetCredentialValidationCode.ConnectionFailed);
        }
        finally
        {
            try
            {
                transport?.Dispose();
            }
            catch
            {
                // Validation already returned a safe result; disposal cannot
                // turn it into a credential-bearing error.
            }
        }
    }

    private sealed class BaseNntpValidationTransport : IUsenetCredentialValidationTransport
    {
        private readonly BaseNntpClient _client = new();

        public Task ConnectAsync(string host, int port, bool useSsl, CancellationToken cancellationToken)
            => _client.ConnectAsync(host, port, useSsl, cancellationToken);

        public async Task<bool> AuthenticateAsync(string user, string pass, CancellationToken cancellationToken)
        {
            var result = await _client.AuthenticateAsync(user, pass, cancellationToken).ConfigureAwait(false);
            return result.Success;
        }

        public void Dispose() => _client.Dispose();
    }
}

public interface IUsenetCredentialValidator
{
    Task<UsenetCredentialValidationResult> ValidateAsync(
        UsenetProviderConfig.ConnectionDetails provider,
        CancellationToken cancellationToken = default);
}
