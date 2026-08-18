using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Xml;
using System.Xml.Linq;

namespace NzbWebDAV.Clients.Newznab;

/// <summary>Checks one or more Newznab credentials without persisting them.</summary>
public sealed class NewznabCapabilityClient
{
    private const int DefaultMaximumResponseBytes = 256 * 1024;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _timeout;
    private readonly int _maximumResponseBytes;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _hostResolver;

    public NewznabCapabilityClient(
        HttpClient httpClient,
        TimeSpan? timeout = null,
        int maximumResponseBytes = DefaultMaximumResponseBytes,
        Func<string, CancellationToken, Task<IPAddress[]>>? hostResolver = null,
        bool pinResolvedConnections = true)
    {
        // The argument is retained for source compatibility, but DNS pinning
        // is not a caller-controlled security switch.
        ArgumentNullException.ThrowIfNull(httpClient);
        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        if (maximumResponseBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));

        _timeout = timeout ?? DefaultTimeout;
        _maximumResponseBytes = maximumResponseBytes;
        _hostResolver = hostResolver ?? ((host, cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken));
    }

    public async Task<NewznabCapabilityResult> CheckAsync(
        NewznabIndexerCredential credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_timeout);
        var token = timeoutCancellation.Token;

        try
        {
            var approvedAddresses = await ResolveApprovedAddressesAsync(credential, token).ConfigureAwait(false);
            if (approvedAddresses is null)
                return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.UnsafeNetworkAddress);

            using var client = CreatePinnedClient(credential.BaseUrl, approvedAddresses);
            using var response = await client.GetAsync(
                BuildCapabilityUri(credential.BaseUrl),
                HttpCompletionOption.ResponseHeadersRead,
                token).ConfigureAwait(false);

            // This is defensive for a caller-controlled endpoint. The pinned
            // handler also has redirects disabled, so no second destination can
            // be contacted.
            if ((int)response.StatusCode is >= 300 and < 400)
                return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.HttpError, (int)response.StatusCode);

            if (!response.IsSuccessStatusCode)
            {
                var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? NewznabCapabilityStatus.InvalidCredentials
                    : NewznabCapabilityStatus.HttpError;
                return new NewznabCapabilityResult(credential.DisplayName, status, (int)response.StatusCode);
            }

            return ParseCapabilities(credential.DisplayName, await ReadBoundedAsync(response, token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.TimedOut);
        }
        catch (HttpRequestException)
        {
            return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.Unreachable);
        }
        catch (SocketException)
        {
            return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.Unreachable);
        }
        catch (IOException)
        {
            return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.Unreachable);
        }
        catch (ResponseTooLargeException)
        {
            return new NewznabCapabilityResult(credential.DisplayName, NewznabCapabilityStatus.MalformedResponse);
        }
    }

    public async Task<IReadOnlyList<NewznabCapabilityResult>> CheckAsync(
        IEnumerable<NewznabIndexerCredential> credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        var results = new List<NewznabCapabilityResult>();
        foreach (var credential in credentials)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await CheckAsync(credential ?? throw new ArgumentException("A credential is required.", nameof(credentials)), cancellationToken).ConfigureAwait(false));
        }
        return results;
    }

    private async Task<IPAddress[]?> ResolveApprovedAddressesAsync(
        NewznabIndexerCredential credential,
        CancellationToken cancellationToken)
    {
        var host = credential.BaseUrl.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
            addresses = [literal];
        else
        {
            try
            {
                // This is the only DNS lookup. ConnectCallback below never
                // delegates to DNS and can therefore not observe a rebinding.
                addresses = await _hostResolver(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException) { return null; }
            catch (ArgumentException) { return null; }
        }

        if (addresses is null || addresses.Length == 0
            || !NewznabUrlPolicy.IsAllowed(credential.BaseUrl, credential.AllowPrivateNetwork, addresses))
            return null;

        // Validate every answer to prevent hiding a private/restricted answer
        // behind a public first answer, then pin the approved answers. All of
        // them are pinned rather than just the first because a host with no
        // routable IPv6 must still reach an A record behind an AAAA answer.
        return addresses;
    }

    private static HttpClient CreatePinnedClient(Uri endpoint, IPAddress[] approvedAddresses)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, endpoint.Host, StringComparison.OrdinalIgnoreCase))
                    throw new HttpRequestException("The approved Newznab endpoint host changed.");

                Exception? lastFailure = null;
                foreach (var approvedAddress in approvedAddresses)
                {
                    var socket = new Socket(approvedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(approvedAddress, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception exception) when (exception is SocketException or IOException)
                    {
                        socket.Dispose();
                        lastFailure = exception;
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
                throw lastFailure ?? new HttpRequestException("The approved Newznab endpoint had no reachable address.");
            }
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength.HasValue && contentLength.Value > _maximumResponseBytes)
            throw new ResponseTooLargeException();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new MemoryStream(Math.Min(_maximumResponseBytes, 4096));
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length > _maximumResponseBytes - read)
                throw new ResponseTooLargeException();
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return output.ToArray();
    }

    private static NewznabCapabilityResult ParseCapabilities(string displayName, byte[] body)
    {
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = DefaultMaximumResponseBytes
            };
            using var input = new MemoryStream(body, writable: false);
            using var reader = XmlReader.Create(input, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            if (document.Root is null)
                return new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.MalformedResponse);
            if (document.Root.Name.LocalName == "error")
                return ErrorResult(displayName, document.Root);
            if (document.Root.Name.LocalName != "caps")
                return new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.MalformedResponse);

            var error = document.Root.Descendants().FirstOrDefault(element => element.Name.LocalName == "error");
            return error is null ? new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.Valid) : ErrorResult(displayName, error);
        }
        catch (XmlException) { return new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.MalformedResponse); }
        catch (InvalidOperationException) { return new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.MalformedResponse); }
    }

    private static NewznabCapabilityResult ErrorResult(string displayName, XElement error)
        => (string?)error.Attribute("code") is "100" or "101" or "102"
            ? new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.InvalidCredentials)
            : new NewznabCapabilityResult(displayName, NewznabCapabilityStatus.MalformedResponse);

    internal static Uri BuildCapabilityUri(Uri baseUri)
    {
        var builder = new UriBuilder(baseUri) { Fragment = string.Empty };
        // The operator enters one URL that is also handed to Prowlarr, whose
        // Newznab schema splits it into baseUrl (scheme + host, no path) and a
        // separate apiPath defaulting to "/api". A pathless base URL is
        // therefore correct input, not a full API endpoint, so mirror that
        // default here instead of querying the indexer's website root.
        if (builder.Path is "" or "/")
            builder.Path = "/api";
        var existingQuery = builder.Query.TrimStart('?');
        builder.Query = string.IsNullOrEmpty(existingQuery) ? "t=caps" : $"{existingQuery}&t=caps";
        return builder.Uri;
    }

    private sealed class ResponseTooLargeException : Exception;
}
