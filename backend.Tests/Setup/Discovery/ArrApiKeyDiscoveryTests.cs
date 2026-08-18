using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NzbWebDAV.Setup.Discovery;

namespace NzbWebDAV.Tests.Setup.Discovery;

public sealed class ArrApiKeyDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nzbdav-discovery-tests", Guid.NewGuid().ToString("N"));

    public ArrApiKeyDiscoveryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task ProductionDiscoveryFailsClosedOnWindowsWithoutOpeningBootstrapPaths()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Production Arr discovery targets Linux.");

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            new ArrApiKeyDiscovery().DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.PlatformNotSupported, exception.Failure);
        Assert.DoesNotContain("config.xml", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DiscoversApiKeyFromTheConfiguredPathForEachService()
    {
        var sonarr = WriteConfig("sonarr", "sonarr-secret");
        var radarr = WriteConfig("radarr", "radarr-secret");
        var prowlarr = WriteConfig("prowlarr", "prowlarr-secret");
        var discovery = CreateDiscovery(sonarr, radarr, prowlarr);

        var results = await Task.WhenAll(
            discovery.DiscoverAsync(ArrService.Sonarr),
            discovery.DiscoverAsync(ArrService.Radarr),
            discovery.DiscoverAsync(ArrService.Prowlarr));

        Assert.Equal("sonarr-secret", results[0].ApiKey);
        Assert.Equal(ArrService.Sonarr, results[0].Service);
        Assert.DoesNotContain("sonarr-secret", results[0].ToString(), StringComparison.Ordinal);
        Assert.Equal("radarr-secret", results[1].ApiKey);
        Assert.Equal("prowlarr-secret", results[2].ApiKey);
    }

    [Theory]
    [InlineData(ArrService.Sonarr)]
    [InlineData(ArrService.Radarr)]
    [InlineData(ArrService.Prowlarr)]
    public async Task ExtractsExactlyOneKeyFromRepresentativeArrConfigWithScalarSiblings(ArrService service)
    {
        var path = WriteRawConfig(service.ToString(), "<?xml version=\"1.0\"?>\n<Config>\n" +
            "  <BindAddress>0.0.0.0</BindAddress>\n" +
            "  <Port>8989</Port>\n" +
            "  <LogLevel><![CDATA[Info]]></LogLevel>\n" +
            "  <UrlBase>  </UrlBase>\n" +
            "  <ApiKey>  representative-secret  </ApiKey>\n" +
            "  <UpdateMechanism>Docker</UpdateMechanism>\n" +
            "  <LaunchBrowser>False</LaunchBrowser>\n" +
            "</Config>\n");
        var discovery = CreateDiscovery(
            service == ArrService.Sonarr ? path : Path.Combine(_root, "sonarr", "config.xml"),
            service == ArrService.Radarr ? path : Path.Combine(_root, "radarr", "config.xml"),
            service == ArrService.Prowlarr ? path : Path.Combine(_root, "prowlarr", "config.xml"));

        var result = await discovery.DiscoverAsync(service);

        Assert.Equal("representative-secret", result.ApiKey);
    }

    [Theory]
    [InlineData("utf8")]
    [InlineData("utf16le")]
    [InlineData("utf16be")]
    public async Task AcceptsSupportedByteOrderMarkedXml(string encoding)
    {
        var declaration = encoding == "utf8" ? "utf-8" : "utf-16";
        var xml = $"<?xml version=\"1.0\" encoding=\"{declaration}\"?><Config><Sibling>\u03bb</Sibling><ApiKey>encoded-secret</ApiKey></Config>";
        var bytes = encoding switch
        {
            "utf8" => Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray(),
            "utf16le" => Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray(),
            "utf16be" => Encoding.BigEndianUnicode.GetPreamble().Concat(Encoding.BigEndianUnicode.GetBytes(xml)).ToArray(),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        };
        var path = WriteRawBytes("sonarr", bytes);

        var result = await CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml")).DiscoverAsync(ArrService.Sonarr);

        Assert.Equal("encoded-secret", result.ApiKey);
    }

    [Theory]
    [InlineData(new byte[] { 0xEF, 0xBB, 0xBF, 0x3C, 0x43, 0x6F, 0x6E, 0x66 })]
    [InlineData(new byte[] { 0xFF, 0xFE, 0x3C, 0x00, 0x43 })]
    [InlineData(new byte[] { 0xFE, 0xFF, 0x00, 0x3C, 0x00, 0x43, 0xD8 })]
    public async Task RejectsMalformedByteSequencesWithoutLeakingSecrets(byte[] bytes)
    {
        var path = WriteRawBytes("sonarr", bytes.Concat(Encoding.UTF8.GetBytes("-do-not-leak")).ToArray());
        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")).DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.Malformed, exception.Failure);
        Assert.DoesNotContain("do-not-leak", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\" encoding=\"utf-16\"?><Config><ApiKey>mismatch-secret</ApiKey></Config>")]
    [InlineData("<?xml version=\"1.0\" encoding=\"x-unsupported\"?><Config><ApiKey>unsupported-secret</ApiKey></Config>")]
    public async Task RejectsDeclaredEncodingMismatchAndUnsupportedEncoding(string xml)
    {
        var path = WriteRawBytes("sonarr", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(xml)).ToArray());
        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")).DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.Malformed, exception.Failure);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsCharacterExpansionPastTheConfiguredByteBudget()
    {
        var xml = "<Config><ApiKey>" + new string('\u03bb', 100) + "</ApiKey></Config>";
        var path = WriteRawBytes("sonarr", Encoding.UTF8.GetBytes(xml));
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            MaxFileBytes = 80,
            TestOpenedFileFactory = OperatingSystem.IsWindows()
                ? _ => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null
        });

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() => discovery.DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.TooLarge, exception.Failure);
    }

    [Theory]
    [InlineData("<Config ordinary=\"no\"><ApiKey>secret</ApiKey></Config>")]
    [InlineData("<Config><Port ordinary=\"no\">8989</Port><ApiKey>secret</ApiKey></Config>")]
    [InlineData("<Config><ApiKey>secret</ApiKey><LogLevel ordinary=\"no\">Info</LogLevel></Config>")]
    public async Task RejectsOrdinaryAttributesOnEveryElement(string xml)
    {
        var path = WriteRawConfig("sonarr", xml);
        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml"))
                .DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.Malformed, exception.Failure);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoesNotSearchForConfigXmlOrUseAnUnconfiguredPath()
    {
        var configured = WriteConfig("sonarr", "configured-secret");
        WriteConfig("fallback", "fallback-secret");
        var discovery = CreateDiscovery(configured, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Radarr));

        Assert.Equal(ArrService.Radarr, exception.Service);
        Assert.Equal(ArrConfigDiscoveryFailure.Missing, exception.Failure);
        Assert.DoesNotContain("fallback-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("config.xml", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RequiresTheConfiguredPathToEndInTheExpectedFilename()
    {
        var path = Path.Combine(_root, "sonarr", "settings.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, Config("filename-secret"));

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
            {
                Paths = new ArrConfigDiscoveryPaths
                {
                    SonarrConfigPath = path,
                    RadarrConfigPath = Path.Combine(_root, "radarr", "config.xml"),
                    ProwlarrConfigPath = Path.Combine(_root, "prowlarr", "config.xml")
                }
            }).DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.InvalidPath, exception.Failure);
        Assert.Contains("Sonarr", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("filename-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ArrService.Sonarr)]
    [InlineData(ArrService.Radarr)]
    [InlineData(ArrService.Prowlarr)]
    public async Task MissingConfigProducesAServiceSpecificSanitizedError(ArrService service)
    {
        var discovery = CreateDiscovery(
            Path.Combine(_root, "sonarr", "config.xml"),
            Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(service));

        Assert.Equal(service, exception.Service);
        Assert.Equal(ArrConfigDiscoveryFailure.Missing, exception.Failure);
        Assert.Contains(service.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("config.xml", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectsMalformedXmlDtdAndAnEmptyApiKeyWithoutLeakingContents()
    {
        var key = "do-not-leak-this-key";
        var path = WriteRawConfig("sonarr", $"<!DOCTYPE Config [<!ENTITY key '{key}'>]><Config><ApiKey>&key;</ApiKey></Config>");
        var discovery = CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.Malformed, exception.Failure);
        Assert.DoesNotContain(key, exception.ToString(), StringComparison.Ordinal);

        path = WriteRawConfig("sonarr", "<Config><ApiKey>   </ApiKey></Config>");
        exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.EmptyApiKey, exception.Failure);
        Assert.DoesNotContain(key, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<Wrong><ApiKey>secret</ApiKey></Wrong>")]
    [InlineData("<Config version=\"1\"><ApiKey>secret</ApiKey></Config>")]
    [InlineData("<Config><ApiKey source=\"x\">secret</ApiKey></Config>")]
    [InlineData("<Config xmlns=\"urn:wrong\"><ApiKey>secret</ApiKey></Config>")]
    [InlineData("<Config><ApiKey>one</ApiKey><ApiKey>two</ApiKey></Config>")]
    [InlineData("<Config><ApiKey><Nested>secret</Nested></ApiKey></Config>")]
    [InlineData("<Config><Nested><ApiKey>secret</ApiKey></Nested></Config>")]
    public async Task RejectsXmlStructureAttributesNamespacesAndDuplicates(string xml)
    {
        var secret = "secret";
        var path = WriteRawConfig("sonarr", xml);
        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml"))
                .DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Malformed, exception.Failure);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AcceptsTheExactBoundButRejectsMaxPlusOne()
    {
        var path = WriteConfig("sonarr", "exact-bound-secret");
        var contents = await File.ReadAllBytesAsync(path);
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            MaxFileBytes = contents.Length,
            TestOpenedFileFactory = OperatingSystem.IsWindows()
                ? _ => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null
        });

        var result = await discovery.DiscoverAsync(ArrService.Sonarr);
        Assert.Equal("exact-bound-secret", result.ApiKey);

        await File.WriteAllBytesAsync(path, contents.Concat(new byte[] { 0 }).ToArray());
        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task RejectsFilesLargerThanTheConfiguredBound()
    {
        var path = WriteRawConfig("sonarr", "<Config><ApiKey>" + new string('x', 100) + "</ApiKey></Config>");
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths
            {
                SonarrConfigPath = path,
                RadarrConfigPath = Path.Combine(_root, "radarr", "config.xml"),
                ProwlarrConfigPath = Path.Combine(_root, "prowlarr", "config.xml")
            },
            MaxFileBytes = 32,
            TestOpenedFileFactory = OperatingSystem.IsWindows()
                ? _ => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null
        });

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));

        Assert.Equal(ArrConfigDiscoveryFailure.TooLarge, exception.Failure);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxFifoAndDirectoryAreRejectedWithoutBlocking()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The nonregular-file test requires Linux.");
        var fifo = Path.Combine(_root, "sonarr", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(fifo)!);
        Assert.Equal(0, MkFifo(fifo, 0x180));

        var discovery = CreateDiscovery(fifo, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));
        var stopwatch = Stopwatch.StartNew();
        var fifoException = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, fifoException.Failure);

        var directory = Path.Combine(_root, "radarr", "config.xml");
        Directory.CreateDirectory(directory);
        var directoryException = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Radarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, directoryException.Failure);
    }

    [Fact]
    public async Task LinuxCharacterAndBlockDevicesAreRejectedWithoutOpeningHandlers()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The special-file test requires Linux.");
        var directory = Path.Combine(_root, "sonarr");
        Directory.CreateDirectory(directory);
        var character = Path.Combine(directory, "config.xml");
        Assert.SkipWhen(MkNode(character, S_IFCHR | 0x180, LinuxDevice(1, 3)) != 0,
            "Creating a device node requires the test runner to have device-mount privileges.");

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(character, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")).DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, exception.Failure);

        // A block node is optional in containers. If it can be created, it is
        // checked through the same O_PATH-before-readable path.
        var blockDirectory = Path.Combine(_root, "radarr");
        Directory.CreateDirectory(blockDirectory);
        var block = Path.Combine(blockDirectory, "config.xml");
        if (MkNode(block, S_IFBLK | 0x180, LinuxDevice(7, 0)) == 0)
        {
            exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
                new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
                {
                    Paths = new ArrConfigDiscoveryPaths(Path.Combine(_root, "sonarr", "unused.xml"), block,
                        Path.Combine(_root, "prowlarr", "config.xml"))
                }).DiscoverAsync(ArrService.Radarr));
            Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, exception.Failure);
        }
    }

    [Fact]
    public async Task LinuxSynchronizedParentReplacementStillReadsTheHeldInsideDirectory()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The descriptor containment test requires Linux.");
        var path = WriteConfig("sonarr", "inside-secret");
        var parent = Path.GetDirectoryName(path)!;
        var outside = Path.Combine(_root, "outside-parent");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "config.xml"), Config("outside-secret"));
        var moved = Path.Combine(_root, "sonarr-original");
        var swapped = 0;
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            BeforeFinalOpen = () =>
            {
                Directory.Move(parent, moved);
                File.CreateSymbolicLink(parent, outside);
                swapped++;
            }
        });

        try
        {
            var result = await discovery.DiscoverAsync(ArrService.Sonarr);
            Assert.Equal("inside-secret", result.ApiKey);
            Assert.Equal(1, swapped);
        }
        finally
        {
            File.Delete(parent);
            Directory.Move(moved, parent);
        }
    }

    [Fact]
    public async Task LinuxFinalSymlinkIsRejectedWithoutReturningOutsideSecret()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The no-follow test requires Linux.");
        var outside = Path.Combine(_root, "outside.xml");
        await File.WriteAllTextAsync(outside, Config("outside-secret"));
        var path = Path.Combine(_root, "sonarr", "config.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, outside);

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml"))
                .DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, exception.Failure);
        Assert.DoesNotContain("outside-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxParentSymlinkIsRejectedWithoutReturningOutsideSecret()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The no-follow parent test requires Linux.");
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        await File.WriteAllTextAsync(Path.Combine(outside, "config.xml"), Config("outside-secret"));

        var bootstrap = Path.Combine(_root, "bootstrap");
        Directory.CreateDirectory(bootstrap);
        var service = Path.Combine(bootstrap, "sonarr");
        File.CreateSymbolicLink(service, outside);
        var path = Path.Combine(service, "config.xml");

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(() =>
            CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml"))
                .DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, exception.Failure);
        Assert.DoesNotContain("outside-secret", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LinuxNonDirectoryParentMapsToMissing()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The errno test requires Linux.");
        var parent = Path.Combine(_root, "not-a-directory");
        await File.WriteAllTextAsync(parent, "not a directory");
        var path = Path.Combine(parent, "config.xml");

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml"))
                .DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Missing, exception.Failure);
    }

    [Fact]
    public async Task LinuxConcurrentFinalPathSwapNeverReturnsOutsideSecret()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The no-follow test requires Linux.");
        var outside = Path.Combine(_root, "outside.xml");
        await File.WriteAllTextAsync(outside, Config("outside-secret"));
        var path = WriteConfig("sonarr", "inside-secret");
        var replacement = Path.Combine(_root, "sonarr", "replacement.xml");
        File.CreateSymbolicLink(replacement, outside);

        var swap = Task.Run(async () =>
        {
            for (var i = 0; i < 100; i++)
            {
                var temporary = Path.Combine(_root, "sonarr", $"swap-{i}.xml");
                await File.WriteAllTextAsync(temporary, Config("inside-secret"));
                File.Move(temporary, path, overwrite: true);
                File.Move(replacement, path, overwrite: true);
                File.CreateSymbolicLink(replacement, outside);
            }
        });

        var discovery = CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));
        var insideSuccesses = 0;
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var result = await discovery.DiscoverAsync(ArrService.Sonarr);
                Assert.Equal("inside-secret", result.ApiKey);
                insideSuccesses++;
            }
            catch (ArrConfigDiscoveryException exception)
            {
                Assert.DoesNotContain("outside-secret", exception.ToString(), StringComparison.Ordinal);
            }
        }

        await swap;
        Assert.True(insideSuccesses > 0, "the synchronized race must demonstrate an inside-key success");
    }

    [Fact]
    public async Task GrowthAfterInitialLengthObservationIsRejectedDeterministically()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The synchronized length test requires Linux.");
        var path = WriteConfig("sonarr", "bounded-secret");
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            MaxFileBytes = new FileInfo(path).Length,
            AfterInitialLengthObserved = () => File.AppendAllText(path, "x")
        });

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.TooLarge, exception.Failure);
    }

    [Fact]
    public async Task LinuxSynchronizedFinalReplacementIsRejectedThenInsideReadSucceeds()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The synchronized final replacement test requires Linux.");
        var path = WriteConfig("sonarr", "inside-secret");
        var outside = Path.Combine(_root, "outside.xml");
        await File.WriteAllTextAsync(outside, Config("outside-secret"));
        var replacement = Path.Combine(_root, "sonarr", "inside.xml");
        File.Move(path, replacement);
        var swapped = false;
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            BeforeFinalOpen = () =>
            {
                if (!swapped)
                {
                    File.CreateSymbolicLink(path, outside);
                    swapped = true;
                }
            }
        });

        var exception = await Assert.ThrowsAsync<ArrConfigDiscoveryException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr));
        Assert.Equal(ArrConfigDiscoveryFailure.Unreadable, exception.Failure);
        File.Delete(path);
        File.Move(replacement, path);
        var result = await discovery.DiscoverAsync(ArrService.Sonarr);
        Assert.Equal("inside-secret", result.ApiKey);
    }

    [Fact]
    public async Task CancellationAfterParsingStartsIsPropagated()
    {
        var path = WriteConfig("sonarr", "parse-cancel-secret");
        using var cancellation = new CancellationTokenSource();
        var discovery = new ArrApiKeyDiscovery(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths(path, Path.Combine(_root, "radarr", "config.xml"),
                Path.Combine(_root, "prowlarr", "config.xml")),
            ParseStarted = cancellation.Cancel,
            TestOpenedFileFactory = OperatingSystem.IsWindows()
                ? _ => new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr, cancellation.Token));
    }

    [Fact]
    public async Task CancellationIsPropagatedAndDiscoveryDoesNotWrite()
    {
        var path = WriteConfig("sonarr", "stable-secret");
        var before = await File.ReadAllBytesAsync(path);
        var discovery = CreateDiscovery(path, Path.Combine(_root, "radarr", "config.xml"),
            Path.Combine(_root, "prowlarr", "config.xml"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => discovery.DiscoverAsync(ArrService.Sonarr, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    private ArrApiKeyDiscovery CreateDiscovery(string sonarr, string radarr, string prowlarr) =>
        new(new ArrConfigDiscoveryOptions
        {
            Paths = new ArrConfigDiscoveryPaths
            {
                SonarrConfigPath = sonarr,
                RadarrConfigPath = radarr,
                ProwlarrConfigPath = prowlarr
            },
            // Windows production discovery is deliberately fail-closed. The
            // parser tests use an explicitly opened regular handle instead of
            // making the production implementation walk arbitrary paths.
            TestOpenedFileFactory = OperatingSystem.IsWindows()
                ? service => new FileStream(
                    service switch
                    {
                        ArrService.Sonarr => sonarr,
                        ArrService.Radarr => radarr,
                        ArrService.Prowlarr => prowlarr,
                        _ => throw new ArgumentOutOfRangeException(nameof(service))
                    }, FileMode.Open, FileAccess.Read, FileShare.Read)
                : null
        });

    private string WriteConfig(string service, string key) => WriteRawConfig(service, Config(key));

    private string WriteRawConfig(string service, string contents) =>
        WriteRawBytes(service, Encoding.UTF8.GetBytes(contents));

    private string WriteRawBytes(string service, byte[] contents)
    {
        var directory = Path.Combine(_root, service);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.xml");
        File.WriteAllBytes(path, contents);
        return path;
    }

    private static string Config(string key) => $"<?xml version=\"1.0\"?><Config><ApiKey>{key}</ApiKey></Config>";

    private const uint S_IFCHR = 0x2000;
    private const uint S_IFBLK = 0x6000;

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MkFifo(string path, uint mode);

    [DllImport("libc", EntryPoint = "mknod", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MkNode(string path, uint mode, ulong device);

    private static ulong LinuxDevice(uint major, uint minor) =>
        ((ulong)(major & 0xFFF) << 8) | (minor & 0xFF) | ((ulong)(minor & ~0xFF) << 12);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
