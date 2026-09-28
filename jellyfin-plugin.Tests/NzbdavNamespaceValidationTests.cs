using System.Diagnostics;
using System.Reflection;
using Jellyfin.Plugin.Nzbdav.Api;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

public sealed class NzbdavNamespaceValidationTests
{
    [Theory]
    [InlineData("A", "unknown", "A/b/c", "directory", false)]
    [InlineData("A", "nzb_file", "A/b.txt", "unknown", false)]
    [InlineData("A", "directory", "A/b/c.txt", "nzb_file", true)]
    [InlineData("A", "directory", "A", "nzb_file", false)]
    [InlineData("A", "directory", "A", "directory", false)]
    [InlineData("A", "nzb_file", "A-sibling/child", "directory", true)]
    [InlineData("A/file", "nzb_file", "A/file.more/child", "directory", true)]
    [InlineData("A/file", "nzb_file", "A/FILE/child", "directory", false)]
    [InlineData("Shows/Cafe\u0301", "unknown", "Shows/Caf\u00e9/child", "directory", false)]
    [InlineData("Shows/Cafe\u0301/extra", "directory", "Shows/Caf\u00e9/a.txt", "unknown", false)]
    [InlineData("Shows/Caf\u00e9/extra", "directory", "Shows/Cafe\u0301/a.txt", "unknown", false)]
    [InlineData("Shows/Cafe\u0301/extra", "directory", "Shows/Cafe\u0301/a.txt", "unknown", true)]
    [InlineData("Shows/Cafe\u0301/extra", "directory", "Other/Caf\u00e9/a.txt", "unknown", true)]
    [InlineData("Shows/Cafe\u0301/extra", "directory", "Shows/Caf\u00e9/other", "directory", true)]
    [InlineData("A/Foo/a.txt", "unknown", "B/foo/b.txt", "unknown", true)]
    [InlineData("A/Foo/a.txt", "unknown", "A/foo/b.txt", "unknown", false)]
    [InlineData("root.txt", "unknown", "extra", "directory", true)]
    public void ManifestNamespacePreservesCollisionRulesInEitherOrder(
        string firstPath, string firstType, string secondPath, string secondType, bool accepted)
    {
        var items = new[] { Item(firstPath, firstType), Item(secondPath, secondType) };
        AssertAccepted(accepted, () => Invoke("ValidateManifestNamespace", (object)items));
        Array.Reverse(items);
        AssertAccepted(accepted, () => Invoke("ValidateManifestNamespace", (object)items));
    }

    [Theory]
    [InlineData("Cafe\u0301")]
    [InlineData("Caf\u00e9")]
    public void MultipleRawDirectoryParentSpellingsRejectAFileMatchingEitherSpelling(string parent)
    {
        var items = new[]
        {
            Item("Shows/Cafe\u0301/extra", "directory"),
            Item("Shows/Caf\u00e9/other", "directory"),
            Item($"Shows/{parent}/a.txt", "unknown")
        };
        AssertAccepted(false, () => Invoke("ValidateManifestNamespace", (object)items));
        Array.Reverse(items);
        AssertAccepted(false, () => Invoke("ValidateManifestNamespace", (object)items));
    }

    [Theory]
    [InlineData("Movie.mkv", "Movie.mp4", false)]
    [InlineData("Movie.mkv", "Movie.strm/child.mkv", false)]
    [InlineData("Movie.mkv", "Movie.strm-other/child.mkv", true)]
    public void GeneratedStrmNamespaceChecksMappingCollisionsAndAncestors(
        string first, string second, bool accepted)
    {
        var items = new[] { Item(first, "nzb_file"), Item(second, "nzb_file") };
        AssertAccepted(accepted, () => Invoke("BuildExpectedStrmRelativePaths", items, items.ToDictionary(i => i.Id)));
        Array.Reverse(items);
        AssertAccepted(accepted, () => Invoke("BuildExpectedStrmRelativePaths", items, items.ToDictionary(i => i.Id)));
    }

    [Theory]
    [InlineData("a.strm", "a.strm/b/c.strm", false)]
    [InlineData("a.strm", "a.strm-sibling/b.strm", true)]
    [InlineData("a.strm", "a.strm//b.strm", false)]
    [InlineData("a.strm", "a.strm\\b.strm", false)]
    [InlineData("Cafe\u0301.strm", "Caf\u00e9.strm/b.strm", false)]
    [InlineData("a.strm", "A.strm/b.strm", false)]
    public void OutputNamespacePreservesSegmentBoundariesAndNormalization(string first, string second, bool accepted)
    {
        AssertAccepted(accepted, () => Invoke("ValidatePathNamespace", (object)new[] { first, second }));
        AssertAccepted(accepted, () => Invoke("ValidatePathNamespace", (object)new[] { second, first }));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NamespaceValidationHandlesOneHundredThousandEntriesWithinTenSeconds(bool manifest)
    {
        const int count = 100_000;
        // Mixed depths force the old output validator's shorter-path cross product;
        // files plus explicit directory siblings also exercise the manifest alias check.
        var items = Enumerable.Range(0, count).Select(i => i % 2 == 0
            ? Item($"movies/Release{i:D6}/extra", "directory")
            : Item($"movies/Release{i - 1:D6}/file.txt", "unknown")).ToArray();
        var paths = Enumerable.Range(0, count).Select(i => i % 2 == 0
            ? $"movies/Release{i:D6}.strm"
            : $"tv/Show{i:D6}/Season01/Episode.strm").ToArray();

        var stopwatch = Stopwatch.StartNew();
        var operation = Task.Run(() => manifest
            ? Invoke("ValidateManifestNamespace", (object)items)
            : Invoke("ValidatePathNamespace", (object)paths), TestContext.Current.CancellationToken);
        // Bound even the old quadratic implementation: do not wait minutes to report
        // failure. The isolated test process exits with any timed-out background work.
        await operation.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        TestContext.Current.TestOutputHelper!.WriteLine($"{(manifest ? "Manifest" : "Output")} namespace: {count} entries in {stopwatch.ElapsedMilliseconds} ms");
    }

    private static ManifestItem Item(string path, string type) => new()
    {
        Id = Guid.NewGuid(), Name = path[(path.LastIndexOf('/') + 1)..],
        Path = "/content/" + path, Type = type
    };

    private static void AssertAccepted(bool accepted, Action action)
    {
        if (accepted) action();
        else Assert.Throws<InvalidOperationException>(action);
    }

    private static object? Invoke(string name, params object[] arguments)
    {
        var method = typeof(NzbdavLibrarySyncTask).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing {name}.");
        var supplied = method.GetParameters().Select((p, i) => i < arguments.Length ? arguments[i] : p.DefaultValue).ToArray();
        try { return method.Invoke(null, supplied); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw ex.InnerException; }
    }
}
