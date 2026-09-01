using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Jellyfin.Plugin.Nzbdav;
using Jellyfin.Plugin.Nzbdav.Api;
using Jellyfin.Plugin.Nzbdav.Configuration;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Sdk;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavLibrarySyncTaskTests
{
    private static readonly string CanonicalToken = new string('A', 43);
    private static string TokenFor(DateTimeOffset now) => $"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}";
    private static string CurrentToken => TokenFor(DateTimeOffset.UtcNow);

    private static readonly MethodInfo BuildRelativePathMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("BuildStrmRelativePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildStrmRelativePath.");
    private static readonly MethodInfo IsNzbdavManagedStrmContentMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("IsNzbdavManagedStrmContent", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find IsNzbdavManagedStrmContent.");
    private static readonly MethodInfo ShouldRefreshStreamUrlMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("ShouldRefreshStreamUrl", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find ShouldRefreshStreamUrl.");
    private static readonly MethodInfo GetQuarantineRelativePathMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("GetQuarantineRelativePath", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find GetQuarantineRelativePath.");
    private static readonly MethodInfo BuildExpectedStrmRelativePathsMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("BuildExpectedStrmRelativePaths", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find BuildExpectedStrmRelativePaths.");
    private static readonly MethodInfo ReconcileStaleFilesMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("ReconcileStaleFiles", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException("Could not find ReconcileStaleFiles.");
    private static readonly MethodInfo SyncDirectoryMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("SyncDirectory", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find SyncDirectory.");
    private static readonly MethodInfo WriteTextAtomicallyAsyncMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("WriteTextAtomicallyAsync", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find WriteTextAtomicallyAsync.");
    private static readonly MethodInfo CaptureFileIdentityMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("CaptureFileIdentity", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Could not find CaptureFileIdentity.");

    [Fact]
    public void LinuxOpenFlags_MatchSupportedArchitectures_AndFailClosedForUnknown()
    {
        var x64 = NzbdavLibrarySyncTask.LinuxOpenFlagsFor(Architecture.X64);
        Assert.Equal((0, 1, 2, 0x20000, 0x10000, 0x40, 0x80, 0x200, 0x80000, "x64"),
            (x64.ReadOnly, x64.WriteOnly, x64.ReadWrite, x64.NoFollow, x64.Directory, x64.Create, x64.Exclusive, x64.Truncate, x64.CloseOnExec, x64.Architecture));

        var arm64 = NzbdavLibrarySyncTask.LinuxOpenFlagsFor(Architecture.Arm64);
        Assert.Equal((0, 1, 2, 0x8000, 0x4000, 0x40, 0x80, 0x200, 0x80000, "arm64"),
            (arm64.ReadOnly, arm64.WriteOnly, arm64.ReadWrite, arm64.NoFollow, arm64.Directory, arm64.Create, arm64.Exclusive, arm64.Truncate, arm64.CloseOnExec, arm64.Architecture));

        Assert.Throws<NotSupportedException>(() => NzbdavLibrarySyncTask.LinuxOpenFlagsFor(Architecture.Wasm));
    }

    [Fact]
    public async Task SyncVideoFile_WritesPathBoundTokenUrl_AndNeverPersistsApiKey()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };
        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "durable-secret"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                Assert.Equal("/api/meta/" + video.Id, request.RequestUri?.AbsolutePath);
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{CurrentToken}\"}}");
            }));
            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var strmPath = Path.Combine(libraryPath, Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm"));
            var content = File.ReadAllText(strmPath);
            Assert.Contains("token=", content);
            Assert.DoesNotContain("apikey=", content, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_DoesNotRefreshForeignUrl()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };

        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        var originalUrl = $"https://other.example/api/stream/{id}?token={now.ToUnixTimeSeconds()}.old";
        WriteFile(libraryPath, expectedRelativePath, originalUrl);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(0, calls);
            var content = File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath));
            Assert.Equal(originalUrl, content);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_RefreshesSameOriginLegacyDownloadKey()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };

        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        WriteFile(libraryPath, expectedRelativePath, $"https://nzbdav.example/api/stream/{id}?apikey=legacy-secret");
        WriteManagedMarker(libraryPath, expectedRelativePath, id);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(now));
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(1, calls);
            var content = File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath));
            Assert.Equal($"https://nzbdav.example/api/stream/{id}?token={now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}", content);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_RefreshesMalformedCanonicalUrl()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };

        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        WriteFile(libraryPath, expectedRelativePath, $"https://nzbdav.example/api/stream/{id}?token=not-a-token");
        WriteManagedMarker(libraryPath, expectedRelativePath, id);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(0, calls);
            var content = File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath));
            Assert.Equal($"https://nzbdav.example/api/stream/{id}?token=not-a-token", content);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_RefreshesEmptyTokenCanonicalUrl()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };

        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        WriteFile(libraryPath, expectedRelativePath, $"https://nzbdav.example/api/stream/{id}?token=");
        WriteManagedMarker(libraryPath, expectedRelativePath, id);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(0, calls);
            var content = File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath));
            Assert.Equal($"https://nzbdav.example/api/stream/{id}?token=", content);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("0", "0", "none", true)]
    [InlineData("00", "0", "none", false)]
    [InlineData("0", "00", "none", false)]
    [InlineData("0", "0", "none\ntrailing", false)]
    public async Task V2MarkerParserRequiresCanonicalZeroIdentityAndExactSingleLine(
        string device, string inode, string probe, bool accepted)
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var relative = "movie.strm";
            var url = $"https://nzbdav.example/api/stream/{id}?token=1700518400.{CanonicalToken}";
            WriteFile(root, relative, url);
            var markerPath = Path.Combine(root, relative + ".nzbdav.managed");
            File.WriteAllText(markerPath, $"nzbdav.managed/2/{id:D}/{device}/{inode}/{probe}");
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler(
                (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)((_, _) =>
                    Task.FromException<HttpResponseMessage>(new XunitException("A fresh v2 marker must not fetch metadata.")))));

            await InvokeSyncVideoFile(task, config, client, new ManifestItem
            {
                Id = id, Name = "Movie.mkv", Path = "/content/movie.mkv", Type = "nzb_file"
            }, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var marker = File.ReadAllText(markerPath);
            Assert.Equal(accepted, marker.StartsWith("nzbdav.managed/4/", StringComparison.Ordinal));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_DoesNotWriteWhenMetaTokenMissing()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };
        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        var preservedUrl = $"https://nzbdav.example/api/stream/{id}?token=oldtoken";
        WriteFile(libraryPath, expectedRelativePath, preservedUrl);
        WriteManagedMarker(libraryPath, expectedRelativePath, id);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json("{\"streamToken\" : \"\"}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(preservedUrl, File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath)));
            Assert.True(File.Exists(Path.Combine(libraryPath, expectedRelativePath + ".nzbdav.managed")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("01700000000", false)]
    [InlineData("1700518400", true)]
    public void PluginTokenGrammarMatchesCanonicalExpiryRules(string expiry, bool expectedFresh)
    {
        var id = Guid.NewGuid();
        var url = $"https://nzbdav.example/api/stream/{id}?token={expiry}.{CanonicalToken}";
        var shouldRefresh = (bool)ShouldRefreshStreamUrlMethod.Invoke(null,
            [url, new Uri("https://nzbdav.example"), id,
                DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)])!;
        Assert.Equal(!expectedFresh, shouldRefresh);
    }

    [Fact]
    public async Task SyncVideoFile_DoesNotRewriteFreshCanonicalFileOrUpdateMtime()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };
        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        var originalUrl = $"https://nzbdav.example/api/stream/{id}?token={now.AddDays(6).ToUnixTimeSeconds()}.{CanonicalToken}";
        WriteFile(libraryPath, expectedRelativePath, originalUrl);
        var targetPath = Path.Combine(libraryPath, expectedRelativePath);
        var mtimeBefore = File.GetLastWriteTimeUtc(targetPath);

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(now));
            var calls = 0;
            var baseUri = new Uri(config.NzbdavBaseUrl);
            var tokenNeedsRefreshMethod = typeof(NzbdavLibrarySyncTask).GetMethod("TokenNeedsRefresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?? throw new InvalidOperationException("Could not find TokenNeedsRefresh.");
            var shouldRefreshMethod = ShouldRefreshStreamUrlMethod
                ?? throw new InvalidOperationException("Could not find ShouldRefreshStreamUrl.");
            var token = originalUrl.Split("?token=")[1];
            Assert.False((bool)tokenNeedsRefreshMethod.Invoke(null, [token, now])!);

            Assert.False((bool)shouldRefreshMethod.Invoke(null, [originalUrl, baseUri, id, now])!);
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> noMetadataHandler = (_, _) =>
            {
                calls++;
                throw new XunitException("Should not fetch metadata");
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler(noMetadataHandler));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var path = Path.Combine(libraryPath, expectedRelativePath);
            Assert.Equal(0, calls);
            Assert.Equal(originalUrl, File.ReadAllText(path));
            Assert.Equal(mtimeBefore, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task RefreshExistingTokens_RotatesExpiringUrlsOnManifest304_AndLeavesFreshUrlsAlone()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var expiringId = Guid.NewGuid();
        var freshId = Guid.NewGuid();
        Directory.CreateDirectory(libraryPath);
        WriteFile(libraryPath, "expiring.strm", $"https://nzbdav.example/api/stream/{expiringId}?token={now.AddDays(1).ToUnixTimeSeconds()}.{CanonicalToken}");
        WriteManagedMarker(libraryPath, "expiring.strm", expiringId);
        var freshUrl = $"https://nzbdav.example/api/stream/{freshId}?token={now.AddDays(5).ToUnixTimeSeconds()}.{CanonicalToken}";
        WriteFile(libraryPath, "fresh.strm", freshUrl);
        WriteManagedMarker(libraryPath, "fresh.strm", freshId);
        var calls = 0;

        try
        {
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header-secret"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                var id = Guid.Parse(request.RequestUri!.Segments[^1]);
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(now));

            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);

            Assert.Equal(1, calls);
            Assert.Contains("token=", File.ReadAllText(Path.Combine(libraryPath, "expiring.strm")));
            Assert.DoesNotContain("old", File.ReadAllText(Path.Combine(libraryPath, "expiring.strm")));
            Assert.Equal(freshUrl, File.ReadAllText(Path.Combine(libraryPath, "fresh.strm")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("marker")]
    public async Task RefreshExistingTokens_SameInodeMetadataRacePreservesForeignMember(string member)
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            const string relative = "movie.strm";
            var oldUrl = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, oldUrl);
            WriteManagedMarker(root, relative, id);
            var streamPath = Path.Combine(root, relative);
            var markerPath = streamPath + ".nzbdav.managed";
            var oldMarker = File.ReadAllBytes(markerPath);
            var target = member == "stream" ? streamPath : markerPath;
            var foreign = member == "stream" ? "foreign-same-inode-stream" : "foreign-same-inode-marker";
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                // File.WriteAllText truncates the existing file; it is the
                // same-inode edit that the old identity-only CAS destroyed.
                File.WriteAllText(target, foreign);
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{TokenFor(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))}\"}}");
            }));
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)));

            Assert.False(await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None));
            Assert.Equal(foreign, member == "stream" ? File.ReadAllText(streamPath) : File.ReadAllText(markerPath));
            if (member == "stream")
                Assert.Equal(oldMarker, File.ReadAllBytes(markerPath));
            else
                Assert.Equal(oldUrl, File.ReadAllText(streamPath));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task RefreshExistingTokens_PostCommitReplacementLeavesForeignFileUntouched()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            const string relative = "movie.strm";
            var oldUrl = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, oldUrl);
            WriteManagedMarker(root, relative, id);
            var target = Path.Combine(root, relative);
            var foreign = "foreign-after-commit";
            var swapped = false;
            Action<string> mutationHook = path =>
            {
                if (!swapped && string.Equals(path, target, StringComparison.Ordinal))
                {
                    swapped = true;
                    File.Delete(target);
                    File.WriteAllText(target, foreign);
                }
            };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json($"{{\"streamToken\":\"{TokenFor(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))}\"}}")));
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
                mutationHook);

            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);

            Assert.True(swapped);
            Assert.Equal(foreign, File.ReadAllText(target));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData("stream", false)]
    [InlineData("marker", true)]
    public async Task RefreshExistingTokens_PairedRotationCrashAtPublicationNeverLeavesMixedPair(
        string failurePath, bool completesNewPair)
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            const string relative = "movie.strm";
            var oldUrl = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            var newUrl = $"https://nzbdav.example/api/stream/{id}?token={TokenFor(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))}";
            WriteFile(root, relative, oldUrl);
            WriteManagedMarker(root, relative, id);
            var strmPath = Path.Combine(root, relative);
            var markerPath = strmPath + ".nzbdav.managed";
            var oldMarker = File.ReadAllBytes(markerPath);
            var injected = false;
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)),
                path =>
                {
                    if (!injected && Path.GetFileName(path).Equals(
                            failurePath == "stream" ? relative : relative + ".nzbdav.managed",
                            StringComparison.Ordinal))
                    {
                        injected = true;
                        throw new IOException("simulated crash");
                    }
                });
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json($"{{\"streamToken\":\"{TokenFor(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))}\"}}")));

            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);

            Assert.True(injected);
            if (completesNewPair)
            {
                Assert.Equal(newUrl, File.ReadAllText(strmPath));
                Assert.NotEqual(oldMarker, File.ReadAllBytes(markerPath));
            }
            else
            {
                Assert.Equal(oldUrl, File.ReadAllText(strmPath));
                Assert.Equal(oldMarker, File.ReadAllBytes(markerPath));
            }
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task RefreshExistingTokens_RotatesLegacyApiKeyUrlsOnManifest304()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var legacyId = Guid.NewGuid();
        Directory.CreateDirectory(libraryPath);
        WriteFile(libraryPath, "legacy.strm", $"https://nzbdav.example/api/stream/{legacyId}?apikey=legacy");
        WriteManagedMarker(libraryPath, "legacy.strm", legacyId);

        var calls = 0;
        try
        {
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header-secret"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}");
            }));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(now));

            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);

            Assert.Equal(1, calls);
            var legacyContent = File.ReadAllText(Path.Combine(libraryPath, "legacy.strm"));
            Assert.Contains("token=", legacyContent);
            Assert.DoesNotContain("apikey=", legacyContent);
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public void ValidateLinuxFstatLayout_IsKnownForCurrentLinuxArch()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var abi = NzbdavLibrarySyncTask.LinuxAbi;
        if (RuntimeInformation.OSArchitecture == Architecture.X64)
        {
            Assert.Equal(144, abi.FstatBufferSize);
            Assert.Equal(24, abi.FstatModeOffset);
            Assert.Equal("x64", abi.Architecture);
        }
        else if (RuntimeInformation.OSArchitecture == Architecture.Arm64)
        {
            Assert.Equal(128, abi.FstatBufferSize);
            Assert.Equal(16, abi.FstatModeOffset);
            Assert.Equal("arm64", abi.Architecture);
        }
        else
        {
            Assert.Fail($"Unexpected Linux architecture: {RuntimeInformation.OSArchitecture}");
        }

        Assert.Equal(0, abi.FstatDeviceOffset);
    }

    [Fact]
    public async Task RefreshExistingTokens_LeavesNonCanonicalForeignUrlAlone()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var oldUrl = $"https://other.example/api/stream/{id}?apikey=durable-secret";
        Directory.CreateDirectory(libraryPath);
        WriteFile(libraryPath, "movie.strm", oldUrl);

        try
        {            var calls = 0;
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header-secret"
            };
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler = (_, _) =>
            {
                calls++;
                return Task.FromResult(TestHttpMessageHandler.Json("{\"streamToken\":\"ignored\"}"));
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler(handler));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(DateTimeOffset.UtcNow));

            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.Equal(oldUrl, File.ReadAllText(Path.Combine(libraryPath, "movie.strm")));
            Assert.DoesNotContain("token=", File.ReadAllText(Path.Combine(libraryPath, "movie.strm")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public void BuildStrmRelativePath_UsesParentDirectoryName_ForObfuscatedVideoFile()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Family.Guy.S24E07.1080p.WEB.h264-EDITH",
            Path = "/content/uncategorized/Family.Guy.S24E07.1080p.WEB.h264-EDITH",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "W6Ss3ROn1dPrVxlU916rJLYwTk6QbtDe.mkv",
            Path = "/content/uncategorized/Family.Guy.S24E07.1080p.WEB.h264-EDITH/W6Ss3ROn1dPrVxlU916rJLYwTk6QbtDe.mkv",
            Type = "nzb_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(
            Normalize(Path.Combine("uncategorized", parent.Name, parent.Name + ".mkv")),
            Normalize(result));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_KeepsObfuscatedNameWhenCanonicalSiblingExists()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Movie.2026.1080p-GROUP",
            Path = "/content/movies/Movie.2026.1080p-GROUP",
            Type = "directory"
        };
        var canonical = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = parent.Name + ".mkv",
            Path = parent.Path + "/" + parent.Name + ".mkv",
            Type = "nzb_file"
        };
        var obfuscated = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "ahm0ohchahcus8euDieh4Dah4fah4cah.mkv",
            Path = parent.Path + "/ahm0ohchahcus8euDieh4Dah4fah4cah.mkv",
            Type = "rar_file"
        };
        var items = new[] { parent, canonical, obfuscated };

        var result = InvokeBuildExpectedStrmRelativePaths(items, items.ToDictionary(i => i.Id));

        Assert.Equal(
            [
                Normalize(Path.Combine("movies", parent.Name, parent.Name + ".strm")),
                Normalize(Path.Combine("movies", parent.Name, "ahm0ohchahcus8euDieh4Dah4fah4cah.strm"))
            ],
            result.Select(Normalize).ToArray());
    }

    [Fact]
    public void BuildStrmRelativePath_KeepsReadableReleaseName()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR",
            Path = "/content/uncategorized/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR.mkv",
            Path = "/content/uncategorized/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR/The.Change-Up.2011.Unrated.BluRay.1080p.DTS-HD.MA.5.1.VC-1.REMUX-FraMeSToR.mkv",
            Type = "multipart_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(
            Normalize(Path.Combine("uncategorized", parent.Name, video.Name)),
            Normalize(result));
    }

    [Fact]
    public void BuildStrmRelativePath_DoesNotTreatPlainSingleWordTitleAsObfuscated()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Interstellar",
            Path = "/content/movies/Interstellar",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "Interstellar.mkv",
            Path = "/content/movies/Interstellar/Interstellar.mkv",
            Type = "nzb_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem> { [parentId] = parent });

        Assert.Equal(Normalize(Path.Combine("movies", parent.Name, video.Name)), Normalize(result));
    }

    [Fact]
    public void BuildStrmRelativePath_RejectsTraversalSegments()
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/content/movies/../etc/passwd.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildStrmRelativePath_RejectsAbsoluteForeignRootPath()
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/etc/passwd/movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildStrmRelativePath_RejectsAltPathSeparator()
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/content/movies\\Movie\\Movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public async Task SyncVideoFile_SkipsSymlinkDirectoryTraversal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        Directory.CreateDirectory(libraryPath);

        var symlinkDir = Path.Combine(libraryPath, "safe-link");
        Directory.CreateSymbolicLink(symlinkDir, outsideRoot);

        try
        {
            var video = new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "Movie.mkv",
                Path = "/content/safe-link/Movie/Movie.mkv",
                Type = "nzb_file"
            };
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var calls = 0;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json("{\"streamToken\":\"123.dead\"}");
            }));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal(0, calls);
            Assert.False(File.Exists(Path.Combine(outsideRoot, "Movie", "Movie.strm")));
            Assert.False(Directory.Exists(Path.Combine(libraryPath, "safe-link", "Movie")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_SkipsFinalSymlinkBeforeNetwork()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        Directory.CreateDirectory(outsideRoot);
        var outsideStrm = Path.Combine(outsideRoot, "Movie.strm");
        File.WriteAllText(outsideStrm, "foreign");
        File.CreateSymbolicLink(Path.Combine(libraryPath, "Movie.strm"), outsideStrm);

        try
        {
            var video = new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file"
            };
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json("{\"streamToken\":\"123.dead\"}");
            }));

            var succeeded = await InvokeSyncVideoFile(
                new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance),
                config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.False(succeeded);
            Assert.Equal(0, calls);
            Assert.Equal("foreign", File.ReadAllText(outsideStrm));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_SymlinkSwapAfterPreflightDoesNotWriteOutsideLibrary()
    {
        if (!OperatingSystem.IsLinux()
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var libraryPath = CreateTestLibrary();
        var outsideRoot = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests-outside", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(outsideRoot);
        try
        {
            var video = new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "Movie.mkv",
                Path = "/content/safe/Movie.mkv",
                Type = "nzb_file"
            };
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var safeDirectory = Path.Combine(libraryPath, "safe");
            var swapped = false;
            var calls = 0;
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!swapped && string.Equals(path, Path.Combine(safeDirectory, "Movie.strm"), StringComparison.Ordinal))
                    {
                        swapped = true;
                        Directory.Delete(safeDirectory, recursive: true);
                        Directory.CreateSymbolicLink(safeDirectory, outsideRoot);
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}");
            }));

            var succeeded = await InvokeSyncVideoFile(
                task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.True(swapped);
            Assert.Equal(1, calls);
            Assert.False(succeeded);
            Assert.False(File.Exists(Path.Combine(outsideRoot, "Movie.strm")));
            Assert.False(File.Exists(Path.Combine(outsideRoot, "Movie.strm.nzbdav.managed")));
        }
        finally
        {
            DeleteTestLibrary(libraryPath);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void SyncDirectory_DoesNotThrow_ForManagedDirectoryAcrossPlatforms()
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);

        try
        {
            var exception = Record.Exception(() => InvokeSyncDirectory(path));
            Assert.Null(exception);
        }
        finally
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void ValidateOpenAtPathComponent_RejectsTraversalAndMultiComponent()
    {
        Assert.Throws<InvalidOperationException>(() => NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("."));
        Assert.Throws<InvalidOperationException>(() => NzbdavLibrarySyncTask.ValidateOpenAtPathComponent(".."));
        Assert.Throws<InvalidOperationException>(() => NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("a/b"));
        Assert.Throws<InvalidOperationException>(() => NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("a\\b"));
        Assert.Throws<InvalidOperationException>(() => NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("a/b/c"));

        NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("movie");
        NzbdavLibrarySyncTask.ValidateOpenAtPathComponent("series.mp4");
    }

    [Fact]
    public async Task WriteTextAtomicallyAsync_RetainsSourceOnReplaceFailure()
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"), "movie.strm");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "original");

        try
        {
            Exception? exception;
            using (var lockedDestination = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                exception = await Record.ExceptionAsync(() =>
                    InvokeWriteTextAtomicallyAsync(
                        Path.GetDirectoryName(path)!,
                        path,
                        "updated",
                        CancellationToken.None));
            }
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                Assert.IsType<IOException>(exception);
                Assert.Equal("original", File.ReadAllText(path));
            }
            else
            {
                Assert.Null(exception);
            }

            var directory = Path.GetDirectoryName(path)!;
            Assert.DoesNotContain(Directory.GetFiles(directory), file =>
                Path.GetFileName(file).StartsWith(".nzbdav.tmp-", StringComparison.Ordinal));
            Assert.False(File.Exists(path + ".backup"));
        }
        finally
        {
            if (Directory.Exists(Path.GetDirectoryName(path)))
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_WindowsPreparedReplacementRetainsForeignSlot()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file"
            };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var replaced = false;
            string? slot = null;
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!replaced && Path.GetFileName(path).StartsWith(".nzbdav.prepare-", StringComparison.Ordinal))
                    {
                        replaced = true;
                        slot = path;
                        File.Delete(path);
                        File.WriteAllText(path, "foreign-prepared-slot");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.True(replaced);
            Assert.NotNull(slot);
            Assert.Equal("foreign-prepared-slot", File.ReadAllText(slot!));
            Assert.False(File.Exists(Path.Combine(root, "Movie.strm")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task WriteTextAtomicallyAsync_ReplacesSourcePathAndPublishesHeldInodeUnderSourceReplacement()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var destination = Path.Combine(root, "atomic.txt");
            var expected = "atomic-payload";
            var foreign = "foreign-source-content";
            string? sourcePath = null;
            bool replaced = false;

            Action<string> mutationHook = path =>
            {
                if (replaced)
                    return;

                var fileName = Path.GetFileName(path);
                if (!fileName.StartsWith(".nzbdav.prepare-", StringComparison.Ordinal)
                    && !fileName.StartsWith(".nzbdav.tmp-", StringComparison.Ordinal))
                    return;

                replaced = true;
                sourcePath = path;
                File.Move(sourcePath, sourcePath + ".held-away");
                File.WriteAllText(sourcePath, foreign);
            };

            bool succeeded = false;
            try
            {
                await InvokeWriteTextAtomicallyAsync(root, destination, expected, CancellationToken.None, mutationHook);
                succeeded = true;
            }
            catch (IOException)
            {
            }

            Assert.True(replaced);
            Assert.NotNull(sourcePath);
            Assert.Equal(foreign, File.ReadAllText(sourcePath!));

            if (succeeded)
            {
                Assert.Equal(expected, File.ReadAllText(destination));
            }
            else
            {
                Assert.False(File.Exists(destination), "If source-path replacement fails closed, destination must remain absent.");
            }

            var heldAway = sourcePath + ".held-away";
            if (File.Exists(heldAway))
            {
                Assert.Equal(expected, File.ReadAllText(heldAway));
            }
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task WriteTextAtomicallyAsync_ReplacesDestinationFileAfterExpectedHandleValidation()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var destination = Path.Combine(root, "atomic.txt");
            File.WriteAllText(destination, "original-payload");
            var expected = "atomic-payload";
            var foreign = "foreign-replaced-content";
            bool callback = false;

            var ex = await Assert.ThrowsAsync<IOException>(() =>
                InvokeWriteTextAtomicallyAsync(root, destination, expected, CancellationToken.None, path =>
                {
                    if (callback || Path.GetFileName(path) != "atomic.txt")
                        return;

                    callback = true;
                    File.WriteAllText(path, foreign);
                }));
            Assert.True(callback, "Expected destination-path mutation hook to run.");
            // The existing destination is held without delete sharing. A
            // rename-away/foreign install must be blocked rather than replacing
            // the object whose handle was validated.
            Assert.Equal("original-payload", File.ReadAllText(destination));
        }
        finally
        {
            DeleteTestLibrary(root);
        }
    }

    [Fact]
    public async Task WriteTextAtomicallyAsync_FailsClosedWhenForeignDestinationInsertedBeforeNoReplaceRename()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var destination = Path.Combine(root, "atomic.txt");
            var expected = "atomic-payload";
            var foreign = "foreign-inserted-content";
            bool callback = false;

            var noReplaceEx = await Assert.ThrowsAsync<IOException>(() =>
                InvokeWriteTextAtomicallyAsync(root, destination, expected, CancellationToken.None, path =>
                {
                    if (callback)
                        return;

                    if (Path.GetFileName(path) != "atomic.txt")
                        return;

                    callback = true;
                    File.WriteAllText(path, foreign);
                }));

            Assert.True(callback, "Expected destination-path mutation hook to run.");
            Assert.Equal(foreign, File.ReadAllText(destination));
        }
        finally
        {
            DeleteTestLibrary(root);
        }
    }

    [Fact]
    public async Task WindowsHandleRenamePublishesHeldPreparedInodeAfterFinalSourceReplacement()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file" };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var expectedUrl = $"https://nzbdav.example/api/stream/{id}?token={CurrentToken}";
            var prepareCallbacks = 0;
            string? foreignSlot = null;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!Path.GetFileName(path).StartsWith(".nzbdav.prepare-", StringComparison.Ordinal))
                        return;

                    prepareCallbacks++;
                    if (prepareCallbacks == 2)
                    {
                        // Callback one is preparation proof. Callback two is
                        // inside the final handle-based rename, after its last
                        // source-path check and before SetFileInformationByHandle.
                        foreignSlot = path;
                        File.Move(path, path + ".held-away");
                        File.WriteAllText(path, "foreign-final-prepared");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var moviePath = Path.Combine(root, "Movie.strm");

            Assert.NotNull(foreignSlot);
            Assert.Equal("foreign-final-prepared", File.ReadAllText(foreignSlot!));
            Assert.Equal(expectedUrl, File.ReadAllText(moviePath));
            Assert.True(File.Exists(moviePath + ".nzbdav.managed"));
            File.Delete(foreignSlot!); // proves the final rename closed its handle
            File.Delete(foreignSlot!); // proves the final rename closed its handle
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void WindowsHandleRenamePublishesHeldQuarantineInodeAfterFinalSourceReplacement()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            const string content = "https://nzbdav.example/api/stream/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
            var source = Path.Combine(root, relative);
            WriteFile(root, relative, content);
            WriteManagedMarker(root, relative, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
            var replaced = false;
            string? foreignTemp = null;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!replaced && path.EndsWith(".quarantined.tmp", StringComparison.Ordinal))
                    {
                        replaced = true;
                        foreignTemp = path;
                        File.Move(path, path + ".held-away");
                        File.WriteAllText(path, "foreign-final-quarantine-temp");
                    }
                });
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };

            Assert.False(InvokeReconcileStaleFiles(task, config, [], "final-rename", CancellationToken.None));

            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            Assert.True(replaced);
            Assert.NotNull(foreignTemp);
            Assert.Equal("foreign-final-quarantine-temp", File.ReadAllText(foreignTemp!));
            Assert.Equal(content, File.ReadAllText(destination));
            Assert.Equal(content, File.ReadAllText(source));
            File.Delete(foreignTemp!); // proves the staging handle was closed
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_CancellationAfterPartialCommit_CleansOnlyOwnedOutput()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file"
            };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            using var cts = new CancellationTokenSource();
            Action<string> mutationHook = path =>
            {
                if (path.EndsWith(".strm", StringComparison.Ordinal))
                    cts.Cancel();
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, pathMutationHook: mutationHook);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), cts.Token));

            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                Path.GetFileName(path).StartsWith(".nzbdav.tmp-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith(".nzbdav.quarantine-tmp-", StringComparison.Ordinal));
            // A cancellation after the stream link is a durable fresh intent,
            // not permission to unlink an inode that may have been replaced.
            Assert.Contains(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                path.EndsWith(".strm", StringComparison.Ordinal));
            Assert.Contains(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                path.EndsWith(".nzbdav.managed.intent", StringComparison.Ordinal));
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                string.Equals(Path.GetFileName(path), ".nzbdav.managed", StringComparison.Ordinal));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void IsNzbdavManagedStrmContent_ReturnsTrue_ForMatchingBaseUrlAndTokenStream()
    {
        var result = InvokeIsNzbdavManagedStrmContent(
            "https://nzbdav.example/api/stream/12345678-1234-1234-1234-123456789abc?token=4102444800.signature",
            "https://nzbdav.example");

        Assert.True(result);
    }

    [Theory]
    [InlineData("https://nzbdav.example.evil", "https://nzbdav.example/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x")]
    [InlineData("https://user@nzbdav.example", "https://nzbdav.example/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x")]
    [InlineData("https://nzbdav.example#fragment", "https://nzbdav.example/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x")]
    [InlineData("https://nzbdav.example:8443", "https://nzbdav.example/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x")]
    [InlineData("https://nzbdav.example/media", "https://nzbdav.example/mediaplus/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x")]
    public void IsNzbdavManagedStrmContent_RejectsAuthorityAndPathPrefixConfusion(string baseUrl, string content)
    {
        Assert.False(InvokeIsNzbdavManagedStrmContent(content, baseUrl));
    }

    [Fact]
    public void IsNzbdavManagedStrmContent_AcceptsExactPathBoundaryAndEffectivePort()
    {
        Assert.True(InvokeIsNzbdavManagedStrmContent(
            "https://nzbdav.example:443/media/api/stream/12345678-1234-1234-1234-123456789abc?apikey=x",
            "https://nzbdav.example/media"));
    }

    [Fact]
    public void IsNzbdavManagedStrmContent_ReturnsFalse_ForForeignUrl()
    {
        var result = InvokeIsNzbdavManagedStrmContent(
            "https://other.example/video.strm",
            "https://nzbdav.example");

        Assert.False(result);
    }

    [Fact]
    public void IsNzbdavManagedStrmContent_ReturnsTrue_ForLegacyApiKeyUrl()
    {
        var result = InvokeIsNzbdavManagedStrmContent(
            "https://nzbdav.example/api/stream/abc?apikey=123",
            "https://nzbdav.example");

        Assert.True(result);
    }

    [Fact]
    public void IsNzbdavManagedStrmContent_ReturnsFalse_ForCanonicalQueryWithExtraParams()
    {
        var result = InvokeIsNzbdavManagedStrmContent(
            "https://nzbdav.example/api/stream/abc?token=4102444800.signature&downloadKey=other",
            "https://nzbdav.example");

        Assert.False(result);
    }

    [Fact]
    public void GetQuarantineRelativePath_PreservesRelativeStructureUnderQuarantineRoot()
    {
        var result = InvokeGetQuarantineRelativePath(
            Path.Combine("shows", "Series", "Episode.strm"),
            "20260416-120000");

        Assert.Equal(
            Normalize(Path.Combine(".quarantine", "shows", "Series", "Episode.strm.quarantined")),
            Normalize(result));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_ReturnsOnlyVideoItems()
    {
        var parentId = Guid.NewGuid();
        var parent = new ManifestItem
        {
            Id = parentId,
            Name = "Movie",
            Path = "/content/movies/Movie",
            Type = "directory"
        };
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };
        var directory = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = parentId,
            Name = "extras",
            Path = "/content/movies/Movie/extras",
            Type = "directory"
        };

        var result = InvokeBuildExpectedStrmRelativePaths(
            [parent, video, directory],
            new Dictionary<Guid, ManifestItem>
            {
                [parent.Id] = parent,
                [video.Id] = video,
                [directory.Id] = directory
            });

        Assert.Equal(
            [Normalize(Path.Combine("movies", "Movie", "Movie.strm"))],
            result.Select(Normalize).ToArray());
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_RejectsCaseDifferencesInDirectorySegments()
    {
        var items = new[]
        {
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "a.mkv",
                Path = "/content/Foo/a.mkv",
                Type = "nzb_file"
            },
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "b.mkv",
                Path = "/content/foo/b.mkv",
                Type = "nzb_file"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_AllowsSameSpellingsUnderDifferentParents()
    {
        var items = new[]
        {
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "a.mkv",
                Path = "/content/A/Foo/a.mkv",
                Type = "nzb_file"
            },
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "b.mkv",
                Path = "/content/B/foo/b.mkv",
                Type = "nzb_file"
            }
        };

        var result = InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>());

        Assert.Equal(
            ["A/Foo/a.strm", "B/foo/b.strm"],
            result.Select(Normalize).ToArray());
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_NormalizesUnicodeBeforeSiblingCollisionCheck()
    {
        var items = new[]
        {
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "a.mkv",
                Path = "/content/Shows/Caf\u00e9/a.mkv",
                Type = "nzb_file"
            },
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "b.mkv",
                Path = "/content/Shows/CAF\u00c9/b.mkv",
                Type = "nzb_file"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_RejectsUnicodeFileDirectoryPrefixCollisions()
    {
        var fileId = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        var items = new[]
        {
            new ManifestItem
            {
                Id = fileId,
                Name = "Cafe",
                Path = "/content/Shows/Cafe\u0301",
                Type = "nzb_file"
            },
            new ManifestItem
            {
                Id = dirId,
                Name = "alias-dir",
                Path = "/content/Shows/Caf\u00e9/child",
                Type = "directory"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_RejectsUnicodeDirectoryFilePrefixCollisions()
    {
        var fileId = Guid.NewGuid();
        var dirId = Guid.NewGuid();
        var items = new[]
        {
            new ManifestItem
            {
                Id = dirId,
                Name = "alias-dir",
                Path = "/content/Shows/Cafe\u0301/child",
                Type = "directory"
            },
            new ManifestItem
            {
                Id = fileId,
                Name = "a.mkv",
                Path = "/content/Shows/Caf\u00e9/a.mkv",
                Type = "nzb_file"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void ValidateManifestNamespace_RejectsUnicodeDirectoryAliasWithoutVideoOutput()
    {
        var items = new[]
        {
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "ignored",
                Path = "/content/Shows/Directory/Cafe\u0301",
                Type = "directory"
            },
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "ignored",
                Path = "/content/Shows/Directory/Caf\u00e9",
                Type = "directory"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>()));
    }


    [Fact]
    public void BuildExpectedStrmRelativePaths_RejectsControlCharactersBeforeCollisionCheck()
    {
        var item = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "a.mkv",
            Path = "/content/Shows/Bad\u0001/a.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildExpectedStrmRelativePaths([item], new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void ManagedOwnership_RejectsValidFirstLineWithOversizedTrailingBytes()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/oversized.strm";
            var id = Guid.NewGuid();
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{id}?token={CurrentToken}");
            WriteFile(root, relative + ".nzbdav.managed", $"nzbdav.managed/1/{id:D}" + new string('x', 1024 * 1024));

            var result = InvokeTryReadBoundedFirstLine(root, Path.Combine(root, relative + ".nzbdav.managed"), 256);

            Assert.False(result);
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void BuildExpectedStrmRelativePaths_DetectsDuplicateDestinationPaths()
    {
        var items = new[]
        {
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "Movie.mkv",
                Path = "/content/movies/Movie/Movie.mkv",
                Type = "nzb_file"
            },
            new ManifestItem
            {
                Id = Guid.NewGuid(),
                Name = "Movie.mkv",
                Path = "/content/movies/Movie/Movie.mkv",
                Type = "nzb_file"
            }
        };

        Assert.Throws<InvalidOperationException>(() => InvokeBuildExpectedStrmRelativePaths(items, new Dictionary<Guid, ManifestItem>{}));
    }

    [Fact]
    public void BuildStrmRelativePath_RejectsReservedPathSegment()
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/content/.quarantine/movies/Movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildStrmRelativePath_RejectsWhitespaceOnlySegment()
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = "/content/movies/   /Movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("CON.txt")]
    [InlineData("LPT9.archive")]
    [InlineData("Movie.")]
    [InlineData("Movie ")]
    public void BuildStrmRelativePath_RejectsWindowsAliasesAndTrailingNameAliases(string segment)
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = $"/content/{segment}/Movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Theory]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData(":")]
    [InlineData("\"")]
    [InlineData("|")]
    [InlineData("?")]
    [InlineData("*")]
    [InlineData("bad\u0001name")]
    [InlineData("bad\u007fname")]
    public void BuildStrmRelativePath_RejectsWindowsInvalidCharactersOnEveryPlatform(string segment)
    {
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = $"/content/{segment}/Movie.mkv",
            Type = "nzb_file"
        };

        Assert.Throws<ArgumentException>(() => InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()));
    }

    [Fact]
    public void BuildStrmRelativePath_PreservesAcceptedSegmentSpelling()
    {
        var segment = "Caf\u0065\u0301";
        var video = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Movie.mkv",
            Path = $"/content/{segment}/Movie.mkv",
            Type = "nzb_file"
        };

        var result = InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>());

        Assert.Equal($"{segment}/Movie.mkv", result.Replace('\\', '/'));
    }

    [Fact]
    public void Reconcile_ForgedValidMarkerAndForeignUrl_LeavesStrmAndProbeUntouched()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "foreign/forged.strm";
            var id = Guid.NewGuid();
            WriteFile(root, relative, $"https://foreign.example/api/stream/{id}?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(root, relative, id);
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "foreign-probe");

            Assert.True(RunReconcile(root));
            Assert.True(File.Exists(Path.Combine(root, relative)));
            Assert.True(File.Exists(Path.Combine(root, relative + ".nzbdav.managed")));
            Assert.True(File.Exists(Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"))));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void Reconcile_MarkerIdDiffersFromSameOriginUrlId_LeavesSidecarsUntouched()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "foreign/mismatch.strm";
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{Guid.NewGuid()}?apikey={new string('a', 64)}");
            WriteManagedMarker(root, relative, Guid.NewGuid());
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "probe");

            Assert.True(RunReconcile(root));
            Assert.True(File.Exists(Path.Combine(root, relative)));
            Assert.True(File.Exists(Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"))));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData("userinfo")]
    [InlineData("fragment")]
    public void Reconcile_SameOriginUrlWithUserInfoOrFragment_IsRejected(string variation)
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "foreign/unsafe.strm";
            var id = Guid.NewGuid();
            var url = variation == "userinfo"
                ? $"https://attacker@nzbdav.example/api/stream/{id}?apikey={new string('a', 64)}"
                : $"https://nzbdav.example/api/stream/{id}?apikey={new string('a', 64)}#foreign";
            WriteFile(root, relative, url);
            WriteManagedMarker(root, relative, id);
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "probe");

            Assert.True(RunReconcile(root));
            Assert.True(File.Exists(Path.Combine(root, relative)));
            Assert.True(File.Exists(Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"))));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void Reconcile_MalformedTokenMarkerSidecarIsNotOwned_AndProbeIsNotMoved()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "foreign/malformed.strm";
            var id = Guid.NewGuid();
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{id}?token=malformed");
            WriteManagedMarker(root, relative, id);
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "probe");

            Assert.True(RunReconcile(root));
            Assert.True(File.Exists(Path.Combine(root, relative)));
            Assert.True(File.Exists(Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"))));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void Reconcile_ValidCurrentAnd64HexLegacyUrls_AreOwnedAndStaleFilesQuarantined()
    {
        var root = CreateTestLibrary();
        try
        {
            var current = "current/current.strm";
            var legacy = "legacy/legacy.strm";
            var currentId = Guid.NewGuid();
            var legacyId = Guid.NewGuid();
            WriteFile(root, current, $"https://nzbdav.example/api/stream/{currentId}?token=4102444800.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(root, current, currentId);
            WriteFile(root, legacy, $"https://nzbdav.example/api/stream/{legacyId}?apikey={new string('a', 64)}");
            WriteManagedMarker(root, legacy, legacyId);
            WriteFile(root, Path.ChangeExtension(legacy, ".mediainfo.json"), "legacy-probe");

            Assert.True(RunReconcile(root));
            // Linux cannot safely unlink a source pathname after an adversarial
            // replacement. Quarantine is a durable copy plus a logical
            // tombstone; ordinary stale sources remain physically present.
            Assert.True(File.Exists(Path.Combine(root, current)));
            Assert.True(File.Exists(Path.Combine(root, legacy)));
            Assert.True(File.Exists(Path.Combine(root, current + ".nzbdav.tombstone")));
            Assert.True(File.Exists(Path.Combine(root, legacy + ".nzbdav.tombstone")));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(root, ".quarantine"), "*.quarantined", SearchOption.AllDirectories), p => p.EndsWith("current.strm.quarantined", StringComparison.Ordinal));
            Assert.Contains(Directory.EnumerateFiles(Path.Combine(root, ".quarantine"), "*.quarantined", SearchOption.AllDirectories), p => p.EndsWith("legacy.mediainfo.json.quarantined", StringComparison.Ordinal));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void Reconcile_ForeignStrmWithProbe_NeverMovesProbeWithoutStrmOwnership()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "foreign/probe-only.strm";
            WriteFile(root, relative, "not-a-stream-url");
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "must-stay");
            Assert.True(RunReconcile(root));
            Assert.True(File.Exists(Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"))));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_QuarantinesOnlyStaleManagedFiles_AndDropsNomedia()
    {
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);

        try
        {
            var activeRelative = Path.Combine("movies", "Movie", "Movie.strm");
            var staleRelative = Path.Combine("movies", "OldMovie", "OldMovie.strm");
            var foreignRelative = Path.Combine("foreign", "Foreign.strm");
            var quarantinedRelative = Path.Combine(".quarantine", "movies", "Ghost.strm.quarantined");
            var staleId = Guid.NewGuid();
            var activeId = Guid.NewGuid();

            WriteFile(libraryPath, activeRelative, $"https://nzbdav.example/api/stream/{activeId}?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(libraryPath, activeRelative, activeId);
            WriteFile(libraryPath, staleRelative, $"https://nzbdav.example/api/stream/{staleId}?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(libraryPath, staleRelative, staleId);
            WriteFile(libraryPath, Path.ChangeExtension(staleRelative, ".mediainfo.json"), "{ }");
            WriteFile(libraryPath, foreignRelative, "https://other.example/video.strm");
            WriteFile(libraryPath, quarantinedRelative, "https://nzbdav.example/api/stream/ghost?token=1.ghost");

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example"
            };

            var success = InvokeReconcileStaleFiles(task, config, [activeRelative], "20260416-120000", CancellationToken.None);
            Assert.True(success);

            Assert.True(File.Exists(Path.Combine(libraryPath, activeRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, activeRelative + ".nzbdav.managed")));
            Assert.True(File.Exists(Path.Combine(libraryPath, foreignRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, quarantinedRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", ".nomedia")));
            // Stale source pathnames are retained; the tombstone makes the
            // quarantine logical without risking a foreign replacement.
            Assert.True(File.Exists(Path.Combine(libraryPath, staleRelative)));
            Assert.True(File.Exists(Path.Combine(libraryPath, staleRelative + ".nzbdav.managed")));
            Assert.True(File.Exists(Path.Combine(libraryPath, Path.ChangeExtension(staleRelative, ".mediainfo.json"))));
            Assert.True(File.Exists(Path.Combine(libraryPath, staleRelative + ".nzbdav.tombstone")));

            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", "movies", "OldMovie", "OldMovie.strm.quarantined")));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", "movies", "OldMovie", "OldMovie.mediainfo.json.quarantined")));
            Assert.True(File.Exists(Path.Combine(libraryPath, ".quarantine", "movies", "OldMovie", "OldMovie.strm.nzbdav.managed.quarantined")));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    [Fact]
    public async Task SyncVideoFile_MissingStreamsProbeDoesNotPublishSidecar()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"}}")
                    : throw new XunitException($"Unexpected request: {request.RequestUri}")));

            var succeeded = await InvokeSyncVideoFile(
                new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance),
                config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.False(succeeded);
            Assert.False(File.Exists(Path.Combine(root, "Movie.mediainfo.json")));
            Assert.False(File.Exists(Path.Combine(root, "Movie.strm.nzbdav.managed")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_ExistingOwnedNoProbeAddsProbeWithMarkerBinding()
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            WriteFile(root, "Movie.strm", $"https://nzbdav.example/api/stream/{id}?token={DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds()}.{CanonicalToken}");
            WriteManagedMarker(root, "Movie.strm", id);
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : throw new XunitException($"Unexpected request: {request.RequestUri}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.True(File.Exists(probe));
            Assert.DoesNotContain("/none/", File.ReadAllText(strm + ".nzbdav.managed"));
            Assert.Contains("nzbdav.managed/4/", File.ReadAllText(strm + ".nzbdav.managed"));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_ProbeAdditionMarkerSameInodeReversionFailsClosed()
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var now = DateTimeOffset.UtcNow;
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file", HasProbeData = true };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            WriteFile(root, "Movie.strm", $"https://nzbdav.example/api/stream/{id}?token={now.AddDays(1).ToUnixTimeSeconds()}.{CanonicalToken}");
            WriteManagedMarker(root, "Movie.strm", id);
            var marker = strm + ".nzbdav.managed";
            var oldMarker = File.ReadAllBytes(marker);
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var staleWindowEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staleWindowRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var barrierCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var firstMarkerCallback = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler(
                (Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>)(async (request, _) =>
                {
                    // Ensure reflection returns the operation task before the
                    // synchronous publication hook reaches the barrier.
                    await Task.Yield();
                    if (request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true)
                        return TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
                    if (request.RequestUri?.AbsolutePath.StartsWith("/api/meta/", StringComparison.Ordinal) == true)
                        return TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}");
                    throw new XunitException($"Unexpected request: {request.RequestUri}");
                })));

            // The handler's asynchronous yield above ensures this invocation
            // returns before the synchronous publication hook reaches the
            // barrier; no worker-thread scheduling race is needed.
            var operation = InvokeSyncVideoFile(new NzbdavLibrarySyncTask(
                    NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(now),
                    path =>
                    {
                        if (string.Equals(path, marker, StringComparison.Ordinal)
                            && Interlocked.Exchange(ref firstMarkerCallback, 1) == 0)
                        {
                            // The marker has just been committed with the probe
                            // binding. Restore the pre-probe bytes while the
                            // carried descriptor ownership remains in memory.
                            File.WriteAllBytes(path, oldMarker);
                            staleWindowEntered.TrySetResult(true);
                            staleWindowRelease.Task.Wait(barrierCancellation.Token);
                        }
                    }),
                config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            try
            {
                await staleWindowEntered.Task.WaitAsync(barrierCancellation.Token);
                Assert.Contains("/none/", File.ReadAllText(marker));
            }
            finally
            {
                // Always unblock the synchronous production hook, including
                // barrier timeout/cancellation paths, before awaiting its task.
                staleWindowRelease.TrySetResult(true);
                barrierCancellation.Cancel();
            }

            await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            // A same-inode reversion after probe publication is indistinguishable
            // from an operator edit. The following rotation must not bless or
            // overwrite it; recovery remains pending for an explicit retry.
            Assert.Contains("token=", File.ReadAllText(strm));
            Assert.Equal(oldMarker, File.ReadAllBytes(marker));
            Assert.True(File.Exists(probe));
            Assert.Single(Directory.EnumerateFiles(root, "*.nzbdav.managed", SearchOption.AllDirectories));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData("probe")]
    [InlineData("publication")]
    [InlineData("marker")]
    public async Task SyncVideoFile_ProbeAdditionCrashSeamsRecoverWithoutMixedPair(string failurePoint)
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file", HasProbeData = true };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            var marker = strm + ".nzbdav.managed";
            WriteFile(root, "Movie.strm", $"https://nzbdav.example/api/stream/{id}?token={DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds()}.{CanonicalToken}");
            WriteManagedMarker(root, "Movie.strm", id);
            var oldMarker = File.ReadAllBytes(marker);
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var count = 0;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    count++;
                    var isProbe = string.Equals(path, probe, StringComparison.Ordinal);
                    var isMarker = string.Equals(path, marker, StringComparison.Ordinal);
                    var isRecovery = path.Contains(".nzbdav-recovery", StringComparison.Ordinal);
                    var shouldFail = failurePoint switch
                    {
                        "probe" => isProbe,
                        "marker" => isMarker,
                        // The fourth publication callback is the fixed-slot
                        // publication phase (intent, ready, probe, publication).
                        "publication" => isRecovery && count == 4,
                        _ => false
                    };
                    if (shouldFail)
                        throw new IOException("simulated probe transaction crash");
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : throw new XunitException($"Unexpected request: {request.RequestUri}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var retryTask = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            await InvokeSyncVideoFile(retryTask, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(probe));
            Assert.Contains("/4/", File.ReadAllText(marker));
            Assert.NotEqual(oldMarker, File.ReadAllBytes(marker));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_ProbeAdditionForeignReplacementIsNeverDeleted()
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file", HasProbeData = true };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            var marker = strm + ".nzbdav.managed";
            WriteFile(root, "Movie.strm", $"https://nzbdav.example/api/stream/{id}?token={DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds()}.{CanonicalToken}");
            WriteManagedMarker(root, "Movie.strm", id);
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var replaced = false;
            var markerFailureInjected = false;
            var probeHookCount = 0;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (string.Equals(path, probe, StringComparison.Ordinal)
                        && ++probeHookCount == 2)
                    {
                        // The first callback is publication. The second is
                        // recovery after its identity/hash check, immediately
                        // before the old implementation would unlinkat.
                        replaced = true;
                        File.Delete(probe);
                        File.WriteAllText(probe, "foreign-probe");
                    }
                    else if (!markerFailureInjected && string.Equals(path, marker, StringComparison.Ordinal))
                    {
                        markerFailureInjected = true;
                        throw new IOException("simulated crash before probe recovery");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : throw new XunitException($"Unexpected request: {request.RequestUri}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.True(replaced);
            Assert.Equal("foreign-probe", File.ReadAllText(probe));
            Assert.Contains("/none/", File.ReadAllText(marker));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_MissingProbeUsesNoReplaceAfterHttpDelay()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/movies/Movie/Movie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            var relative = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
            var url = $"https://nzbdav.example/api/stream/{id}?token={DateTimeOffset.UtcNow.AddDays(6).ToUnixTimeSeconds()}.{CanonicalToken}";
            WriteFile(root, relative, url);
            WriteManagedMarker(root, relative, id);
            var probe = Path.Combine(root, Path.ChangeExtension(relative, ".mediainfo.json"));
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
            {
                if (request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true)
                {
                    File.WriteAllText(probe, "foreign-probe");
                    return TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}");
                }

                throw new XunitException($"Unexpected request: {request.RequestUri}");
            }));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, new FixedTimeProvider(DateTimeOffset.UtcNow));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.Equal("foreign-probe", File.ReadAllText(probe));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_DoesNotQuarantineSwappedSourcePath()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        var outsideRoot = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-outside-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        Directory.CreateDirectory(outsideRoot);

        var staleRelative = Path.Combine("movies", "OldMovie", "OldMovie.strm");
        var staleTarget = Path.Combine(libraryPath, staleRelative);
        WriteFile(libraryPath, staleRelative, "https://nzbdav.example/api/stream/aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        WriteManagedMarker(libraryPath, staleRelative, Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        var outsideTarget = Path.Combine(outsideRoot, "outside.txt");
        File.WriteAllText(outsideTarget, "outside-content");

        try
        {
            var didSwap = false;
            Action<string> mutationHook = path =>
            {
                if (!didSwap
                    && string.Equals(path, staleTarget, StringComparison.Ordinal))
                {
                    didSwap = true;
                    File.Delete(staleTarget);
                    File.CreateSymbolicLink(staleTarget, outsideTarget);
                }
            };

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, pathMutationHook: mutationHook);
            var config = new PluginConfiguration
            {
                LibraryPath = libraryPath,
                NzbdavBaseUrl = "https://nzbdav.example"
            };
            var reconciled = InvokeReconcileStaleFiles(task, config, [], "20260416-120000", CancellationToken.None);

            Assert.True(didSwap);
            Assert.False(reconciled);
            Assert.True(File.Exists(staleTarget));
            Assert.True(File.Exists(outsideTarget));
            var quarantined = Path.Combine(libraryPath, ".quarantine", "movies", "OldMovie", "OldMovie.strm.quarantined");
            Assert.False(File.Exists(quarantined));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
            if (Directory.Exists(outsideRoot))
                Directory.Delete(outsideRoot, recursive: true);
        }
    }

    [Fact]
    public void ReconcileStaleFiles_DestinationPostCheckRaceLeavesForeignDestinationUntouched()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            var id = Guid.NewGuid();
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}");
            WriteManagedMarker(root, relative, id);
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            var replaced = false;
            Action<string> mutationHook = path =>
            {
                if (!replaced && string.Equals(path, destination, StringComparison.Ordinal))
                {
                    replaced = true;
                    File.Delete(destination);
                    File.WriteAllText(destination, "foreign-destination");
                }
            };

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance, pathMutationHook: mutationHook);
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example"
            };

            Assert.False(InvokeReconcileStaleFiles(task, config, [], "destination-race", CancellationToken.None));
            Assert.True(replaced);
            Assert.Equal("foreign-destination", File.ReadAllText(destination));
            Assert.DoesNotContain(Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories), path =>
                Path.GetFileName(path).StartsWith(".nzbdav.tmp-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith(".nzbdav.quarantine-tmp-", StringComparison.Ordinal));
            Assert.Equal($"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}", File.ReadAllText(Path.Combine(root, relative)));
            Assert.False(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_CopyRetainsExactSourceContent()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            var id = Guid.NewGuid();
            var source = Path.Combine(root, relative);
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{id}?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(root, relative, id);
            var sourceIdentity = InvokeCaptureFileIdentity(root, source);

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example"
            };

            Assert.True(InvokeReconcileStaleFiles(task, config, [], "identity", CancellationToken.None));
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            Assert.NotEqual(sourceIdentity, InvokeCaptureFileIdentity(root, destination));
            Assert.Equal(File.ReadAllText(source), File.ReadAllText(destination));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_SameInodeSourceRewriteDoesNotPublishQuarantineCopy_AndRetrySucceeds()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            var id = Guid.NewGuid();
            var source = Path.Combine(root, relative);
            var originalContent = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            const string rewrittenContent = "foreign-in-place-rewrite";
            WriteFile(root, relative, originalContent);
            WriteManagedMarker(root, relative, id);
            var sourceIdentity = InvokeCaptureFileIdentity(root, source);
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            var rewritten = false;
            var firstAttempt = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!rewritten && string.Equals(path, source, StringComparison.Ordinal))
                    {
                        rewritten = true;
                        File.WriteAllText(source, rewrittenContent);
                    }
                });
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };

            Assert.False(InvokeReconcileStaleFiles(firstAttempt, config, [], "same-inode-rewrite", CancellationToken.None));
            Assert.True(rewritten);
            Assert.Equal(sourceIdentity, InvokeCaptureFileIdentity(root, source));
            Assert.Equal(rewrittenContent, File.ReadAllText(source));
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));

            File.WriteAllText(source, originalContent);
            Assert.Equal(sourceIdentity, InvokeCaptureFileIdentity(root, source));
            Assert.True(InvokeReconcileStaleFiles(
                new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance),
                config, [], "same-inode-rewrite-retry", CancellationToken.None));
            Assert.Equal(originalContent, File.ReadAllText(destination));
            Assert.True(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Windows_PreoccupiedPreparationSlotsAreForeignEvenWhenContentMatches(bool firstSlotMatches)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file" };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var token = CurrentToken;
            var expectedUrl = $"https://nzbdav.example/api/stream/{id}?token={token}";
            var directory = root;
            Directory.CreateDirectory(directory);
            var slot0 = Path.Combine(directory, ".nzbdav.prepare-0");
            var slot1 = Path.Combine(directory, ".nzbdav.prepare-1");
            File.WriteAllText(slot0, firstSlotMatches ? expectedUrl : "foreign-slot-0");
            File.WriteAllText(slot1, expectedUrl);

            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/meta/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"streamToken\":\"" + token + "\"}")
                    : throw new XunitException($"Unexpected request: {request.RequestUri}")));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            var strm = Path.Combine(directory, "Movie.strm");
            Assert.False(File.Exists(strm));
            Assert.Equal(firstSlotMatches ? expectedUrl : "foreign-slot-0", File.ReadAllText(slot0));
            Assert.Equal(expectedUrl, File.ReadAllText(slot1));

            // Removing only the operator-owned collision permits a clean retry;
            // the remaining pre-existing slot is still never adopted or moved.
            File.Delete(slot0);
            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.Equal(expectedUrl, File.ReadAllText(strm));
            Assert.True(File.Exists(strm + ".nzbdav.managed"));
            Assert.Equal(expectedUrl, File.ReadAllText(slot1));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Windows_PreoccupiedQuarantineTempIsForeignEvenWhenContentMatches(bool exactContent)
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            var id = Guid.NewGuid();
            var sourceContent = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, sourceContent);
            WriteManagedMarker(root, relative, id);
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            var temp = destination + ".tmp";
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            File.WriteAllText(temp, exactContent ? sourceContent : "foreign-quarantine-temp");

            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            Assert.False(InvokeReconcileStaleFiles(task, config, [], "foreign-temp", CancellationToken.None));
            Assert.Equal(exactContent ? sourceContent : "foreign-quarantine-temp", File.ReadAllText(temp));
            Assert.False(File.Exists(destination));
            Assert.Equal(sourceContent, File.ReadAllText(Path.Combine(root, relative)));

            File.Delete(temp);
            Assert.True(InvokeReconcileStaleFiles(task, config, [], "foreign-temp-retry", CancellationToken.None));
            Assert.Equal(sourceContent, File.ReadAllText(destination));
            Assert.True(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_RestartsAfterCopyBeforeTombstoneAndConverges()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/Restart/Restart.strm";
            var id = Guid.NewGuid();
            var content = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, content);
            WriteManagedMarker(root, relative, id);
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            var interrupted = false;
            var firstAttempt = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!interrupted && path.EndsWith("Restart.strm.nzbdav.managed.quarantined", StringComparison.Ordinal))
                    {
                        interrupted = true;
                        throw new IOException("simulated restart after durable quarantine copy");
                    }
                });
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };

            Assert.False(InvokeReconcileStaleFiles(firstAttempt, config, [], "copy-before-tombstone", CancellationToken.None));
            Assert.True(interrupted);
            Assert.True(File.Exists(destination));
            Assert.False(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));

            Assert.True(InvokeReconcileStaleFiles(
                new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance),
                config, [], "copy-before-tombstone-retry", CancellationToken.None));
            Assert.Equal(content, File.ReadAllText(destination));
            Assert.True(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public void ReconcileStaleFiles_MatchingBytesRequireSameDurableManagedMarker(
        bool foreignMarkerPresent, bool matchingStreamBytes)
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/Foreign/Foreign.strm";
            var id = Guid.NewGuid();
            var sourceContent = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, sourceContent);
            WriteManagedMarker(root, relative, id);
            var sourceMarker = File.ReadAllBytes(Path.Combine(root, relative + ".nzbdav.managed"));
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            var destinationMarker = Path.Combine(root, ".quarantine", relative + ".nzbdav.managed.quarantined");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllText(destination, matchingStreamBytes ? sourceContent : "foreign-stream-bytes");
            if (foreignMarkerPresent)
                File.WriteAllText(destinationMarker, "foreign-marker");

            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };
            var exception = Record.Exception(() => InvokeReconcileStaleFiles(
                new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance),
                config, [], "foreign-marker", CancellationToken.None));
            Assert.NotNull(exception);
            Assert.IsType<TargetInvocationException>(exception);
            Assert.Equal(matchingStreamBytes ? sourceContent : "foreign-stream-bytes", File.ReadAllText(destination));
            Assert.Equal(sourceMarker, File.ReadAllBytes(Path.Combine(root, relative + ".nzbdav.managed")));
            Assert.False(File.Exists(Path.Combine(root, relative + ".nzbdav.tombstone")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public void ReconcileStaleFiles_SourceCollisionHardFailsWithoutMovingSource()
    {
        var root = CreateTestLibrary();
        try
        {
            const string relative = "movies/OldMovie/OldMovie.strm";
            var id = Guid.NewGuid();
            var destination = Path.Combine(root, ".quarantine", relative + ".quarantined");
            WriteFile(root, relative, $"https://nzbdav.example/api/stream/{id}?token=1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
            WriteManagedMarker(root, relative, id);
            WriteFile(root, Path.GetRelativePath(root, destination), "pre-existing collision");

            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example"
            };

            var collision = Assert.Throws<TargetInvocationException>(() =>
                InvokeReconcileStaleFiles(task, config, [], "collision", CancellationToken.None));
            Assert.IsType<IOException>(collision.InnerException);
            Assert.True(File.Exists(Path.Combine(root, relative)));
            Assert.Equal("pre-existing collision", File.ReadAllText(destination));
        }
        finally { DeleteTestLibrary(root); }
    }

    private static string InvokeBuildStrmRelativePath(
        ManifestItem video,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        try
        {
            return (string)BuildRelativePathMethod.Invoke(null, [video, allItems])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static bool InvokeIsNzbdavManagedStrmContent(string content, string baseUrl)
    {
        return (bool)IsNzbdavManagedStrmContentMethod.Invoke(null, [content, baseUrl])!;
    }

    private static string InvokeGetQuarantineRelativePath(string relativePath, string runId)
    {
        return (string)GetQuarantineRelativePathMethod.Invoke(null, [relativePath, runId])!;
    }

    private static string[] InvokeBuildExpectedStrmRelativePaths(
        ManifestItem[] items,
        IReadOnlyDictionary<Guid, ManifestItem> allItems)
    {
        try
        {
            return (string[])BuildExpectedStrmRelativePathsMethod.Invoke(null, [items, allItems])!;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static string CreateTestLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteTestLibrary(string root)
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    private static bool RunReconcile(string root)
    {
        var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
        var config = new PluginConfiguration
        {
            LibraryPath = root,
            NzbdavBaseUrl = "https://nzbdav.example"
        };
        return InvokeReconcileStaleFiles(task, config, [], Guid.NewGuid().ToString("N"), CancellationToken.None);
    }

    private static bool InvokeReconcileStaleFiles(
        NzbdavLibrarySyncTask task,
        PluginConfiguration config,
        string[] expectedRelativePaths,
        string runId,
        CancellationToken ct)
    {
        return (bool)(ReconcileStaleFilesMethod.Invoke(task, [config, expectedRelativePaths, runId, ct])
            ?? throw new InvalidOperationException("ReconcileStaleFiles did not return a value."));
    }

    private static void InvokeSyncDirectory(string path)
    {
        try
        {
            SyncDirectoryMethod.Invoke(null, [path]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static Task InvokeWriteTextAtomicallyAsync(string libraryRoot, string path, string content, CancellationToken ct)
        => InvokeWriteTextAtomicallyAsync(libraryRoot, path, content, ct, mutationHook: null);

    private static Task InvokeWriteTextAtomicallyAsync(
        string libraryRoot, string path, string content, CancellationToken ct, Action<string>? mutationHook)
    {
        try
        {
            return (Task?)WriteTextAtomicallyAsyncMethod.Invoke(
                null,
                [libraryRoot, path, content, ct, null, null, mutationHook])
                ?? throw new InvalidOperationException("WriteTextAtomicallyAsync did not return a Task.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static async Task<bool> InvokeRefreshExistingTokens(
        NzbdavLibrarySyncTask task,
        PluginConfiguration config,
        NzbdavApiClient client,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(config.NzbdavBaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("Invalid NZBDAV base URL in test helper.");

        var method = typeof(NzbdavLibrarySyncTask).GetMethod("RefreshExistingTokens", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find RefreshExistingTokens.");
        var operation = (Task<bool>?)method.Invoke(task, [config, client, baseUri, ct])
            ?? throw new InvalidOperationException("RefreshExistingTokens did not return a Task<bool>.");
        return await operation;
    }

    private static async Task<bool> InvokeSyncVideoFile(
        NzbdavLibrarySyncTask task,
        PluginConfiguration config,
        NzbdavApiClient client,
        ManifestItem video,
        Dictionary<Guid, ManifestItem> allItems,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(config.NzbdavBaseUrl, UriKind.Absolute, out var baseUri))
            throw new InvalidOperationException("Invalid NZBDAV base URL in test helper.");

        var method = typeof(NzbdavLibrarySyncTask).GetMethod("SyncVideoFile", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Could not find SyncVideoFile.");
        var operation = (Task<bool>?)method.Invoke(task, [config, client, baseUri, video, allItems, ct])
            ?? throw new InvalidOperationException("SyncVideoFile did not return a Task<bool>.");
        return await operation;
    }


    private static NzbdavMediaSourceProvider CreateProvider(string baseUrl, string root)
        => new(NullLogger<NzbdavMediaSourceProvider>.Instance,
            () => new NzbdavOperationConfiguration(baseUrl, root, string.Empty, 30));

    private static bool InvokeTryReadBoundedFirstLine(string libraryRoot, string path, int maxBytes)
    {
        var method = typeof(NzbdavLibrarySyncTask).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(candidate => candidate.Name == "TryReadBoundedFirstLineForPath"
                && candidate.GetParameters().Length == 4);
        var arguments = new object?[] { libraryRoot, path, maxBytes, null };
        return (bool)(method.Invoke(null, arguments) ?? false);
    }

    private static object? InvokeCaptureFileIdentity(string libraryRoot, string path)
    {
        try
        {
            return CaptureFileIdentityMethod.Invoke(null, [libraryRoot, path]);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void PartitionSkipsAnUnrepresentableReleaseAndItsDescendantsButKeepsTheRest()
    {
        var goodDir = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Good Movie",
            Path = "/content/movies/Good Movie",
            Type = "directory"
        };
        var goodFile = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = goodDir.Id,
            Name = "Good.Movie.mkv",
            Path = "/content/movies/Good Movie/Good.Movie.mkv",
            Type = "nzb_file"
        };
        // Real-world offender: release directory name ends with a period,
        // which Windows/SMB filesystems cannot represent.
        var badDir = new ManifestItem
        {
            Id = Guid.NewGuid(),
            Name = "Bad.Release.BLURAY-UNTOUCHED.",
            Path = "/content/movies/Bad.Release.BLURAY-UNTOUCHED.",
            Type = "directory"
        };
        var badDescendant = new ManifestItem
        {
            Id = Guid.NewGuid(),
            ParentId = badDir.Id,
            Name = "movie.mkv",
            Path = "/content/movies/Bad.Release.BLURAY-UNTOUCHED./movie.mkv",
            Type = "nzb_file"
        };

        var (representable, skipped) = NzbdavLibrarySyncTask.PartitionRepresentableManifestItems(
            [goodDir, goodFile, badDir, badDescendant]);

        Assert.Equal([goodDir, goodFile], representable);
        Assert.Equal(
            [badDir.Path, badDescendant.Path],
            skipped);
    }

    [Fact]
    public void PartitionKeepsAFullyRepresentableManifestIntact()
    {
        var items = new[]
        {
            new ManifestItem { Id = Guid.NewGuid(), Name = "movies", Path = "/content/movies", Type = "directory" },
            new ManifestItem { Id = Guid.NewGuid(), Name = "a.mkv", Path = "/content/movies/a.mkv", Type = "nzb_file" }
        };

        var (representable, skipped) = NzbdavLibrarySyncTask.PartitionRepresentableManifestItems(items);

        Assert.Equal(items, representable);
        Assert.Empty(skipped);
    }

    [Fact]
    public async Task ScheduledOperationUsesSnapshotCapturedBeforeManifestAwait()
    {
        var firstRoot = CreateTestLibrary();
        var secondRoot = CreateTestLibrary();
        try
        {
            var first = new NzbdavOperationConfiguration(
                "http://127.0.0.1:1", firstRoot, "first-key", 30);
            var second = new NzbdavOperationConfiguration(
                "http://127.0.0.1:2", secondRoot, "second-key", 30);
            var accessorCalls = 0;
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                timeProvider: null,
                pathMutationHook: null,
                configurationAccessor: () => ++accessorCalls == 1 ? first : second);

            using var gate = await NzbdavLibrarySyncTask.EnterMediaGateAsync(TestContext.Current.CancellationToken);
            var operation = task.ExecuteAsync(new Progress<double>(), TestContext.Current.CancellationToken);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(1, accessorCalls);

            gate.Dispose();
            await operation;
            Assert.False(Directory.Exists(Path.Combine(secondRoot, ".nzbdav-recovery")));
        }
        finally
        {
            DeleteTestLibrary(firstRoot);
            DeleteTestLibrary(secondRoot);
        }
    }

    [Fact]
    public async Task ScheduledOperationFailsClosedForNullOrInvalidConfigurationSnapshot()
    {
        var root = CreateTestLibrary();
        try
        {
            var calls = 0;
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                timeProvider: null,
                pathMutationHook: null,
                configurationAccessor: () =>
                {
                    calls++;
                    return null;
                });

            await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);
            Assert.Equal(1, calls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task ProviderGateWaitIsBoundedByCancellation()
    {
        using var gate = await NzbdavLibrarySyncTask.EnterMediaGateAsync(CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var provider = CreateProvider("https://nzbdav.example", Path.GetTempPath());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetMediaSources(
            new Video { Path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "movie.strm"), Name = "Movie" },
            cts.Token));
    }

    [Fact]
    public async Task TombstoneSuppressesSyncRefreshAndProvider_RejectsStaleForeignReplacement()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "OldMovie.mkv",
                Path = "/content/movies/OldMovie/OldMovie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            const string relative = "movies/OldMovie/OldMovie.strm";
            var stale = $"https://nzbdav.example/api/stream/{id}?token=1.{CanonicalToken}";
            WriteFile(root, relative, stale);
            WriteManagedMarker(root, relative, id);
            WriteFile(root, Path.ChangeExtension(relative, ".mediainfo.json"), "{\"format\":{}} ");
            Assert.True(RunReconcile(root));

            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var calls = 0;
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
            {
                calls++;
                return TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}");
            }));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            await InvokeRefreshExistingTokens(task, config, client, CancellationToken.None);
            Assert.Equal(0, calls);
            Assert.Equal(stale, File.ReadAllText(Path.Combine(root, relative)));
            Assert.False((await CreateProvider(config.NzbdavBaseUrl, root).GetMediaSources(
                new Video { Path = Path.Combine(root, relative), Name = "OldMovie" },
                CancellationToken.None)).Any());

            // Replacing the source pathname creates a different inode. The old
            // tombstone must not hide or mutate this foreign replacement.
            var foreignId = Guid.NewGuid();
            var foreign = $"https://nzbdav.example/api/stream/{foreignId}?token={CurrentToken}";
            File.Delete(Path.Combine(root, relative));
            File.WriteAllText(Path.Combine(root, relative), foreign);
            Assert.True(RunReconcile(root));
            Assert.Equal(foreign, File.ReadAllText(Path.Combine(root, relative)));
            var sources = await CreateProvider(config.NzbdavBaseUrl, root).GetMediaSources(
                new Video { Id = Guid.NewGuid(), Path = Path.Combine(root, relative), Name = "Foreign" },
                CancellationToken.None);
            Assert.Empty(sources);
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData("nzbdav.tombstone/2/1/1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/20260416-120000000Z-aaaaaaaa/extra")]
    [InlineData("nzbdav.tombstone/2/1/1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/invalid-run")]
    [InlineData(" nzbdav.tombstone/2/1/1/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA/20260416-120000000Z-aaaaaaaa")]
    public void TombstoneV2RequiresExactFieldsAndRunIdGrammar(string tombstone)
    {
        var root = CreateTestLibrary();
        try
        {
            var source = Path.Combine(root, "movie.strm");
            File.WriteAllText(source, "foreign-visible");
            File.WriteAllText(source + ".nzbdav.tombstone", tombstone);
            Assert.False(NzbdavLibrarySyncTask.IsLogicallyTombstoned(root, source));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task FreshPartialOutputRetainsIntentAndConvergesOnRetry()
    {
        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var failOnce = true;
            var strm = Path.Combine(root, "Movie.strm");
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (failOnce && string.Equals(path, strm, StringComparison.Ordinal))
                    {
                        failOnce = false;
                        throw new IOException("publish fault");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(strm));
            Assert.True(File.Exists(strm + ".nzbdav.managed.intent"));
            Assert.False(File.Exists(Path.ChangeExtension(strm, ".mediainfo.json")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(Path.ChangeExtension(strm, ".mediainfo.json")));
            Assert.Contains("/api/stream/" + id, File.ReadAllText(strm));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Theory]
    [InlineData("stream")]
    [InlineData("probe")]
    [InlineData("completion")]
    public async Task FreshPublicationCrashBeforeEachLinkRetainsExactIntentAndRetries(string failurePoint)
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem
            {
                Id = id,
                Name = "Movie.mkv",
                Path = "/content/Movie.mkv",
                Type = "nzb_file",
                HasProbeData = true
            };
            var config = new PluginConfiguration
            {
                LibraryPath = root,
                NzbdavBaseUrl = "https://nzbdav.example",
                ApiKey = "header"
            };
            var strm = Path.Combine(root, "Movie.strm");
            var probe = Path.ChangeExtension(strm, ".mediainfo.json");
            var completion = strm + ".nzbdav.managed";
            var intent = strm + ".nzbdav.managed.intent";
            var failed = false;
            var target = failurePoint switch
            {
                "stream" => strm,
                "probe" => probe,
                _ => completion
            };
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (!failed && string.Equals(path, target, StringComparison.Ordinal))
                    {
                        failed = true;
                        throw new IOException("simulated publication crash");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(intent));
            Assert.True(File.Exists(strm));
            if (failurePoint == "stream")
                Assert.False(File.Exists(probe));
            else
                Assert.True(File.Exists(probe));
            if (failurePoint == "completion")
                Assert.True(File.Exists(completion));
            else
                Assert.False(File.Exists(completion));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(strm));
            Assert.True(File.Exists(probe));
            Assert.True(File.Exists(completion));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories), path =>
                Path.GetFileName(path).StartsWith(".nzbdav.tmp-", StringComparison.Ordinal)
                || Path.GetFileName(path).StartsWith("prepare-", StringComparison.Ordinal));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task FreshIntentCannotAdoptExactContentForeignInodeOrQuarantineIt()
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file" };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            var failed = true;
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path =>
                {
                    if (failed && string.Equals(path, strm, StringComparison.Ordinal))
                    {
                        failed = false;
                        throw new IOException("simulated publication crash");
                    }
                });
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            var exactContent = File.ReadAllText(strm);
            File.Delete(strm);
            File.WriteAllText(strm, exactContent);

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.Equal(exactContent, File.ReadAllText(strm));
            Assert.False(File.Exists(strm + ".nzbdav.managed"));
            Assert.False(Directory.Exists(Path.Combine(root, ".quarantine")));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task FreshIntentMissingOutputsMaySupersedeOnRetry()
    {
        if (OperatingSystem.IsLinux() && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT")))
            return;

        var root = CreateTestLibrary();
        try
        {
            var id = Guid.NewGuid();
            var video = new ManifestItem { Id = id, Name = "Movie.mkv", Path = "/content/Movie.mkv", Type = "nzb_file", HasProbeData = true };
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example", ApiKey = "header" };
            var strm = Path.Combine(root, "Movie.strm");
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance,
                pathMutationHook: path => throw new IOException("crash before stream retry"));
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                request.RequestUri?.AbsolutePath.StartsWith("/api/probe/", StringComparison.Ordinal) == true
                    ? TestHttpMessageHandler.Json("{\"format\":{\"format_name\":\"matroska\"},\"streams\":[]}")
                    : TestHttpMessageHandler.Json("{\"streamToken\":\"" + CurrentToken + "\"}")));

            await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            File.Delete(strm);
            // The retry has no source inode to adopt; it may supersede the exact
            // missing output with a newly prepared inode bound into the intent.
            var retryTask = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            await InvokeSyncVideoFile(retryTask, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);
            Assert.True(File.Exists(strm));
            Assert.True(File.Exists(Path.ChangeExtension(strm, ".mediainfo.json")));
            Assert.True(File.Exists(strm + ".nzbdav.managed"));
        }
        finally { DeleteTestLibrary(root); }
    }

    [Fact]
    public async Task SyncVideoFile_AdoptsMarkerAfterDeviceNumberChanged()
    {
        // Device-mapper reassigns LVM minor numbers whenever a volume is
        // reactivated, so a marker written before a reboot records an st_dev
        // that no longer matches a byte-identical file. Ownership must still
        // be adopted; refusing pins the manifest ETag forever.
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var id = Guid.NewGuid();
        var libraryPath = Path.Combine(Path.GetTempPath(), "nzbdav-jellyfin-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(libraryPath);
        var video = new ManifestItem
        {
            Id = id,
            Name = "Movie.mkv",
            Path = "/content/movies/Movie/Movie.mkv",
            Type = "nzb_file"
        };

        var expectedRelativePath = Path.ChangeExtension(InvokeBuildStrmRelativePath(video, new Dictionary<Guid, ManifestItem>()), ".strm");
        WriteFile(libraryPath, expectedRelativePath, $"https://nzbdav.example/api/stream/{id}?apikey=legacy-secret");
        WriteManagedMarker(libraryPath, expectedRelativePath, id);

        var markerPath = Path.Combine(libraryPath, expectedRelativePath) + ".nzbdav.managed";
        var parts = File.ReadAllText(markerPath).Split('/');
        parts[4] = (ulong.Parse(parts[4]) + 15).ToString();
        File.WriteAllText(markerPath, string.Join('/', parts));

        var config = new PluginConfiguration
        {
            LibraryPath = libraryPath,
            NzbdavBaseUrl = "https://nzbdav.example",
            ApiKey = "header"
        };

        try
        {
            var task = new NzbdavLibrarySyncTask(
                NullLogger<NzbdavLibrarySyncTask>.Instance,
                new FixedTimeProvider(now));
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((request, _) =>
                TestHttpMessageHandler.Json($"{{\"streamToken\":\"{now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}\"}}")));

            var adopted = await InvokeSyncVideoFile(task, config, client, video, new Dictionary<Guid, ManifestItem>(), CancellationToken.None);

            Assert.True(adopted);
            Assert.Equal(
                $"https://nzbdav.example/api/stream/{id}?token={now.AddDays(7).ToUnixTimeSeconds()}.{CanonicalToken}",
                File.ReadAllText(Path.Combine(libraryPath, expectedRelativePath)));
        }
        finally
        {
            if (Directory.Exists(libraryPath))
                Directory.Delete(libraryPath, recursive: true);
        }
    }

    private static void WriteFile(string libraryPath, string relativePath, string content)
    {
        var fullPath = Path.Combine(libraryPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        File.WriteAllText(fullPath, content);

        // Keep fixtures on the strict completion marker when a probe is written
        // after its .strm marker. The marker binds both output identities and
        // hashes, just like the production writer.
        if (relativePath.EndsWith(".mediainfo.json", StringComparison.OrdinalIgnoreCase))
        {
            var strmPath = fullPath[..^".mediainfo.json".Length] + ".strm";
            var markerPath = strmPath + ".nzbdav.managed";
            if (File.Exists(markerPath))
            {
                var markerParts = File.ReadAllText(markerPath).Trim().Split('/');
                if (markerParts.Length >= 4 && Guid.TryParse(markerParts[3], out var v4Id))
                    RewriteCompletionMarker(libraryPath, strmPath, markerPath, v4Id);
                else if (markerParts.Length >= 3 && Guid.TryParse(markerParts[2], out var legacyId))
                    RewriteCompletionMarker(libraryPath, strmPath, markerPath, legacyId);
            }
        }
    }

    private static void WriteManagedMarker(string libraryPath, string relativePath, Guid id)
    {
        var strmPath = Path.Combine(libraryPath, relativePath);
        var markerPath = strmPath + ".nzbdav.managed";
        RewriteCompletionMarker(libraryPath, strmPath, markerPath, id);
    }

    private static void RewriteCompletionMarker(string libraryPath, string strmPath, string markerPath, Guid id)
    {
        var streamIdentity = InvokeCaptureFileIdentity(libraryPath, strmPath)
            ?? throw new InvalidOperationException("Could not capture stream identity.");
        var identityType = streamIdentity.GetType();
        var streamDevice = (ulong)identityType.GetProperty("Device")!.GetValue(streamIdentity)!;
        var streamInode = (ulong)identityType.GetProperty("Inode")!.GetValue(streamIdentity)!;
        var streamBytes = File.ReadAllBytes(strmPath);
        var streamHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(streamBytes));
        var probePath = Path.ChangeExtension(strmPath, ".mediainfo.json");
        var probe = File.Exists(probePath) ? InvokeCaptureFileIdentity(libraryPath, probePath) : null;
        var probeDevice = 0UL;
        var probeInode = 0UL;
        var probeHash = "none";
        if (probe is not null)
        {
            var probeType = probe.GetType();
            probeDevice = (ulong)probeType.GetProperty("Device")!.GetValue(probe)!;
            probeInode = (ulong)probeType.GetProperty("Inode")!.GetValue(probe)!;
            probeHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(probePath)));
        }

        var encodedUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(File.ReadAllText(strmPath)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        File.WriteAllText(markerPath,
            $"nzbdav.managed/4/{Guid.NewGuid():N}/{id:D}/{streamDevice}/{streamInode}/{streamHash}/{probeDevice}/{probeInode}/{probeHash}/{encodedUrl}");
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed class DisposableAction(Action action) : IDisposable
    {
        private readonly Action _action = action;

        public void Dispose() => _action();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

}
