using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Nzbdav.Api;
using Jellyfin.Plugin.Nzbdav.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

/// <summary>
/// The client hides paging from its callers: the sync task still receives one assembled
/// manifest. These cover the walk itself, and the failure modes that only exist once a
/// manifest arrives in more than one piece.
/// </summary>
public sealed class NzbdavApiClientPagingTests
{
    [Fact]
    public async Task WalksEveryPageAndAssemblesThemIntoOneManifest()
    {
        var requested = new List<string?>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            requested.Add(CursorOf(request));
            return TestHttpMessageHandler.Json(CursorOf(request) switch
            {
                null => Page("v1", "Y3Vyc29yLTE=", (1, "/content/a.mkv")),
                "Y3Vyc29yLTE=" => Page("v1", "Y3Vyc29yLTI=", (2, "/content/b.mkv")),
                _ => Page("v1", null, (3, "/content/c.mkv"))
            });
        });

        var (manifest, _) = await CreateClient(handler).GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.ItemCount);
        Assert.Equal(3, manifest.Items.Length);
        Assert.Equal(
            new[] { "/content/a.mkv", "/content/b.mkv", "/content/c.mkv" },
            manifest.Items.Select(x => x.Path).ToArray());
        Assert.Equal(new string?[] { null, "Y3Vyc29yLTE=", "Y3Vyc29yLTI=" }, requested);
    }

    /// <summary>
    /// The opt-in is what stops the server paginating for clients that cannot follow
    /// cursors, so the client has to actually send it.
    /// </summary>
    [Fact]
    public async Task RequestsPagingExplicitly()
    {
        string? query = null;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            query = request.RequestUri!.Query;
            return TestHttpMessageHandler.Json(Page("v1", null, (1, "/content/a.mkv")));
        });

        await CreateClient(handler).GetManifestAsync(null, CancellationToken.None);

        Assert.Contains("paged=true", query);
    }

    /// <summary>
    /// A server that predates paging answers with the whole tree and no version or
    /// cursor. That must still work rather than being treated as a one-page walk of
    /// something larger.
    /// </summary>
    [Fact]
    public async Task HandlesAServerThatDoesNotPaginate()
    {
        var handler = new TestHttpMessageHandler((_, _) => TestHttpMessageHandler.Json(
            """
            {"itemCount":1,"items":[{"id":"00000000-0000-0000-0000-000000000001",
            "parentId":null,"name":"a.mkv","path":"/content/a.mkv","type":"nzb_file",
            "fileSize":1,"createdAt":"2026-04-06T12:00:00Z","hasProbeData":false}]}
            """));

        var (manifest, _) = await CreateClient(handler).GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.ItemCount);
    }

    /// <summary>
    /// Pages assembled from different versions of the tree are a torn read: the result
    /// may be missing items that were only ever in the half the client did not see.
    /// </summary>
    [Fact]
    public async Task RestartsTheWalkWhenTheTreeChangesMidway()
    {
        var attempts = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            var cursor = CursorOf(request);
            if (cursor is null) attempts++;

            // The first walk sees the version move between page one and page two.
            var version = attempts == 1 && cursor is not null ? "v2" : $"v{attempts}";
            return TestHttpMessageHandler.Json(cursor is null
                ? Page(version, "Y3Vyc29yLTE=", (1, "/content/a.mkv"))
                : Page(version, null, (2, "/content/b.mkv")));
        });

        var (manifest, _) = await CreateClient(handler).GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(2, attempts);
        Assert.Equal(2, manifest!.ItemCount);
    }

    [Fact]
    public async Task GivesUpWhenTheTreeNeverSettles()
    {
        var page = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            // A distinct version on every single response: no walk can ever agree.
            var version = $"v{page++}";
            return TestHttpMessageHandler.Json(CursorOf(request) is null
                ? Page(version, "Y3Vyc29yLTE=", (1, "/content/a.mkv"))
                : Page(version, null, (2, "/content/b.mkv")));
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).GetManifestAsync(null, CancellationToken.None));

        Assert.Contains("changed on the server", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A cursor that never advances would loop and grow the list forever.</summary>
    [Fact]
    public async Task RefusesACursorThatDoesNotAdvance()
    {
        var handler = new TestHttpMessageHandler((_, _) =>
            TestHttpMessageHandler.Json(Page("v1", "c3RhbGU=", (1, "/content/a.mkv"))));

        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).GetManifestAsync(null, CancellationToken.None));

        Assert.Contains("did not advance", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Each page is internally consistent here; only the assembled set reveals the
    /// collision. Validating per page would let this through.
    /// </summary>
    [Fact]
    public async Task RejectsDuplicatesThatOnlyCollideAcrossPages()
    {
        var handler = new TestHttpMessageHandler((request, _) => TestHttpMessageHandler.Json(
            CursorOf(request) is null
                ? Page("v1", "Y3Vyc29yLTE=", (1, "/content/a.mkv"))
                : Page("v1", null, (1, "/content/a.mkv"))));

        await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(handler).GetManifestAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task StillShortCircuitsOnNotModified()
    {
        var calls = 0;
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            calls++;
            Assert.Equal("\"tree-v1\"", request.Headers.IfNoneMatch.Single().Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified)
            {
                Headers = { ETag = new EntityTagHeaderValue("\"tree-v1\"") }
            };
        });

        var (manifest, etag) = await CreateClient(handler).GetManifestAsync("\"tree-v1\"", CancellationToken.None);

        Assert.Null(manifest);
        Assert.Equal("\"tree-v1\"", etag);
        Assert.Equal(1, calls);
    }

    /// <summary>
    /// The server refuses to revalidate a later page, so sending the header there would
    /// be pointless at best and a truncated walk at worst.
    /// </summary>
    [Fact]
    public async Task OnlyRevalidatesTheFirstPage()
    {
        var conditional = new List<bool>();
        var handler = new TestHttpMessageHandler((request, _) =>
        {
            conditional.Add(request.Headers.IfNoneMatch.Count > 0);
            return TestHttpMessageHandler.Json(CursorOf(request) is null
                ? Page("v1", "Y3Vyc29yLTE=", (1, "/content/a.mkv"))
                : Page("v1", null, (2, "/content/b.mkv")));
        });

        await CreateClient(handler).GetManifestAsync("\"tree-v0\"", CancellationToken.None);

        Assert.Equal(new[] { true, false }, conditional);
    }

    /// <summary>
    /// The cap bounds the assembled tree, not one page, so a walk that stays under it
    /// per response must still be rejected once the total passes the configured value.
    /// </summary>
    [Fact]
    public async Task EnforcesTheConfiguredItemCapAcrossTheWholeWalk()
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(
            () => CreateClient(ThreeSinglePageItems(), maxManifestItems: 2)
                .GetManifestAsync(null, CancellationToken.None));

        Assert.Contains("too many items", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A cap of zero would reject every manifest, so it is treated as unset rather than
    /// as an operator asking for a library of nothing.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task FallsBackToTheDefaultCapWhenTheConfiguredValueIsNotPositive(int configured)
    {
        var (manifest, _) = await CreateClient(ThreeSinglePageItems(), maxManifestItems: configured)
            .GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.ItemCount);
    }

    /// <summary>A raised cap admits a walk the default would also have admitted.</summary>
    [Fact]
    public async Task AcceptsAWalkThatFitsWithinTheConfiguredCap()
    {
        var (manifest, _) = await CreateClient(ThreeSinglePageItems(), maxManifestItems: 3)
            .GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.ItemCount);
    }

    /// <summary>
    /// An operator who never touches the setting keeps the cap the client shipped with.
    /// </summary>
    [Fact]
    public async Task LeavingTheCapUnconfiguredKeepsTheShippedDefault()
    {
        Assert.Equal(250000, new PluginConfiguration().MaxManifestItems);

        var (manifest, _) = await CreateClient(ThreeSinglePageItems())
            .GetManifestAsync(null, CancellationToken.None);

        Assert.NotNull(manifest);
        Assert.Equal(3, manifest!.ItemCount);
    }

    private static TestHttpMessageHandler ThreeSinglePageItems()
        => new((request, _) => TestHttpMessageHandler.Json(CursorOf(request) switch
        {
            null => Page("v1", "Y3Vyc29yLTE=", (1, "/content/a.mkv")),
            "Y3Vyc29yLTE=" => Page("v1", "Y3Vyc29yLTI=", (2, "/content/b.mkv")),
            _ => Page("v1", null, (3, "/content/c.mkv"))
        }));

    private static NzbdavApiClient CreateClient(HttpMessageHandler handler, int? maxManifestItems = null)
    {
        var config = new PluginConfiguration
        {
            NzbdavBaseUrl = "https://nzbdav.example",
            TimeoutSeconds = 5
        };
        if (maxManifestItems is not null) config.MaxManifestItems = maxManifestItems.Value;
        return new NzbdavApiClient(config, handler);
    }

    private static string? CursorOf(HttpRequestMessage request)
    {
        var query = request.RequestUri!.Query;
        var marker = query.IndexOf("after=", StringComparison.Ordinal);
        if (marker < 0) return null;

        var value = query[(marker + "after=".Length)..];
        var end = value.IndexOf('&');
        if (end >= 0) value = value[..end];
        return Uri.UnescapeDataString(value);
    }

    private static string Page(string version, string? nextCursor, params (int Id, string Path)[] items)
    {
        var serialized = items.Select(item =>
            $$"""
              {"id":"00000000-0000-0000-0000-{{item.Id:D12}}","parentId":null,
               "name":"{{item.Path[(item.Path.LastIndexOf('/') + 1)..]}}","path":"{{item.Path}}",
               "type":"nzb_file","fileSize":1,"createdAt":"2026-04-06T12:00:00Z","hasProbeData":false}
              """);

        var cursor = nextCursor is null ? string.Empty : $",\"nextCursor\":\"{nextCursor}\"";
        return $$"""
                 {"itemCount":{{items.Length}},"version":"{{version}}"{{cursor}},
                  "items":[{{string.Join(",", serialized)}}]}
                 """;
    }
}
