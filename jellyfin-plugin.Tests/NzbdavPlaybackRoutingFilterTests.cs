using Jellyfin.Plugin.Nzbdav;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
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

    private static void Filter(object response, string? baseUrl = "http://nzbdav:8080")
    {
        var action = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
        var context = new ResultExecutingContext(action, [], new ObjectResult(response), new object());
        new NzbdavPlaybackRoutingFilter(() => baseUrl).OnResultExecuting(context);
    }
}
