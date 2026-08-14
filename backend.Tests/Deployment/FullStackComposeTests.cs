using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace backend.Tests.Deployment;

public sealed class FullStackComposeTests
{
    private static readonly string[] Services = ["nzbdav", "jellyfin", "sonarr", "radarr", "prowlarr"];
    private static string RepoRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static string ComposePath => Path.Combine(RepoRoot, "docker-compose.full-stack.yml");
    private static string NvidiaPath => Path.Combine(RepoRoot, "docker-compose.nvidia.yml");

    [Fact]
    public void BaseCompose_IsTheFiveServiceCpuOnlyStackWithEgress()
    {
        using var compose = ComposeConfig(ComposePath);
        var root = compose.RootElement;
        var services = root.GetProperty("services");

        Assert.Equal(Services.OrderBy(x => x), services.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.Single(root.GetProperty("networks").EnumerateObject());
        Assert.Equal(
            ["completed_downloads", "jellyfin_cache", "jellyfin_config", "nzbdav_config", "nzbdav_media", "prowlarr_config", "radarr_config", "sonarr_config"],
            root.GetProperty("volumes").EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        var network = root.GetProperty("networks").GetProperty("full_stack");
        Assert.Equal("bridge", network.GetProperty("driver").GetString());
        Assert.True(!network.TryGetProperty("internal", out var internalNetwork) || !internalNetwork.GetBoolean());
        Assert.DoesNotContain("privileged", root.GetRawText(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/var/run/docker.sock", root.GetRawText(), StringComparison.Ordinal);
        Assert.All(services.EnumerateObject(), service => Assert.False(service.Value.TryGetProperty("gpus", out _)));
        Assert.Equal(["ALL"], services.GetProperty("nzbdav").GetProperty("cap_drop").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["ALL"], services.GetProperty("jellyfin").GetProperty("cap_drop").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(["CHOWN", "DAC_OVERRIDE", "SETUID", "SETGID"], services.GetProperty("jellyfin").GetProperty("cap_add").EnumerateArray().Select(x => x.GetString()));
        Assert.All(services.EnumerateObject().Where(x => x.Name is "sonarr" or "radarr" or "prowlarr"), service =>
            Assert.Equal(["NET_RAW"], service.Value.GetProperty("cap_drop").EnumerateArray().Select(x => x.GetString())));
    }

    // Descriptors scale with (concurrent streams x article buffer) because every
    // buffered segment is an open file in the L1 cache. Docker's 1024 default was
    // exhausted by ~20 concurrent 4K streams, which surfaced as reads aborting
    // mid-response with "No file descriptors available" - a truncated file to the
    // client, since the response had already started.
    [Fact]
    public void NzbdavRaisesTheFileDescriptorLimitForConcurrentStreaming()
    {
        using var compose = ComposeConfig(ComposePath);
        var nofile = compose.RootElement
            .GetProperty("services")
            .GetProperty("nzbdav")
            .GetProperty("ulimits")
            .GetProperty("nofile");

        Assert.True(nofile.GetProperty("soft").GetInt32() >= 65536);
        Assert.True(nofile.GetProperty("hard").GetInt32() >= 65536);
    }

    // The L2 tier is a host-managed mount, so it is wired as a bind mount rather
    // than a named volume. Both halves must stay in place: without the mount the
    // path resolves to container-local disk, and without the env var the backend
    // silently stays off.
    [Fact]
    public void NzbdavBindsTheL2CacheMountAndItsPathTogether()
    {
        using var compose = ComposeConfig(ComposePath);
        var nzbdav = compose.RootElement.GetProperty("services").GetProperty("nzbdav");

        // `docker compose config` normalizes volumes to long form, so each entry
        // is an object with source/target rather than a "src:dst" string.
        var l2Mounts = nzbdav.GetProperty("volumes")
            .EnumerateArray()
            .Select(v => v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : v.TryGetProperty("target", out var target) ? target.GetString() ?? string.Empty : string.Empty)
            .Where(v => v == "/l2" || v.EndsWith(":/l2", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            l2Mounts.Length == 1,
            $"expected exactly one /l2 mount, found {l2Mounts.Length}");
        Assert.True(
            nzbdav.GetProperty("environment").TryGetProperty("NZBDAV_L2_PATH", out _),
            "NZBDAV_L2_PATH must be declared, otherwise the mount is inert");
    }

    // The L1 cache size was previously only reachable by hand-editing a row in
    // the runtime database, so a rebuilt stack silently reverted to the 10 GB
    // application default. Passing it through compose keeps the deployed cache
    // size reproducible from the committed .env.
    [Fact]
    public void NzbdavPassesTheCacheSizeThroughFromTheEnvironment()
    {
        using var compose = ComposeConfig(ComposePath);
        var environment = compose.RootElement
            .GetProperty("services")
            .GetProperty("nzbdav")
            .GetProperty("environment");

        Assert.True(
            environment.TryGetProperty("NZBDAV_CACHE_MAX_SIZE_GB", out var cacheSize),
            "compose must pass NZBDAV_CACHE_MAX_SIZE_GB so .env controls the cache size");
        Assert.True(int.TryParse(cacheSize.GetString(), out var gigabytes) && gigabytes > 0);
    }

    [Fact]
    public void NvidiaOverride_AddsGpuOnlyToJellyfin()
    {
        using var compose = ComposeConfig(ComposePath, NvidiaPath);
        var services = compose.RootElement.GetProperty("services");

        Assert.Equal(Services.OrderBy(x => x), services.EnumerateObject().Select(x => x.Name).OrderBy(x => x));
        Assert.All(services.EnumerateObject().Where(x => x.Name != "jellyfin"), service => Assert.False(service.Value.TryGetProperty("gpus", out _)));
        var jellyfinEnvironment = services.GetProperty("jellyfin").GetProperty("environment");
        Assert.Equal("compute,video,utility", jellyfinEnvironment.GetProperty("NVIDIA_DRIVER_CAPABILITIES").GetString());
        var gpus = services.GetProperty("jellyfin").GetProperty("gpus");
        Assert.Single(gpus.EnumerateArray());
        Assert.Equal(-1, gpus[0].GetProperty("count").GetInt32());
    }

    [Fact]
    public void Services_ArePinnedAndFreshBuildsArePossible()
    {
        using var compose = ComposeConfig(ComposePath);
        var services = compose.RootElement.GetProperty("services");

        Assert.Equal("linuxserver/sonarr:4.0.19.2979-ls321@sha256:373159ba768e23a3a1c497d9f2b936addf8fd5b1fdce7dd6a14080ac928bfda0", services.GetProperty("sonarr").GetProperty("image").GetString());
        Assert.Equal("linuxserver/radarr:6.3.0.10514-ls313@sha256:a45b5ab0f850f39edb4cc9c95bbd967b52ddc3d4574a4dfb45561177db6c88f4", services.GetProperty("radarr").GetProperty("image").GetString());
        Assert.Equal("linuxserver/prowlarr:2.5.2.5491-ls156@sha256:1295cff29d10b486c0d8324d1559a552140a5932bf8b3d87e398654414f63f92", services.GetProperty("prowlarr").GetProperty("image").GetString());
        Assert.Equal("nzbdav:0.6.4-full-stack", services.GetProperty("nzbdav").GetProperty("image").GetString());
        Assert.Equal("nzbdav-jellyfin:10.11.8", services.GetProperty("jellyfin").GetProperty("image").GetString());

        Assert.Equal("Dockerfile", services.GetProperty("nzbdav").GetProperty("build").GetProperty("dockerfile").GetString());
        Assert.Equal("jellyfin-stack/Dockerfile", services.GetProperty("jellyfin").GetProperty("build").GetProperty("dockerfile").GetString());
        Assert.All(services.EnumerateObject(), service => Assert.Equal("unless-stopped", service.Value.GetProperty("restart").GetString()));
        Assert.All(services.EnumerateObject(), service => Assert.True(service.Value.TryGetProperty("healthcheck", out _)));

        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot, "jellyfin-stack", "Dockerfile"));
        Assert.Contains("FROM jellyfin/jellyfin:10.11.8@sha256:1694ff069f0c9dafb283c36765175606866769f5d72f2ed56b6a0f1be922fc37", dockerfile, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"FROM mcr\.microsoft\.com/dotnet/sdk:9\.0\.308-alpine3\.22@sha256:[0-9a-f]{64}"), dockerfile);
        Assert.DoesNotContain("10.11.*", dockerfile, StringComparison.Ordinal);

        var jellyfinEntrypoint = File.ReadAllText(Path.Combine(RepoRoot, "jellyfin-stack", "entrypoint.sh"));
        Assert.DoesNotContain("cap_add:\n      - FOWNER", File.ReadAllText(ComposePath), StringComparison.Ordinal);
        Assert.Contains("chown 0:0 \"$plugins_dir\" \"$plugin_dir\"", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.Contains("chmod 0755 \"$plugins_dir\" \"$plugin_dir\"", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.Contains("chown \"$puid:$pgid\" \"$plugins_dir\" \"$plugin_dir\"", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.Contains("$5 == target", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("prefix=\"$volume/\"", jellyfinEntrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void JellyfinEntrypoint_SeedsTheManagedLibraryCategoryRootsBeforeDroppingPrivileges()
    {
        var jellyfinEntrypoint = File.ReadAllText(Path.Combine(RepoRoot, "jellyfin-stack", "entrypoint.sh"));

        Assert.Contains("/media/nzbdav /media/nzbdav/movies /media/nzbdav/tv", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.True(
            jellyfinEntrypoint.IndexOf("mkdir -p -- \"$path\"", StringComparison.Ordinal)
            < jellyfinEntrypoint.IndexOf("initialize_volume /media/nzbdav", StringComparison.Ordinal));
    }

    [Fact]
    public void JellyfinEntrypoint_ReclaimsCategoryRootOwnershipAfterTheOneTimeVolumeScan()
    {
        // initialize_volume early-returns once its marker matches, so the roots
        // NZBDAV seeds as root on a later start would otherwise stay root-owned
        // and the plugin's library writes fail with errno=13.
        var jellyfinEntrypoint = File.ReadAllText(Path.Combine(RepoRoot, "jellyfin-stack", "entrypoint.sh"));

        Assert.Contains("for path in /media/nzbdav/movies /media/nzbdav/tv; do", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.Contains("chown \"$puid:$pgid\" -- \"$path\"", jellyfinEntrypoint, StringComparison.Ordinal);
        Assert.True(
            jellyfinEntrypoint.IndexOf("initialize_volume /media/nzbdav", StringComparison.Ordinal)
            < jellyfinEntrypoint.IndexOf("for path in /media/nzbdav/movies /media/nzbdav/tv; do", StringComparison.Ordinal));
    }

    [Fact]
    public void DeploymentSmoke_NoRestageProofUsesInodeAndPayloadHashNotJellyfinMetadataMtime()
    {
        var smoke = File.ReadAllText(Path.Combine(RepoRoot, "tools", "full-stack-deployment-smoke.sh"));

        // Jellyfin updates its own parsed meta.json timestamp during normal startup.
        // The smoke already verifies all four payload hashes before/after restart;
        // its no-restage fingerprint must therefore track the inode and size, not mtime.
        Assert.DoesNotContain("stat -c \\\"%i:%Y:%s\\\"", smoke, StringComparison.Ordinal);
        const string portableFingerprint = "stat -c \\\"%i:%s\\\" /config/plugins/Nzbdav/";
        Assert.Equal(2, Regex.Matches(smoke, Regex.Escape(portableFingerprint)).Count);
    }

    [Fact]
    public void Bootstrap_UsesRootBoundaryConfigPathExactInternalUrlsAndReadOnlyServarrConfigMounts()
    {
        using var compose = ComposeConfig(ComposePath);
        var nzbdav = compose.RootElement.GetProperty("services").GetProperty("nzbdav");
        var environment = nzbdav.GetProperty("environment");
        Assert.Equal("/config/data", environment.GetProperty("CONFIG_PATH").GetString());
        Assert.Equal("", environment.GetProperty("NZBDAV_MASTER_KEY").GetString());
        Assert.Equal("1001", environment.GetProperty("FRONTEND_PUID").GetString());
        var configMount = Mounts(nzbdav).Single(x => x.GetProperty("source").GetString() == "nzbdav_config");
        Assert.Equal("/config", configMount.GetProperty("target").GetString());
        Assert.Equal("http://localhost:8080", environment.GetProperty("BACKEND_URL").GetString());
        Assert.Equal("http://nzbdav:8080", environment.GetProperty("SETUP_NZBDAV_URL").GetString());
        Assert.Equal("http://jellyfin:8096", environment.GetProperty("SETUP_JELLYFIN_URL").GetString());
        Assert.Equal("http://sonarr:8989", environment.GetProperty("SETUP_SONARR_URL").GetString());
        Assert.Equal("http://radarr:7878", environment.GetProperty("SETUP_RADARR_URL").GetString());
        Assert.Equal("http://prowlarr:9696", environment.GetProperty("SETUP_PROWLARR_URL").GetString());
        Assert.Equal("true", environment.GetProperty("NZBDAV_FULL_STACK").GetString());

        var mounts = Mounts(nzbdav);
        foreach (var app in new[] { "sonarr", "radarr", "prowlarr" })
        {
            var mount = mounts.Single(x => x.GetProperty("target").GetString() == $"/bootstrap/{app}");
            Assert.True(mount.GetProperty("read_only").GetBoolean());
        }
    }

    [Fact]
    public void MasterKeyOverride_ForwardsConfiguredValueWithoutPrintingItAndBlankIsEmpty()
    {
        const string configuredKey = "VwJb7F5zWKx30P0yOsJJ1+ZvSEEhN9wJNq+2+PTnsOg=";
        using var blank = ComposeConfigWithMasterKey("", ComposePath);
        using var configured = ComposeConfigWithMasterKey(configuredKey, ComposePath);

        Assert.Equal("", blank.RootElement.GetProperty("services").GetProperty("nzbdav")
            .GetProperty("environment").GetProperty("NZBDAV_MASTER_KEY").GetString());
        Assert.True(configured.RootElement.GetProperty("services").GetProperty("nzbdav")
            .GetProperty("environment").GetProperty("NZBDAV_MASTER_KEY").GetString() == configuredKey);

        var compose = File.ReadAllText(ComposePath);
        var entrypoint = File.ReadAllText(Path.Combine(RepoRoot, "entrypoint.sh"));
        Assert.Contains("NZBDAV_MASTER_KEY: \"${NZBDAV_MASTER_KEY:-}\"", compose, StringComparison.Ordinal);
        Assert.Contains("clear_blank_master_key_override", entrypoint, StringComparison.Ordinal);
        Assert.DoesNotContain("echo \"$NZBDAV_MASTER_KEY", entrypoint, StringComparison.Ordinal);
    }

    [Fact]
    public void HostPorts_AreParameterisedWithLANFriendlyDefaults()
    {
        var compose = File.ReadAllText(ComposePath);
        foreach (var (variable, port, containerPort) in new[]
        {
            ("NZBDAV_PORT", 3000, 3000),
            ("JELLYFIN_PORT", 8096, 8096),
            ("SONARR_PORT", 8989, 8989),
            ("RADARR_PORT", 7878, 7878),
            ("PROWLARR_PORT", 9696, 9696),
        })
        {
            Assert.Contains("\"${BIND_ADDRESS:-127.0.0.1}:${" + variable + ":-" + port + "}:" + containerPort + "\"", compose, StringComparison.Ordinal);
        }

        var expectedHostPorts = new[] { 3000, 8096, 8989, 7878, 9696 };
        Assert.Equal(expectedHostPorts.Length, expectedHostPorts.Distinct().Count());
        using var normalized = ComposeConfig(ComposePath);
        var services = normalized.RootElement.GetProperty("services");
        Assert.Equal("3000", services.GetProperty("nzbdav").GetProperty("environment").GetProperty("PORT").GetString());
        var healthChecks = new Dictionary<string, string>
        {
            ["nzbdav"] = "curl -fsS http://127.0.0.1:3000/ >/dev/null && curl -fsS http://127.0.0.1:8080/health >/dev/null",
            ["jellyfin"] = "curl -fsS http://127.0.0.1:8096/health || exit 1",
            ["sonarr"] = "curl -fsS http://127.0.0.1:8989/ping || exit 1",
            ["radarr"] = "curl -fsS http://127.0.0.1:7878/ping || exit 1",
            ["prowlarr"] = "curl -fsS http://127.0.0.1:9696/ping || exit 1",
        };
        foreach (var (service, command) in healthChecks)
        {
            var test = services.GetProperty(service).GetProperty("healthcheck").GetProperty("test");
            Assert.Equal(["CMD-SHELL", command], test.EnumerateArray().Select(x => x.GetString()));
        }

        var redirectDocs = File.ReadAllText(Path.Combine(RepoRoot, "docs", "full-stack-deployment.md"));
        var docs = File.ReadAllText(Path.Combine(RepoRoot, "docs", "all-in-one.md"));
        Assert.Contains("BIND_ADDRESS=127.0.0.1", redirectDocs, StringComparison.Ordinal);
        Assert.Contains("Read `docs/all-in-one.md`", redirectDocs, StringComparison.Ordinal);
        Assert.Contains("Standalone master-key rotation", docs, StringComparison.Ordinal);
        Assert.Contains("unset NZBDAV_MASTER_KEY", docs, StringComparison.Ordinal);
        Assert.Contains("nzbdav-master-key.pending", docs, StringComparison.Ordinal);
        Assert.Contains("retain the old config and the pending key", docs, StringComparison.Ordinal);
        Assert.DoesNotContain("FULL_STACK_BIND_ADDRESS", compose, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_RestoresEveryProjectBeforeNoRestoreAndCleansObj()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "docker-publish.yml"));
        var clean = workflow.IndexOf("find backend backend.Tests jellyfin-plugin jellyfin-plugin.Tests", StringComparison.Ordinal);
        var restore = workflow.IndexOf("dotnet restore backend/NzbWebDAV.csproj --locked-mode", StringComparison.Ordinal);
        var backendTestsRestore = workflow.IndexOf("dotnet restore backend.Tests/backend.Tests.csproj --locked-mode", StringComparison.Ordinal);
        var pluginRestore = workflow.IndexOf("dotnet restore jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj --locked-mode", StringComparison.Ordinal);
        var pluginTestsRestore = workflow.IndexOf("dotnet restore jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --locked-mode", StringComparison.Ordinal);
        var noRestore = workflow.IndexOf("dotnet test backend.Tests/backend.Tests.csproj --configuration Release --no-restore", StringComparison.Ordinal);
        Assert.True(clean >= 0 && clean < restore);
        Assert.True(restore < backendTestsRestore && backendTestsRestore < pluginRestore && pluginRestore < pluginTestsRestore && pluginTestsRestore < noRestore);
        Assert.Contains("dotnet build jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj --configuration Release --no-restore", workflow, StringComparison.Ordinal);
        Assert.Contains("dotnet test jellyfin-plugin.Tests/jellyfin-plugin.Tests.csproj --configuration Release --no-restore", workflow, StringComparison.Ordinal);
        Assert.Contains("bash tools/full-stack-deployment-smoke.tests.sh", workflow, StringComparison.Ordinal);
        var onboardingTestsStep = workflow.IndexOf("- name: Run browser onboarding smoke harness contract tests", StringComparison.Ordinal);
        var onboardingTestsEnd = workflow.IndexOf("\n      - name:", onboardingTestsStep + 1, StringComparison.Ordinal);
        Assert.True(onboardingTestsStep >= 0 && onboardingTestsEnd > onboardingTestsStep,
            "The publish workflow must run the focused public-onboarding harness contract tests as a dedicated gate.");
        var onboardingTests = workflow[onboardingTestsStep..onboardingTestsEnd];
        Assert.Contains("python3 tools/test-full-stack-onboarding-smoke.py", onboardingTests, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 10", onboardingTests, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", onboardingTests, StringComparison.OrdinalIgnoreCase);

        var onboardingRealStep = workflow.IndexOf("- name: Run real browser onboarding smoke before publish", StringComparison.Ordinal);
        var onboardingRealEnd = workflow.IndexOf("\n      - name:", onboardingRealStep + 1, StringComparison.Ordinal);
        Assert.True(onboardingRealStep > onboardingTestsStep && onboardingRealEnd > onboardingRealStep,
            "The real public browser onboarding smoke must be a dedicated gate after its contract tests.");
        var onboardingReal = workflow[onboardingRealStep..onboardingRealEnd];
        Assert.Contains("python3 tools/full-stack-onboarding-smoke.py", onboardingReal, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 20", onboardingReal, StringComparison.Ordinal);
        Assert.Contains("TMPDIR=\"$RUNNER_TEMP\"", onboardingReal, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error", onboardingReal, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("|| true", onboardingReal, StringComparison.Ordinal);
        Assert.DoesNotContain("if:", onboardingReal, StringComparison.OrdinalIgnoreCase);

        var onboardingEvidenceStep = workflow.IndexOf("- name: Upload browser onboarding smoke evidence on failure", StringComparison.Ordinal);
        var onboardingEvidenceEnd = workflow.IndexOf("\n      - name:", onboardingEvidenceStep + 1, StringComparison.Ordinal);
        Assert.True(onboardingEvidenceStep > onboardingRealStep && onboardingEvidenceEnd > onboardingEvidenceStep,
            "Failure-only upload must follow the real browser smoke gate.");
        var onboardingEvidence = workflow[onboardingEvidenceStep..onboardingEvidenceEnd];
        Assert.Contains("if: failure()", onboardingEvidence, StringComparison.Ordinal);
        Assert.Contains("nzbdav-onboarding-e2e-*/evidence.json", onboardingEvidence, StringComparison.Ordinal);
        Assert.Contains("full-stack-onboarding-smoke.log", onboardingEvidence, StringComparison.Ordinal);

        var realSmokeStep = workflow.IndexOf("- name: Run genuine full-stack deployment smoke before publish", StringComparison.Ordinal);
        var realSmokeCommand = realSmokeStep >= 0
            ? workflow.IndexOf("bash tools/full-stack-deployment-smoke.sh --real", realSmokeStep, StringComparison.Ordinal)
            : -1;
        Assert.True(realSmokeStep > onboardingEvidenceStep && realSmokeCommand > realSmokeStep,
            "Both onboarding gates must run before the supervised real deployment smoke.");
        Assert.DoesNotContain("timeout --foreground", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("timeout --signal=TERM", workflow, StringComparison.Ordinal);
        Assert.Contains("python3 tools/full-stack-deployment-supervisor.py", workflow, StringComparison.Ordinal);
        Assert.Contains("--deadline 40m --grace 60s", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 45", workflow, StringComparison.Ordinal);
        var realSmokeEnd = workflow.IndexOf("\n      - name:", realSmokeStep + 1, StringComparison.Ordinal);
        Assert.True(realSmokeEnd > realSmokeStep, "The real smoke must remain a dedicated bounded gate.");
        var realSmoke = workflow[realSmokeStep..realSmokeEnd];
        Assert.DoesNotContain("continue-on-error", realSmoke, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("|| true", realSmoke, StringComparison.Ordinal);
        var build = workflow.IndexOf("- name: Build and run all .NET tests without restore", StringComparison.Ordinal);
        var publish = workflow.IndexOf("- name: Build and push Docker image (amd64 + arm64)", StringComparison.Ordinal);
        Assert.True(realSmokeStep < build && build < publish,
            "Both real pre-publish smoke gates must run before build and publish.");
        var supervisor = File.ReadAllText(Path.Combine(RepoRoot, "tools", "full-stack-deployment-supervisor.py"));
        Assert.Contains("start_new_session=True", supervisor, StringComparison.Ordinal);
        Assert.Contains("os.killpg", supervisor, StringComparison.Ordinal);
        Assert.Contains("signal.SIGKILL", supervisor, StringComparison.Ordinal);
        Assert.Contains("start_time", supervisor, StringComparison.Ordinal);
        Assert.Contains("session", supervisor, StringComparison.Ordinal);
        Assert.Contains("pgid", supervisor, StringComparison.Ordinal);
        Assert.Contains("tools/tests/nuget-vulnerabilities-top-level.json", workflow, StringComparison.Ordinal);
        Assert.Contains("tools/tests/nuget-vulnerabilities-transitive.json", workflow, StringComparison.Ordinal);
        var auditStep = workflow.IndexOf("- name: Audit vulnerable and deprecated packages", StringComparison.Ordinal);
        Assert.True(auditStep >= 0, "The workflow must retain the package audit gate.");
        var audit = workflow[auditStep..];
        Assert.Contains("bash tools/tests/check-nuget-vulnerabilities.tests.sh", audit, StringComparison.Ordinal);
        Assert.Contains("bash tools/tests/check-nuget-deprecated.tests.sh", audit, StringComparison.Ordinal);
        var vulnerableReports = new[] { "backend.json", "backend-tests.json", "plugin.json", "plugin-tests.json" };
        foreach (var report in vulnerableReports)
        {
            Assert.Contains("\"$audit_dir/" + report + "\"", audit, StringComparison.Ordinal);
            Assert.Contains("\"$deprecated_dir/" + report + "\"", audit, StringComparison.Ordinal);
        }
        var vulnerableParserStart = audit.IndexOf("python3 tools/check-nuget-vulnerabilities.py", StringComparison.Ordinal);
        var deprecatedParserStart = audit.IndexOf("python3 tools/check-nuget-deprecated.py", StringComparison.Ordinal);
        Assert.True(vulnerableParserStart >= 0 && deprecatedParserStart > vulnerableParserStart);
        var vulnerableParser = audit[vulnerableParserStart..deprecatedParserStart];
        var deprecatedParser = audit[deprecatedParserStart..];
        Assert.All(vulnerableReports, report => Assert.Contains("\"$audit_dir/" + report + "\"", vulnerableParser, StringComparison.Ordinal));
        Assert.All(vulnerableReports, report => Assert.Contains("\"$deprecated_dir/" + report + "\"", deprecatedParser, StringComparison.Ordinal));
        Assert.DoesNotContain("$audit_dir/*.json", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("$deprecated_dir/*.json", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("python3 tools/check-nuget-vulnerabilities.py \"$audit_dir\"", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("python3 tools/check-nuget-deprecated.py \"$deprecated_dir\"", audit, StringComparison.Ordinal);
        var deprecatedTests = File.ReadAllText(Path.Combine(RepoRoot, "tools", "tests", "check-nuget-deprecated.tests.sh"));
        Assert.Contains("PARSER=\"$ROOT/tools/check-nuget-deprecated.py\"", deprecatedTests, StringComparison.Ordinal);
        Assert.Contains("\"$@\" >/dev/null 2>&1", deprecatedTests, StringComparison.Ordinal);
        Assert.Contains("if [[ $actual -ne $expected ]]; then", deprecatedTests, StringComparison.Ordinal);
        Assert.Contains("nuget-deprecated-safe.json", deprecatedTests, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"expect_exit\s+0\s+python3\s+\""\$PARSER\""\s+\""\$ROOT/tools/tests/nuget-deprecated-safe\.json\"""), deprecatedTests);

        var genuineFixtureLoop = deprecatedTests.IndexOf("for fixture in", StringComparison.Ordinal);
        var malformedFixtureLoop = deprecatedTests.IndexOf("for fixture in", genuineFixtureLoop + 1, StringComparison.Ordinal);
        Assert.True(genuineFixtureLoop >= 0 && malformedFixtureLoop > genuineFixtureLoop);
        var genuineFixtures = deprecatedTests[genuineFixtureLoop..malformedFixtureLoop];
        Assert.All(new[]
        {
            "nuget-deprecated-top-level.json",
            "nuget-deprecated-transitive.json",
            "nuget-deprecated-sdk-top-level-requested-version.json"
        }, fixture => Assert.Contains(fixture, genuineFixtures, StringComparison.Ordinal));
        Assert.Matches(new Regex(@"expect_exit\s+1\s+python3\s+\""\$PARSER\""\s+\""\$ROOT/tools/tests/\$fixture\"""), genuineFixtures);

        var malformedFixtures = deprecatedTests[malformedFixtureLoop..];
        Assert.All(new[]
        {
            "nuget-malformed-framework.json",
            "nuget-deprecated-top-level-requested-version-empty.json"
        }, fixture => Assert.Contains(fixture, malformedFixtures, StringComparison.Ordinal));
        Assert.Matches(new Regex(@"expect_exit\s+2\s+python3\s+\""\$PARSER\""\s+\""\$ROOT/tools/tests/\$fixture\"""), malformedFixtures);
        var pluginTestsProject = File.ReadAllText(Path.Combine(RepoRoot, "jellyfin-plugin.Tests", "jellyfin-plugin.Tests.csproj"));
        Assert.DoesNotContain("10.11.*", pluginTestsProject, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_RunsBoundedLinuxBackupValidatorAndEightVolumeRoundTrip()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "docker-publish.yml"));
        var stepStart = workflow.IndexOf("- name: Run full-stack volume backup validator and round-trip gate", StringComparison.Ordinal);
        var stepEnd = workflow.IndexOf("\n      - name:", stepStart + 1, StringComparison.Ordinal);
        Assert.True(stepStart >= 0 && stepEnd > stepStart, "The publish workflow must run the backup gate as a dedicated step.");
        var step = workflow[stepStart..stepEnd];
        Assert.Contains("timeout 10m bash docs/support/full-stack-volume-backup.tests.sh", step, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes: 15", step, StringComparison.Ordinal);
        Assert.Contains("CI: true", step, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", step, StringComparison.Ordinal);

        var helper = File.ReadAllText(Path.Combine(RepoRoot, "docs", "support", "full-stack-volume-backup.sh"));
        foreach (var key in new[]
        {
            "nzbdav_config", "nzbdav_media", "completed_downloads", "jellyfin_config",
            "jellyfin_cache", "sonarr_config", "radarr_config", "prowlarr_config"
        })
        {
            Assert.Contains("  " + key, helper, StringComparison.Ordinal);
        }
        Assert.Contains("[[ ${#PROJECT_VOLUMES[@]} -eq 8 ]]", helper, StringComparison.Ordinal);
        Assert.Contains("kernel=$(uname -s 2>/dev/null || true)", helper, StringComparison.Ordinal);
        Assert.Contains("[[ \"$kernel\" == Linux ]] && return 0", helper, StringComparison.Ordinal);
        Assert.Contains("require_linux_host", helper, StringComparison.Ordinal);
        Assert.Contains("Unsupported platform: backup/restore requires a Linux server or WSL", helper, StringComparison.Ordinal);

        var backupTests = File.ReadAllText(Path.Combine(RepoRoot, "docs", "support", "full-stack-volume-backup.tests.sh"));
        Assert.Contains("fake Darwin host was accepted", backupTests, StringComparison.Ordinal);
        Assert.Contains("! -e \"$platform_fixture/docker-called\"", backupTests, StringComparison.Ordinal);
        Assert.Contains("production Linux validator: SKIP (MSYS awk)", backupTests, StringComparison.Ordinal);
        Assert.Contains("docker volume rm \"${volumes[@]}\"", backupTests, StringComparison.Ordinal);
        Assert.Contains("Docker named-volume completed-downloads round-trip: PASS", backupTests, StringComparison.Ordinal);
        Assert.Contains("real-validator matrix: PASS", backupTests, StringComparison.Ordinal);
    }

    [Fact]
    public void Workflow_AlpineDeploymentInvocationPlacesDockerOptionsBeforeImage()
    {
        var workflow = File.ReadAllText(Path.Combine(RepoRoot, ".github", "workflows", "docker-publish.yml"));
        var stepStart = workflow.IndexOf("- name: Run real Alpine deployment shell suites", StringComparison.Ordinal);
        var runStart = workflow.IndexOf("docker run ", stepStart, StringComparison.Ordinal);
        var runEnd = workflow.IndexOf("\n            '\n", runStart, StringComparison.Ordinal);
        Assert.True(stepStart >= 0 && runStart > stepStart && runEnd > runStart);

        var invocation = workflow.Substring(runStart, runEnd - runStart);
        var flattened = invocation.Replace("\\\r\n", " ", StringComparison.Ordinal).Replace("\\\n", " ", StringComparison.Ordinal);
        var image = Regex.Match(flattened, @"(?<!\S)alpine:3\.22@sha256:[0-9a-f]{64}(?!\S)");
        Assert.True(image.Success, "The deployment step must run a pinned Alpine image.");
        var shell = flattened.IndexOf(" /bin/sh ", image.Index + image.Length, StringComparison.Ordinal);
        Assert.True(shell > image.Index, "The Alpine image must precede the container command.");
        var optionsBeforeImage = flattened.Substring(0, image.Index);
        Assert.Contains("--cap-add=SYS_ADMIN", optionsBeforeImage, StringComparison.Ordinal);
        Assert.DoesNotContain("--privileged", optionsBeforeImage, StringComparison.Ordinal);

        var dockerOptionsAfterImage = Regex.Match(
            flattened.Substring(image.Index + image.Length, shell - image.Index - image.Length),
            @"(?<!\S)(?:--[A-Za-z0-9-]+|-[A-Za-z])(?=\s)");
        Assert.False(dockerOptionsAfterImage.Success,
            "Docker options must be before the image in the extracted workflow command.");
        Assert.True(flattened.IndexOf(" -e CI=true ", StringComparison.Ordinal) < image.Index,
            "CI=true must be a Docker environment option before the image.");
        Assert.Contains("apk add --no-cache bash coreutils", invocation, StringComparison.Ordinal);
        Assert.Contains("command -v timeout", invocation, StringComparison.Ordinal);
        Assert.Contains("/workspace/tools/full-stack-deployment-smoke.tests.sh", invocation, StringComparison.Ordinal);
        Assert.Contains("/workspace/tools/full-stack-deployment-supervisor.py", invocation, StringComparison.Ordinal);
    }

    [Fact]
    public void RootBoundary_IsExercisedByNumericUserSmokeProbes()
    {
        var smoke = File.ReadAllText(Path.Combine(RepoRoot, "tools", "full-stack-deployment-smoke.sh"));
        Assert.Contains("assert env[\"CONFIG_PATH\"] == \"/config/data\"", smoke, StringComparison.Ordinal);
        Assert.Contains("probe_write", smoke, StringComparison.Ordinal);
        Assert.Contains("test ! -w /config", smoke, StringComparison.Ordinal);
        Assert.Contains("/config/bootstrap-secrets", smoke, StringComparison.Ordinal);
        Assert.Contains("FRONTEND_PUID", smoke, StringComparison.Ordinal);
        Assert.Contains("restart jellyfin nzbdav", smoke, StringComparison.Ordinal);
        Assert.Contains("assert_backend_api_contract", smoke, StringComparison.Ordinal);
        Assert.Contains("node dist-node/server.js", smoke, StringComparison.Ordinal);
        Assert.Contains("CapEff:", smoke, StringComparison.Ordinal);
        Assert.Contains("--header 'Cookie:'", smoke, StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080/api?mode=version", smoke, StringComparison.Ordinal);
        Assert.Contains("http://localhost:8080/api/manifest", smoke, StringComparison.Ordinal);
        Assert.Contains("probe_cross_container_sentinel jellyfin /media/nzbdav nzbdav /media/nzbdav .deployment-smoke-sentinel", smoke, StringComparison.Ordinal);
        Assert.Contains("persistent_sentinel_write nzbdav /config/data", smoke, StringComparison.Ordinal);
        Assert.Contains("persistent_sentinel_verify nzbdav /config/data", smoke, StringComparison.Ordinal);
        Assert.Contains("restart sentinel did not preserve exact bytes and metadata", smoke, StringComparison.Ordinal);
        Assert.Contains("--persistent-sentinel", smoke, StringComparison.Ordinal);
        Assert.Contains("same device,", smoke, StringComparison.Ordinal);
        Assert.Contains("inode, and", smoke, StringComparison.Ordinal);
        Assert.Contains("mount root/source", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void RealSmoke_InstallsCleanupBeforeEveryFallibleAllocationAndPreservesSignalExit()
    {
        var smoke = File.ReadAllText(Path.Combine(RepoRoot, "tools", "full-stack-deployment-smoke.sh"));
        var realSmoke = smoke.IndexOf("real_smoke()", StringComparison.Ordinal);
        var staticContract = smoke.IndexOf("static_contract()", StringComparison.Ordinal);
        var firstStaticAllocation = smoke.IndexOf("if ! base_file=$(mktemp); then", staticContract, StringComparison.Ordinal);
        var firstRealAllocation = smoke.IndexOf("if ! override=$(mktemp); then", realSmoke, StringComparison.Ordinal);
        var initialized = smoke.IndexOf("TEMP_FILES=()", StringComparison.Ordinal);
        var trap = smoke.IndexOf("trap cleanup EXIT", StringComparison.Ordinal);
        var cleanupFiles = smoke.IndexOf("rm -f -- \"${TEMP_FILES[@]}\"", StringComparison.Ordinal);
        var overrideWrite = smoke.IndexOf("cat >\"$override\"", realSmoke, StringComparison.Ordinal);
        var firstPull = smoke.IndexOf("docker pull ", realSmoke, StringComparison.Ordinal);
        Assert.True(realSmoke >= 0 && staticContract >= 0 && initialized >= 0 && initialized < trap);
        Assert.True(trap < firstStaticAllocation && trap < firstRealAllocation);
        Assert.True(cleanupFiles >= 0 && cleanupFiles < firstStaticAllocation);
        Assert.True(trap < overrideWrite && trap < firstPull);
        Assert.Contains("TEMP_FILES+=(\"$base_file\")", smoke, StringComparison.Ordinal);
        Assert.Contains("TEMP_FILES+=(\"$gpu_file\")", smoke, StringComparison.Ordinal);
        Assert.Contains("TEMP_FILES+=(\"$override\")", smoke, StringComparison.Ordinal);
        Assert.Contains("trap 'on_signal INT' INT", smoke, StringComparison.Ordinal);
        Assert.Contains("trap 'on_signal TERM' TERM", smoke, StringComparison.Ordinal);
        Assert.Contains("exit 130", smoke, StringComparison.Ordinal);
        Assert.Contains("exit 143", smoke, StringComparison.Ordinal);
        Assert.DoesNotContain("trap cleanup EXIT INT TERM", smoke, StringComparison.Ordinal);
        Assert.Contains("down --volumes --remove-orphans", smoke, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedVolumes_HaveOnlyTheirIntendedWritableConsumers()
    {
        using var compose = ComposeConfig(ComposePath);
        var services = compose.RootElement.GetProperty("services");
        var consumers = new Dictionary<string, string[]>
        {
            ["nzbdav_media"] = ["nzbdav", "jellyfin"],
            ["completed_downloads"] = ["nzbdav", "sonarr", "radarr"]
        };

        foreach (var (volume, expectedConsumers) in consumers)
        {
            var actual = services.EnumerateObject()
                .Where(service => Mounts(service.Value).Any(m => m.GetProperty("source").GetString() == volume))
                .Select(service => service.Name)
                .OrderBy(x => x);
            Assert.Equal(expectedConsumers.OrderBy(x => x), actual);
        }

        Assert.DoesNotContain(Mounts(services.GetProperty("jellyfin")), mount => mount.GetProperty("target").GetString() == "/media/nzbdav" && mount.TryGetProperty("read_only", out var readOnly) && readOnly.GetBoolean());
        Assert.DoesNotContain(Mounts(services.GetProperty("prowlarr")), mount => mount.GetProperty("target").GetString() is "/media/nzbdav" or "/data/completed-downloads");
        Assert.All(new[] { "nzbdav", "sonarr", "radarr" }, service =>
            Assert.Contains(Mounts(services.GetProperty(service)), mount =>
                mount.GetProperty("source").GetString() == "completed_downloads" &&
                mount.GetProperty("target").GetString() == "/data/completed-downloads"));
    }

    private static IEnumerable<JsonElement> Mounts(JsonElement service) =>
        service.GetProperty("volumes").EnumerateArray();

    private static JsonDocument ComposeConfig(params string[] files) => ComposeConfigWithMasterKey(null, files);

    private static JsonDocument ComposeConfigWithMasterKey(string? masterKey, params string[] files)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "docker",
            WorkingDirectory = RepoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (masterKey is null)
        {
            startInfo.Environment.Remove("NZBDAV_MASTER_KEY");
        }
        else
        {
            startInfo.Environment["NZBDAV_MASTER_KEY"] = masterKey;
        }
        startInfo.ArgumentList.Add("compose");
        foreach (var file in files)
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(file);
        }
        startInfo.ArgumentList.Add("config");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start docker compose");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"docker compose config failed: {error}");
        return JsonDocument.Parse(output);
    }
}
