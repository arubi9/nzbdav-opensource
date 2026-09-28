using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.Nzbdav.Api;
using Jellyfin.Plugin.Nzbdav.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavSiblingIndexTests
{
    private const string Obfuscated = "aBcD0123456789efGh";

    [Theory]
    [InlineData("Movie.2026.mkv", "nzb_file", true, false, true)]
    [InlineData("MOVIE.2026.MP4", "unknown", true, false, true)]
    [InlineData("Movie.2026.mkv", "directory", true, false, true)]
    [InlineData("Movie.2026.mkv", "nzb_file", false, false, false)]
    [InlineData("Movie.2026.txt", "nzb_file", true, false, false)]
    [InlineData("Movie.2026.extra.mkv", "nzb_file", true, false, false)]
    [InlineData("Movie.2026.mkv", "nzb_file", true, true, false)]
    public void MappingPreservesCanonicalCandidateAndSelfExclusionRules(
        string candidateName, string candidateType, bool sameParent, bool sameId, bool keepObfuscated)
    {
        var parent = Parent("Movie.2026");
        var video = Video(parent);
        var candidate = new ManifestItem
        {
            Id = sameId ? video.Id : Guid.NewGuid(), ParentId = sameParent ? parent.Id : Guid.NewGuid(),
            Name = candidateName, Path = "/content/unrelated-path.txt", Type = candidateType
        };
        // The old predicate uses Name and ParentId, not candidate Path or Type.
        var all = new Dictionary<Guid, ManifestItem> { [parent.Id] = parent, [candidate.Id] = candidate };
        var result = (string)Invoke("BuildStrmRelativePath", null, video, all, CreateIndex(all))!;
        Assert.Equal($"movies/{parent.Name}/{(keepObfuscated ? Obfuscated : parent.Name)}.mkv", result.Replace('\\', '/'));
    }

    [Fact]
    public void CanonicalNameComparisonDoesNotAddUnicodeNormalization()
    {
        var parent = Parent("Caf\u00e9");
        var video = Video(parent);
        var candidate = new ManifestItem { Id = Guid.NewGuid(), ParentId = parent.Id, Name = "Cafe\u0301.mkv" };
        var all = new Dictionary<Guid, ManifestItem> { [parent.Id] = parent, [candidate.Id] = candidate };
        var result = (string)Invoke("BuildStrmRelativePath", null, video, all, CreateIndex(all))!;
        Assert.EndsWith("/Caf\u00e9.mkv", result.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherCandidateStillCountsWhenTheFirstCandidateIsSelf()
    {
        var parent = Parent("Movie.2026");
        var video = Video(parent);
        video.Name = parent.Name + ".mkv";
        var other = new ManifestItem { Id = Guid.NewGuid(), ParentId = parent.Id, Name = parent.Name + ".mp4" };
        var all = new Dictionary<Guid, ManifestItem> { [parent.Id] = parent, [video.Id] = video, [other.Id] = other };
        var result = (string)Invoke("BuildStrmRelativePath", null, video, all, CreateIndex(all))!;
        Assert.EndsWith($"/{Obfuscated}.mkv", result.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Readable.Release.2026")]
    [InlineData("Interstellar")]
    public void ReadableAndSingleWordNamesAreNotRenamed(string name)
    {
        var parent = Parent("Different.Parent");
        var video = Video(parent, name);
        var all = new Dictionary<Guid, ManifestItem> { [parent.Id] = parent, [video.Id] = video };
        Assert.EndsWith($"/{name}.mkv", ((string)Invoke("BuildStrmRelativePath", null, video, all, CreateIndex(all))!).Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneIndexIsReusedForExpectedMappingAndPerVideoSyncWithoutAnotherManifestWalk()
    {
        var parent = Parent("Movie.2026");
        var video = Video(parent);
        var canonical = new ManifestItem
        {
            Id = Guid.NewGuid(), ParentId = parent.Id, Name = parent.Name + ".mkv",
            Path = parent.Path + "/" + parent.Name + ".mkv", Type = "nzb_file"
        };
        var items = new[] { parent, video, canonical };
        var all = new CountingItems(items.ToDictionary(i => i.Id));
        var index = CreateIndex(all);
        Assert.NotNull(index);
        Assert.Equal(1, all.ValueWalks);
        all.ForbidValueWalks = true;
        var expected = (string[])Invoke("BuildExpectedStrmRelativePaths", null, items, all, index)!;
        Assert.Contains($"movies/{parent.Name}/{Obfuscated}.strm", expected);

        var root = Path.Combine(Path.GetTempPath(), "nzbdav-sibling-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var config = new PluginConfiguration { LibraryPath = root, NzbdavBaseUrl = "https://nzbdav.example" };
            var token = $"{DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds()}.{new string('A', 43)}";
            var client = new NzbdavApiClient(config, new TestHttpMessageHandler((_, _) =>
                TestHttpMessageHandler.Json($"{{\"streamToken\":\"{token}\"}}")));
            var task = new NzbdavLibrarySyncTask(NullLogger<NzbdavLibrarySyncTask>.Instance);
            Assert.True(await (Task<bool>)Invoke("SyncVideoFile", task, config, client,
                new Uri(config.NzbdavBaseUrl), video, all, TestContext.Current.CancellationToken, index)!);
            Assert.True(File.Exists(Path.Combine(root, "movies", parent.Name, Obfuscated + ".strm")));
            Assert.Equal(1, all.ValueWalks);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task MapsFiftyThousandObfuscatedVideosWithoutQuadraticSiblingScans()
    {
        var items = Enumerable.Range(0, 50_000).SelectMany(i =>
        {
            var parent = Parent($"Movie.Release{i:D6}");
            return new[] { parent, Video(parent) };
        }).ToArray();
        var all = items.ToDictionary(i => i.Id);
        var stopwatch = Stopwatch.StartNew();
        var work = Task.Run(() => (string[])Invoke("BuildExpectedStrmRelativePaths", null, items, all)!);
        var paths = await work.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(50_000, paths.Length);
        Assert.All(paths, path => Assert.DoesNotContain(Obfuscated, path, StringComparison.Ordinal));
        TestContext.Current.TestOutputHelper!.WriteLine($"100000 items / 50000 obfuscated mappings: {stopwatch.ElapsedMilliseconds}ms");
    }

    private static ManifestItem Parent(string name) => new()
    {
        Id = Guid.NewGuid(), Name = name, Path = "/content/movies/" + name, Type = "directory"
    };
    private static ManifestItem Video(ManifestItem parent, string name = Obfuscated) => new()
    {
        Id = Guid.NewGuid(), ParentId = parent.Id, Name = name + ".mkv",
        Path = parent.Path + "/" + name + ".mkv", Type = "nzb_file"
    };
    private static object? CreateIndex(IReadOnlyDictionary<Guid, ManifestItem> all)
    {
        var type = typeof(NzbdavLibrarySyncTask).GetNestedType("CanonicalVideoSiblingIndex", BindingFlags.NonPublic);
        return type is null ? null : Activator.CreateInstance(type, [all]);
    }
    private static object? Invoke(string name, object? target, params object?[] supplied)
    {
        var method = typeof(NzbdavLibrarySyncTask).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!;
        var arguments = method.GetParameters().Select((p, i) => i < supplied.Length ? supplied[i] : p.DefaultValue).ToArray();
        try { return method.Invoke(target, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
    private sealed class CountingItems(Dictionary<Guid, ManifestItem> items) : IReadOnlyDictionary<Guid, ManifestItem>
    {
        public int ValueWalks { get; private set; }
        public bool ForbidValueWalks { get; set; }
        public IEnumerable<ManifestItem> Values
        {
            get
            {
                Assert.False(ForbidValueWalks, "Operation must reuse its sibling index.");
                ValueWalks++;
                return items.Values;
            }
        }
        public int Count => items.Count;
        public IEnumerable<Guid> Keys => items.Keys;
        public ManifestItem this[Guid key] => items[key];
        public bool ContainsKey(Guid key) => items.ContainsKey(key);
        public bool TryGetValue(Guid key, out ManifestItem value) => items.TryGetValue(key, out value!);
        public IEnumerator<KeyValuePair<Guid, ManifestItem>> GetEnumerator() => items.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
