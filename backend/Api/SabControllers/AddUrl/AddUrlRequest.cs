using Microsoft.AspNetCore.Http;
using NzbWebDAV.Api.SabControllers.AddFile;
using NzbWebDAV.Config;
using NzbWebDAV.Extensions;
using NzbWebDAV.Utils;
using System.Net;
using System.Net.Sockets;

namespace NzbWebDAV.Api.SabControllers.AddUrl;

public class AddUrlRequest() : AddFileRequest
{
    private static readonly HttpClient HttpClientInstance = InitializeHttpClient();
    private const int MaxAutomaticRedirections = 10;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static async Task<AddUrlRequest> New(HttpContext context, ConfigManager configManager)
    {
        var nzbUrl = context.GetQueryParam("name");
        var nzbName = context.GetQueryParam("nzbname");
        var userAgent = configManager.GetUserAgent();
        var nzbFile = await GetNzbFile(nzbUrl, nzbName, userAgent).ConfigureAwait(false);
        return new AddUrlRequest()
        {
            FileName = nzbFile.FileName,
            MimeType = nzbFile.ContentType,
            NzbFileStream = nzbFile.FileStream,
            Category = context.GetQueryParam("cat") ?? configManager.GetManualUploadCategory(),
            Priority = MapPriorityOption(context.GetQueryParam("priority")),
            PostProcessing = MapPostProcessingOption(context.GetQueryParam("pp")),
            CancellationToken = context.RequestAborted
        };
    }

    private static async Task<NzbFileResponse> GetNzbFile(string? url, string? nzbName, string userAgent)
    {
        try
        {
            // validate url
            if (string.IsNullOrWhiteSpace(url))
                throw new Exception($"The url is invalid.");

            // fetch url
            var response = await GetAsync(url, userAgent).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new Exception($"Received status code {response.StatusCode}.");

            // read the content type
            var contentType = response.Content.Headers.ContentType?.MediaType;

            // determine the filename
            var fileName = AddNzbExtension(nzbName)
                           ?? GetFilenameFromResponseHeader(response)
                           ?? GetFilenameFromUrl(url)
                           ?? throw new Exception("Nzb filename could not be determined.");

            // read the file contents
            var fileStream = await response.Content.ReadAsStreamAsync();

            // return response
            return new NzbFileResponse
            {
                FileName = fileName,
                ContentType = contentType,
                FileStream = fileStream
            };
        }
        catch (Exception ex)
        {
            throw new BadHttpRequestException($"Failed to fetch nzb-file url `{url}`: {ex.Message}");
        }
    }

    private static string? AddNzbExtension(string? nzbName)
    {
        return nzbName == null ? null
            : nzbName.ToLower().EndsWith("nzb") ? nzbName
            : $"{nzbName}.nzb";
    }

    private static async Task<HttpResponseMessage> GetAsync(string url, string userAgent)
    {
        var httpClient = HttpClientInstance;
        var currentUri = await ValidateRemoteUriAsync(url).ConfigureAwait(false);
        var response = await SendGetAsync(httpClient, currentUri, userAgent).ConfigureAwait(false);
        var remainingRedirects = MaxAutomaticRedirections;
        while
        (
            (int)response.StatusCode is >= 300 and < 400
            && remainingRedirects > 0
            && response.Headers.Location is not null
        )
        {
            var redirect = response.Headers.Location;
            var redirectUri = redirect.IsAbsoluteUri ? redirect : new Uri(currentUri, redirect);
            if (currentUri.Scheme == Uri.UriSchemeHttps
                && redirectUri.Scheme == Uri.UriSchemeHttp
                && !EnvironmentUtil.IsVariableTrue("ALLOW_HTTPS_TO_HTTP_REDIRECTS"))
            {
                break;
            }

            currentUri = await ValidateRemoteUriAsync(redirectUri).ConfigureAwait(false);
            response.Dispose();
            response = await SendGetAsync(httpClient, currentUri, userAgent).ConfigureAwait(false);
            remainingRedirects--;
        }

        return response;
    }

    private static async Task<HttpResponseMessage> SendGetAsync(HttpClient httpClient, Uri uri, string userAgent)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!request.Headers.UserAgent.TryParseAdd(userAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        return await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead)
            .ConfigureAwait(false);
    }

    private static HttpClient InitializeHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectCallback = ConnectToValidatedRemoteAsync,
        };
        return new HttpClient(handler)
        {
            Timeout = RequestTimeout
        };
    }

    internal static async Task<Uri> ValidateRemoteUriAsync(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new BadHttpRequestException("The url must be absolute.");

        return await ValidateRemoteUriAsync(uri).ConfigureAwait(false);
    }

    internal static async Task<Uri> ValidateRemoteUriAsync(Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new BadHttpRequestException("The url scheme must be http or https.");

        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new BadHttpRequestException("The url host is required.");

        await ResolveAllowedAddressesAsync(uri.DnsSafeHost).ConfigureAwait(false);
        return uri;
    }

    private static async ValueTask<Stream> ConnectToValidatedRemoteAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var addresses = await ResolveAllowedAddressesAsync(context.DnsEndPoint.Host, cancellationToken)
            .ConfigureAwait(false);
        var address = addresses[0];
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<IPAddress[]> ResolveAllowedAddressesAsync(
        string host,
        CancellationToken cancellationToken = default)
    {
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new BadHttpRequestException("The url host could not be resolved.", ex);
        }

        if (addresses.Length == 0 || addresses.Any(IsBlockedAddress))
            throw new BadHttpRequestException("The url host resolves to a blocked address.");

        return addresses;
    }

    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            return IsBlockedAddress(address.MapToIPv4());

        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                0 => true,
                10 => true,
                127 => true,
                169 when bytes[1] == 254 => true,
                172 when bytes[1] is >= 16 and <= 31 => true,
                192 when bytes[1] == 0 => true,
                192 when bytes[1] == 168 => true,
                198 when bytes[1] is 18 or 19 => true,
                100 when bytes[1] is >= 64 and <= 127 => true,
                >= 224 => true,
                _ => false
            };
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal
                   || address.IsIPv6SiteLocal
                   || address.IsIPv6Multicast
                   || address.Equals(IPAddress.IPv6None)
                   || address.Equals(IPAddress.IPv6Any)
                   || (bytes[0] & 0xfe) == 0xfc;
        }

        return true;
    }

    private static string? GetFilenameFromResponseHeader(HttpResponseMessage response)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var filename = contentDisposition?.FileName?.Trim('"');
        return StringUtil.EmptyToNull(filename);
    }

    private static string? GetFilenameFromUrl(string url)
    {
        try
        {
            var filename = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(filename)) return null;
            filename = Uri.UnescapeDataString(filename);
            filename = AddNzbExtension(filename);
            return filename;
        }
        catch
        {
            return null;
        }
    }

    private class NzbFileResponse
    {
        public required string FileName { get; init; }
        public required string? ContentType { get; init; }
        public required Stream FileStream { get; init; }
    }
}
