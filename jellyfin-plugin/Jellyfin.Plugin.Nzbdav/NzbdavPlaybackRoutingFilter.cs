using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.Nzbdav;

/// <summary>
/// Keep NZBDAV HTTP playback on Jellyfin's authenticated, range-capable static
/// stream endpoint instead of sending clients to the backend's internal URL.
/// The same applies to any HTTP source on a single-label host (a Docker service
/// name such as <c>zurg</c>): only Jellyfin can resolve it, never a client.
/// </summary>
public sealed class NzbdavPlaybackRoutingFilter : IResultFilter
{
    private readonly Func<string?> _baseUrlAccessor;

    public NzbdavPlaybackRoutingFilter()
        : this(() => Plugin.Instance?.Configuration.NzbdavBaseUrl)
    {
    }

    internal NzbdavPlaybackRoutingFilter(Func<string?> baseUrlAccessor)
    {
        _baseUrlAccessor = baseUrlAccessor;
    }

    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: PlaybackInfoResponse response })
            return;
        Uri? backend = TryHttpUri(_baseUrlAccessor()?.Trim(), out var configured)
            && string.IsNullOrEmpty(configured.Query) ? configured : null;

        // Run after device-profile negotiation. Changing only the custom media
        // source provider misses Jellyfin's built-in sources for imported .strm
        // files (including /view/.ids URLs outside the plugin's LibraryPath).
        foreach (var source in response.MediaSources)
        {
            if (source.Protocol == MediaProtocol.Http
                && source.SupportsDirectPlay
                && source.SupportsDirectStream
                && (IsInternalHostStream(source.Path)
                    || (backend is not null && IsBackendStream(source.Path, backend))))
            {
                // The client now selects /Videos/{id}/stream?Static=true. Keep
                // the backend Path for Jellyfin, and preserve negotiated stream
                // copy/transcode policy; do not force a re-encode or grant rights.
                source.SupportsDirectPlay = false;
            }
        }
    }

    public void OnResultExecuted(ResultExecutedContext context) { }

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
