using System.Net;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Keep NZBDAV HTTP playback on Jellyfin's authenticated, range-capable static
/// stream endpoint instead of sending clients to the backend's internal URL.
/// The same applies to any HTTP source on a single-label host (a Docker service
/// name such as <c>zurg</c>): only Jellyfin can resolve it, never a client.
/// Such a source's Path is replaced with that Jellyfin endpoint for clients
/// (Infuse) that open Path directly.
/// A .strm source's Size is the pointer file's length (tens of bytes), because
/// Jellyfin sets Size from the file on disk. The real size is looked up from the
/// stream itself so clients that check Size (Infuse) see the video's length.
/// </summary>
public sealed class NzbdavPlaybackRoutingFilter : IAsyncResultFilter
{
    // Larger than any .strm pointer file, smaller than any video.
    private const long PointerFileMaxSize = 1024 * 1024;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Func<string?> _baseUrlAccessor;
    private readonly Func<string, CancellationToken, Task<StreamInfo?>> _probe;

    public NzbdavPlaybackRoutingFilter()
        : this(() => Plugin.Instance?.Configuration.NzbdavBaseUrl)
    {
    }

    internal NzbdavPlaybackRoutingFilter(
        Func<string?> baseUrlAccessor,
        Func<string, CancellationToken, Task<StreamInfo?>>? probe = null)
    {
        _baseUrlAccessor = baseUrlAccessor;
        _probe = probe ?? ProbeStreamAsync;
    }

    internal sealed record StreamInfo(long Size, string? FileName);

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        // Single items only: a list of hundreds would cost one debrid link
        // lookup per entry.
        var sources = (context.Result as ObjectResult)?.Value switch
        {
            PlaybackInfoResponse r => r.MediaSources,
            BaseItemDto d => d.MediaSources,
            _ => null
        };
        if (sources is not null)
        {
            var backend = Backend();
            await Task.WhenAll(sources
                .Where(s => s.Size is null or < PointerFileMaxSize && IsRouted(s, backend))
                .Select(s => FixSizeAsync(s, context.HttpContext.RequestAborted))).ConfigureAwait(false);
        }

        OnResultExecuting(context);
        await next().ConfigureAwait(false);
    }

    private async Task FixSizeAsync(MediaSourceInfo source, CancellationToken cancellationToken)
    {
        StreamInfo? info;
        try
        {
            info = await _probe(source.Path!, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return; // Keep Jellyfin's value; playback itself does not need it.
        }

        if (info is null || info.Size < PointerFileMaxSize)
            return;
        source.Size = info.Size;
        var ext = Path.GetExtension(info.FileName ?? string.Empty).TrimStart('.').ToLowerInvariant();
        if (string.Equals(source.Container, "strm", StringComparison.OrdinalIgnoreCase) && ext is "mkv" or "mp4" or "m4v" or "avi" or "ts" or "m2ts" or "mov" or "webm" or "wmv")
            source.Container = ext;
    }

    // One byte is enough: the total length is in Content-Range. Headers only;
    // disposing the response drops the connection before the body is read.
    private static async Task<StreamInfo?> ProbeStreamAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        var size = response.StatusCode switch
        {
            HttpStatusCode.PartialContent => response.Content.Headers.ContentRange?.Length,
            HttpStatusCode.OK => response.Content.Headers.ContentLength,
            _ => null
        };
        if (size is null)
            return null;
        var disposition = response.Content.Headers.ContentDisposition;
        var name = disposition?.FileNameStar ?? disposition?.FileName?.Trim('"')
            ?? response.RequestMessage?.RequestUri?.Segments.LastOrDefault();
        return new StreamInfo(size.Value, name is null ? null : Uri.UnescapeDataString(name));
    }

    private Uri? Backend() =>
        TryHttpUri(_baseUrlAccessor()?.Trim(), out var configured)
            && string.IsNullOrEmpty(configured.Query) ? configured : null;

    private static bool IsRouted(MediaSourceInfo source, Uri? backend) =>
        source.Protocol == MediaProtocol.Http
        && (IsInternalHostStream(source.Path)
            || (backend is not null && IsBackendStream(source.Path, backend)));

    internal void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: PlaybackInfoResponse response })
            return;
        var backend = Backend();

        // Run after device-profile negotiation. Changing only the custom media
        // source provider misses Jellyfin's built-in sources for imported .strm
        // files (including /view/.ids URLs outside the plugin's LibraryPath).
        var request = context.HttpContext.Request;
        var token = RequestToken(request);
        foreach (var source in response.MediaSources)
        {
            if (!IsRouted(source, backend))
                continue;

            // Well-behaved clients now select /Videos/{id}/stream?Static=true.
            // Preserve negotiated stream copy/transcode policy; do not force
            // a re-encode or grant rights.
            if (source.SupportsDirectPlay && source.SupportsDirectStream)
                source.SupportsDirectPlay = false;

            // Infuse (8.1.8+) ignores those flags and opens Path itself, even
            // when negotiation already disabled direct play. Point Path at the
            // authenticated Jellyfin endpoint, with the token this client just
            // presented, so it never sees an internal URL.
            if (token is not null && !string.IsNullOrEmpty(source.Id))
                source.Path = StaticStreamUrl(request, source.Id, token);
        }
    }

    private static readonly Regex AuthToken = new(
        @"(?:^|[\s,])Token=""?([^"",\s]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string? RequestToken(HttpRequest request)
    {
        foreach (var name in new[] { "X-Emby-Token", "X-MediaBrowser-Token" })
        {
            var value = request.Headers[name].ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }

        foreach (var name in new[] { "Authorization", "X-Emby-Authorization" })
        {
            var match = AuthToken.Match(request.Headers[name].ToString());
            if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value);
        }

        foreach (var name in new[] { "ApiKey", "api_key" })
        {
            var value = request.Query[name].ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    // Absolute URL on the origin the client used; behind cloudflared the Host is
    // the public name and X-Forwarded-Proto carries https.
    private static string StaticStreamUrl(HttpRequest request, string sourceId, string token)
    {
        var proto = request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim().ToLowerInvariant();
        var scheme = proto is "http" or "https" ? proto : request.Scheme;
        var id = Uri.EscapeDataString(sourceId);
        return $"{scheme}://{request.Host.Value}{request.PathBase.Value}/Videos/{id}/stream"
            + $"?Static=true&MediaSourceId={id}&ApiKey={Uri.EscapeDataString(token)}";
    }

    // A dotless DNS name (not an IP literal) is resolvable only on Jellyfin's
    // side, e.g. through Docker's embedded DNS; a remote client cannot reach it.
    private static bool IsInternalHostStream(string? path) =>
        TryHttpUri(path, out var stream)
        && stream.HostNameType == UriHostNameType.Dns
        && !stream.IdnHost.Contains('.');

    private static bool IsBackendStream(string? path, Uri backend)
    {
        if (!TryHttpUri(path, out var stream)
            || !string.Equals(stream.Scheme, backend.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(stream.IdnHost, backend.IdnHost, StringComparison.OrdinalIgnoreCase)
            || stream.Port != backend.Port)
            return false;

        var prefix = backend.AbsolutePath.TrimEnd('/') + "/";
        if (!stream.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var route = stream.AbsolutePath[prefix.Length..];
        const string apiPrefix = "api/stream/";
        if (route.StartsWith(apiPrefix, StringComparison.Ordinal))
            return Guid.TryParseExact(route[apiPrefix.Length..], "D", out _);

        const string viewPrefix = "view/.ids/";
        if (!route.StartsWith(viewPrefix, StringComparison.Ordinal))
            return false;

        var parts = route[viewPrefix.Length..].Split('/');
        if (parts.Length != 6 || !Guid.TryParseExact(parts[5], "D", out var id))
            return false;

        var compactId = id.ToString("N");
        for (var i = 0; i < 5; i++)
        {
            if (parts[i].Length != 1 || char.ToLowerInvariant(parts[i][0]) != compactId[i])
                return false;
        }
        return true;
    }

    private static bool TryHttpUri(string? value, out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(parsed.Host)
            || !string.IsNullOrEmpty(parsed.UserInfo)
            || !string.IsNullOrEmpty(parsed.Fragment))
            return false;

        uri = parsed;
        return true;
    }
}
