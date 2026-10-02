using Jellyfin.Plugin.Nzbdav;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavPlaybackRoutingFilterTests
{
    private const string Id = "e64caeca-d2b6-43c1-a9e6-ab61be307efb";
    private const string ViewPath = "/view/.ids/e/6/4/c/a/" + Id;
    private const string ApiPath = "/api/stream/" + Id;

    [Theory]
    [InlineData(ViewPath, true)]
    [InlineData(ApiPath, false)]
    public void RoutesBuiltInAndPluginSourcesThroughJellyfinWithoutForcingTranscoding(string path, bool supportsProbing)
    {
        // The imported /data/media .strm is not plugin-owned and has no sidecar.
        // Apply this AFTER Jellyfin's device-profile negotiation, to either source.
        var source = Source("http://nzbdav:8080" + path);
        source.Path += "?opaque=fixture";
        source.SupportsProbing = supportsProbing;
        source.Size = supportsProbing ? 166 : 7000000000;
        source.Bitrate = 22000000;
        source.TranscodingUrl = "/videos/fallback/master.m3u8";
        var originalPath = source.Path;
        var response = new PlaybackInfoResponse { MediaSources = [source] };
        Filter(response);
        Assert.False(source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        Assert.Equal(originalPath, source.Path);
        Assert.Equal("/videos/fallback/master.m3u8", source.TranscodingUrl);
        Assert.Equal(supportsProbing, source.SupportsProbing);
        Assert.Equal(22000000, source.Bitrate);
        Assert.Equal(supportsProbing ? 166 : 7000000000, source.Size);
    }

    [Theory]
    [InlineData("http://NZBDAV:8080/", "http://nzbdav:8080" + ViewPath)]
    [InlineData("https://backend.example/nzb/", "https://backend.example/nzb" + ApiPath)]
    public void MatchesConfiguredOriginAndBasePath(string baseUrl, string path)
    {
        var source = Source(path);
        Filter(new PlaybackInfoResponse { MediaSources = [source] }, baseUrl);
        Assert.False(source.SupportsDirectPlay);
    }

    // Dotted hosts: single-label hosts are always routed (see below), so the
    // backend-match rules are exercised on a client-resolvable name.
    [Theory]
    [InlineData("http://other.lan:8080" + ViewPath)]
    [InlineData("http://nzbdav.example:8080" + ViewPath)]
    [InlineData("http://nzbdav.lan:8081" + ViewPath)]
    [InlineData("https://nzbdav.lan:8080" + ViewPath)]
    [InlineData("http://nzbdav.lan:8080/api/stream/not-a-guid")]
    [InlineData("http://nzbdav.lan:8080" + ApiPath + "/tail")]
    [InlineData("http://nzbdav.lan:8080/view/.ids/x/6/4/c/a/" + Id)]
    [InlineData("http://nzbdav.lan:8080/other/movie.mkv")]
    [InlineData("http://nzbdav.lan:8080/other" + ApiPath)]
    [InlineData("http://user@nzbdav:8080" + ApiPath)]
    [InlineData("http://nzbdav:8080" + ApiPath + "#fragment")]
    [InlineData("/data/media/movie.mkv")]
    [InlineData(null)]
    public void LeavesUnrelatedOrMalformedSourcesUnchanged(string? path)
    {
        var source = Source(path);
        Filter(new PlaybackInfoResponse { MediaSources = [source] }, "http://nzbdav.lan:8080");
        Assert.True(source.SupportsDirectPlay);
    }

    [Theory]
    [InlineData("http://zurg:9999/strm/TEHFAXYF5KQ7IC2D", null, false)]
    [InlineData("http://zurg:9999/strm/TEHFAXYF5KQ7IC2D", "http://nzbdav:8080", false)]
    [InlineData("http://nzbdav:8080/other/movie.mkv", "not-a-url", false)]
    [InlineData("http://192.168.1.116:9999/strm/TEHFAXYF5KQ7IC2D", null, true)]
    [InlineData("http://[::1]:9999/strm/TEHFAXYF5KQ7IC2D", null, true)]
    [InlineData("https://cdn.example/movie.mkv", null, true)]
    public void RoutesSingleLabelHostsThroughJellyfinWhateverTheConfiguration(string path, string? baseUrl, bool directPlay)
    {
        // Docker service names resolve only inside Jellyfin's network (zurg's
        // .strm files point at http://zurg:9999); a remote client cannot reach them.
        var source = Source(path);
        Filter(new PlaybackInfoResponse { MediaSources = [source] }, baseUrl);
        Assert.Equal(directPlay, source.SupportsDirectPlay);
        Assert.True(source.SupportsDirectStream);
        Assert.Equal(path, source.Path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-url")]
    [InlineData("file:///tmp")]
    [InlineData("http://user@nzbdav.lan:8080")]
    [InlineData("http://nzbdav.lan:8080?invalid=true")]
    [InlineData("http://nzbdav.lan:8080#fragment")]
    [InlineData("http://nzbdav.lan:8080/nzb")]
    public void InvalidOrDifferentConfigurationDoesNotChangePlayback(string? baseUrl)
    {
        var source = Source("http://nzbdav.lan:8080" + ViewPath);
        Filter(new PlaybackInfoResponse { MediaSources = [source] }, baseUrl);
        Assert.True(source.SupportsDirectPlay);
    }

    [Fact]
    public void PreservesNegotiatedTranscodeAndRestrictedDirectStreamPolicies()
    {
        var transcodeOnly = Source("http://nzbdav:8080" + ViewPath);
        transcodeOnly.SupportsDirectPlay = false;
        transcodeOnly.SupportsDirectStream = false;
        transcodeOnly.TranscodingUrl = "/videos/fallback/master.m3u8";
        var directOnly = Source("http://nzbdav:8080" + ApiPath);
        directOnly.SupportsDirectStream = false;
        directOnly.SupportsTranscoding = false;
        Filter(new PlaybackInfoResponse { MediaSources = [transcodeOnly, directOnly] });
        Assert.False(transcodeOnly.SupportsDirectPlay);
        Assert.False(transcodeOnly.SupportsDirectStream);
        Assert.True(transcodeOnly.SupportsTranscoding);
        Assert.Equal("/videos/fallback/master.m3u8", transcodeOnly.TranscodingUrl);
        Assert.True(directOnly.SupportsDirectPlay);
        Assert.False(directOnly.SupportsDirectStream);
        Assert.False(directOnly.SupportsTranscoding);
    }

    [Fact]
    public void DoesNotChangeNonPlaybackResponsesOrLocalProtocol()
    {
        var source = Source("http://nzbdav:8080" + ViewPath);
        Filter(new BaseItemDto { MediaSources = [source] });
        Assert.True(source.SupportsDirectPlay);
        source.Protocol = MediaProtocol.File;
        Filter(new PlaybackInfoResponse { MediaSources = [source] });
        Assert.True(source.SupportsDirectPlay);
    }

    [Theory]
    [InlineData("http://zurg:9999/strm/TEHFAXYF5KQ7IC2D")]
    [InlineData("http://nzbdav:8080" + ViewPath)]
    public void PointsPathAtJellyfinForClientsThatOpenItDirectly(string path)
    {
        // Infuse opens MediaSource.Path itself; the internal host is unreachable.
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("watch.example");
        http.Request.Headers["X-Forwarded-Proto"] = "https";
        http.Request.Headers["X-Emby-Authorization"] =
            "MediaBrowser Client=\"Infuse-Direct\", Device=\"iPhone\", DeviceId=\"d1\", Version=\"8.5.5\", Token=\"tok123\"";
        var source = Source(path);
        // Negotiation may already have refused direct play (seen on NZB sources).
        var negotiated = Source(path);
        negotiated.SupportsDirectPlay = false;
        Filter(new PlaybackInfoResponse { MediaSources = [source, negotiated] }, http: http);
        Assert.False(source.SupportsDirectPlay);
        var expected = $"https://watch.example/Videos/{Id}/stream?Static=true&MediaSourceId={Id}&ApiKey=tok123";
        Assert.Equal(expected, source.Path);
        Assert.Equal(expected, negotiated.Path);
        Assert.True(negotiated.SupportsDirectStream);
    }

    [Fact]
    public void UsesQueryTokenRequestSchemeAndPathBase()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("192.168.1.116:8096");
        http.Request.PathBase = "/jf";
        http.Request.QueryString = new QueryString("?ApiKey=a%2Bb");
        var source = Source("http://zurg:9999/strm/X");
        Filter(new PlaybackInfoResponse { MediaSources = [source] }, http: http);
        Assert.Equal(
            $"http://192.168.1.116:8096/jf/Videos/{Id}/stream?Static=true&MediaSourceId={Id}&ApiKey=a%2Bb",
            source.Path);
    }

    [Fact]
    public void LeavesReachableSourcesAndItemDtosAloneEvenWithAToken()
    {
        var http = new DefaultHttpContext();
        http.Request.Host = new HostString("watch.example");
        http.Request.Headers["X-Emby-Token"] = "tok";
        var reachable = Source("https://cdn.example/movie.mkv");
        Filter(new PlaybackInfoResponse { MediaSources = [reachable] }, http: http);
        Assert.Equal("https://cdn.example/movie.mkv", reachable.Path);
        var dto = Source("http://zurg:9999/strm/X");
        Filter(new BaseItemDto { MediaSources = [dto] }, http: http);
        Assert.Equal("http://zurg:9999/strm/X", dto.Path);
    }

    [Fact]
    public async Task ReplacesPointerFileSizeWithTheStreamsRealSize()
    {
        // Jellyfin reports a .strm item's Size as the pointer file's own length
        // (38 bytes); Infuse refuses a source that small.
        var probed = new List<string>();
        var rd = Source("http://zurg:9999/strm/X");
        rd.Size = 38;
        rd.Container = "strm";
        var nzb = Source("http://nzbdav:8080" + ApiPath);
        nzb.Size = 1995565389;
        var external = Source("https://cdn.example/a.mkv");
        external.Size = 38;
        var http = new DefaultHttpContext();
        http.Request.Scheme = "http";
        http.Request.Host = new HostString("watch.example");
        http.Request.Headers["X-Emby-Token"] = "tok";
        await FilterAsync(new PlaybackInfoResponse { MediaSources = [rd, nzb, external] }, (url, _) =>
        {
            lock (probed) probed.Add(url);
            return Task.FromResult<NzbdavPlaybackRoutingFilter.StreamInfo?>(new(26119819645, "Flight.2160p.mkv"));
        }, http);
        Assert.Equal(["http://zurg:9999/strm/X"], probed); // looked up before Path is rewritten
        Assert.Equal(26119819645, rd.Size);
        Assert.Equal("mkv", rd.Container);
        Assert.StartsWith("http://watch.example/Videos/", rd.Path);
        Assert.Equal(1995565389, nzb.Size);
        Assert.Equal(38, external.Size);
    }

    [Fact]
    public async Task FixesSingleItemDtosButNotListsAndKeepsSizeWhenLookupFails()
    {
        var dto = Source("http://zurg:9999/strm/X");
        dto.Size = 38;
        await FilterAsync(new BaseItemDto { MediaSources = [dto] }, (_, _) =>
            Task.FromResult<NzbdavPlaybackRoutingFilter.StreamInfo?>(new(5000000000, null)));
        Assert.Equal(5000000000, dto.Size);
        Assert.Equal("http://zurg:9999/strm/X", dto.Path);
        Assert.Equal("mkv", dto.Container);

        var listed = Source("http://zurg:9999/strm/X");
        listed.Size = 38;
        await FilterAsync(
            new QueryResult<BaseItemDto>([new BaseItemDto { MediaSources = [listed] }]),
            (_, _) => throw new InvalidOperationException("lists must not be probed"));
        Assert.Equal(38, listed.Size);

        var failed = Source("http://zurg:9999/strm/X");
        failed.Size = 38;
        await FilterAsync(new PlaybackInfoResponse { MediaSources = [failed] }, (_, _) =>
            throw new HttpRequestException("down"));
        Assert.Equal(38, failed.Size);
        Assert.False(failed.SupportsDirectPlay);
    }

    [Fact]
    public void RegistersFilterWithMvc()
    {
        var services = new ServiceCollection();
        new NzbdavServiceRegistrator().RegisterServices(services, null!);
        using var provider = services.BuildServiceProvider();
        var filters = provider.GetRequiredService<IOptions<MvcOptions>>().Value.Filters;
        var factory = Assert.IsType<TypeFilterAttribute>(Assert.Single(filters));
        Assert.IsType<NzbdavPlaybackRoutingFilter>(factory.CreateInstance(provider));
    }

    private static MediaSourceInfo Source(string? path) => new()
    {
        Id = Id,
        Path = path,
        Protocol = MediaProtocol.Http,
        IsRemote = true,
        Container = "mkv",
        SupportsDirectPlay = true,
        SupportsDirectStream = true,
        SupportsTranscoding = true
    };

    private static void Filter(object response, string? baseUrl = "http://nzbdav:8080", HttpContext? http = null)
    {
        var action = new ActionContext(http ?? new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], new ObjectResult(response), new object());
        new NzbdavPlaybackRoutingFilter(() => baseUrl).OnResultExecuting(context);
    }

    private static async Task FilterAsync(
        object response,
        Func<string, CancellationToken, Task<NzbdavPlaybackRoutingFilter.StreamInfo?>> probe,
        HttpContext? http = null)
    {
        var action = new ActionContext(http ?? new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], new ObjectResult(response), new object());
        var ran = false;
        await new NzbdavPlaybackRoutingFilter(() => "http://nzbdav:8080", probe).OnResultExecutionAsync(context, () =>
        {
            ran = true;
            return Task.FromResult(new ResultExecutedContext(action, [], context.Result, new object()));
        });
        Assert.True(ran);
    }
}
