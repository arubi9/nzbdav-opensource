using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Nzbdav;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavMediaSourceProviderTests
{
    private static readonly string Token = "4102444800." + new string('A', 43);
    private static readonly MethodInfo CaptureIdentityMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("CaptureFileIdentity", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing identity capture.");

    [Fact]
    public async Task ProviderUsesCompletedMarkerGuidAndIgnoresJellyfinBaseItemId()
    {
        var root = CreateRoot();
        try
        {
            var streamId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var url = $"https://nzbdav.example/api/stream/{streamId:D}?token={Token}";
            File.WriteAllText(strm, url);
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            WriteCompletionMarker(root, strm, probe, streamId);

            var provider = Provider(root);
            var sources = await provider.GetMediaSources(
                new Video { Id = Guid.NewGuid(), Name = "Library identity differs", Path = strm }, CancellationToken.None);

            var source = Assert.Single(sources);
            Assert.Equal(url, source.Path);
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"format\":null}")]
    public async Task ProviderRejectsInvalidProbeShape(string probeJson)
    {
        var root = CreateRoot();
        try
        {
            var streamId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var url = $"https://nzbdav.example/api/stream/{streamId:D}?token={Token}";
            File.WriteAllText(strm, url);
            File.WriteAllText(probe, probeJson);
            WriteCompletionMarker(root, strm, probe, streamId);

            Assert.Empty(await Provider(root).GetMediaSources(new Video { Path = strm }, CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProviderRequiresStrictApiStreamGuidAndMarkerGuidEquality()
    {
        var root = CreateRoot();
        try
        {
            var urlId = Guid.NewGuid();
            var markerId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            File.WriteAllText(strm, $"https://nzbdav.example/api/stream/{urlId:D}/tail?token={Token}");
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            WriteCompletionMarker(root, strm, probe, markerId);

            var sources = await Provider(root).GetMediaSources(new Video { Id = Guid.NewGuid(), Path = strm }, CancellationToken.None);
            Assert.Empty(sources);

            File.WriteAllText(strm, $"https://nzbdav.example/api/stream/{urlId:D}?token={Token}");
            WriteCompletionMarker(root, strm, probe, markerId);
            sources = await Provider(root).GetMediaSources(new Video { Id = Guid.NewGuid(), Path = strm }, CancellationToken.None);
            Assert.Empty(sources);
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProviderRejectsForeignStreamWithStaleMarkerAndProbe()
    {
        var root = CreateRoot();
        try
        {
            var originalId = Guid.NewGuid();
            var foreignId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            File.WriteAllText(strm, $"https://nzbdav.example/api/stream/{originalId:D}?token={Token}");
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            WriteCompletionMarker(root, strm, probe, originalId);
            File.Delete(strm);
            File.WriteAllText(strm, $"https://nzbdav.example/api/stream/{foreignId:D}?token={Token}");

            var sources = await Provider(root).GetMediaSources(new Video { Id = originalId, Path = strm }, CancellationToken.None);
            Assert.Empty(sources);
            Assert.Equal($"https://nzbdav.example/api/stream/{foreignId:D}?token={Token}", File.ReadAllText(strm));
            Assert.Equal("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}", File.ReadAllText(probe));
        }
        finally { DeleteRoot(root); }
    }

    [Theory]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    [InlineData("duplicate")]
    [InlineData("malformed")]
    public async Task ProviderRejectsMalformedUnsafeOrDuplicateStreamQueries(string variation)
    {
        var root = CreateRoot();
        try
        {
            var id = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var url = variation switch
            {
                "userinfo" => $"https://user@nzbdav.example/api/stream/{id:D}?token={Token}",
                "fragment" => $"https://nzbdav.example/api/stream/{id:D}?token={Token}#fragment",
                "duplicate" => $"https://nzbdav.example/api/stream/{id:D}?token={Token}&token={Token}",
                _ => $"https://nzbdav.example/api/stream/{id:D}?token=not-a-token"
            };
            File.WriteAllText(strm, url);
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            WriteCompletionMarker(root, strm, probe, id);

            Assert.Empty(await Provider(root).GetMediaSources(new Video { Id = Guid.NewGuid(), Path = strm }, CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task ProviderUsesOneConfigurationSnapshotAcrossGateAwait()
    {
        var root = CreateRoot();
        var changedRoot = CreateRoot();
        try
        {
            var streamId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var url = $"https://nzbdav.example/api/stream/{streamId:D}?token={Token}";
            File.WriteAllText(strm, url);
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            WriteCompletionMarker(root, strm, probe, streamId);

            var calls = 0;
            var provider = new NzbdavMediaSourceProvider(
                NullLogger<NzbdavMediaSourceProvider>.Instance,
                () =>
                {
                    calls++;
                    return calls == 1
                        ? new NzbdavOperationConfiguration("https://nzbdav.example", root, string.Empty, 30)
                        : new NzbdavOperationConfiguration("https://other.example", changedRoot, string.Empty, 30);
                });

            using var gate = await NzbdavLibrarySyncTask.EnterMediaGateAsync(TestContext.Current.CancellationToken);
            var operation = provider.GetMediaSources(
                new Video { Id = Guid.NewGuid(), Path = strm }, TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(1, calls);

            gate.Dispose();
            var source = Assert.Single(await operation);
            Assert.Equal(url, source.Path);
            Assert.Equal(1, calls);
        }
        finally
        {
            DeleteRoot(root);
            DeleteRoot(changedRoot);
        }
    }

    [Fact]
    public async Task ProviderFailsClosedForNullOrInvalidConfigurationSnapshot()
    {
        var root = CreateRoot();
        try
        {
            var item = new Video { Path = Path.Combine(root, "movie.strm") };
            var nullProvider = new NzbdavMediaSourceProvider(
                NullLogger<NzbdavMediaSourceProvider>.Instance, () => null);
            Assert.Empty(await nullProvider.GetMediaSources(item, CancellationToken.None));

            var invalidProvider = new NzbdavMediaSourceProvider(
                NullLogger<NzbdavMediaSourceProvider>.Instance,
                () => new NzbdavOperationConfiguration("not-a-url", root, string.Empty, 30));
            Assert.Empty(await invalidProvider.GetMediaSources(item, CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    [Fact]
    public async Task V2MigrationInputStillRequiresUrlGuidAndIsNotProviderOwnership()
    {
        var root = CreateRoot();
        try
        {
            var urlId = Guid.NewGuid();
            var markerId = Guid.NewGuid();
            var strm = Path.Combine(root, "movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            File.WriteAllText(strm, $"https://nzbdav.example/api/stream/{urlId:D}?token={Token}");
            File.WriteAllText(probe, "{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
            var probeIdentity = CaptureIdentity(root, probe);
            var type = probeIdentity.GetType();
            var device = (ulong)type.GetProperty("Device")!.GetValue(probeIdentity)!;
            var inode = (ulong)type.GetProperty("Inode")!.GetValue(probeIdentity)!;
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probe)));
            File.WriteAllText(strm + ".nzbdav.managed", $"nzbdav.managed/2/{markerId:D}/{device}/{inode}/{hash}");

            Assert.Empty(await Provider(root).GetMediaSources(new Video { Id = markerId, Path = strm }, CancellationToken.None));
        }
        finally { DeleteRoot(root); }
    }

    private static NzbdavMediaSourceProvider Provider(string root)
        => new(NullLogger<NzbdavMediaSourceProvider>.Instance,
            () => new NzbdavOperationConfiguration("https://nzbdav.example", root, string.Empty, 30));

    private static void WriteCompletionMarker(string root, string strm, string probe, Guid markerId)
    {
        var streamIdentity = CaptureIdentity(root, strm);
        var streamType = streamIdentity.GetType();
        var streamDevice = (ulong)streamType.GetProperty("Device")!.GetValue(streamIdentity)!;
        var streamInode = (ulong)streamType.GetProperty("Inode")!.GetValue(streamIdentity)!;
        var streamHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(strm)));
        var probeIdentity = CaptureIdentity(root, probe);
        var probeType = probeIdentity.GetType();
        var probeDevice = (ulong)probeType.GetProperty("Device")!.GetValue(probeIdentity)!;
        var probeInode = (ulong)probeType.GetProperty("Inode")!.GetValue(probeIdentity)!;
        var probeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(probe)));
        var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(File.ReadAllText(strm)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(strm + ".nzbdav.managed",
            $"nzbdav.managed/4/{Guid.NewGuid():N}/{markerId:D}/{streamDevice}/{streamInode}/{streamHash}/{probeDevice}/{probeInode}/{probeHash}/{encodedUrl}");
    }

    private static object CaptureIdentity(string root, string path)
        => CaptureIdentityMethod.Invoke(null, [root, path])
            ?? throw new InvalidOperationException("Identity capture failed.");

    private static string CreateRoot()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "nzbdav-provider-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
