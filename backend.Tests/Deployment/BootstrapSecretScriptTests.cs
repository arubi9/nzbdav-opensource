using System.Diagnostics;

namespace backend.Tests.Deployment;

public sealed class BootstrapSecretScriptTests
{
    [Fact]
    public void BootstrapScript_UsesPersistentFilesAndExportsBothSecretsWithoutLoggingValues()
    {
        var script = File.ReadAllText(ScriptPath());

        Assert.Contains("NZBDAV_MASTER_KEY", script);
        Assert.Contains("FRONTEND_BACKEND_API_KEY", script);
        Assert.Contains("umask 077", script);
        Assert.Contains("export NZBDAV_MASTER_KEY", script);
        Assert.Contains("export FRONTEND_BACKEND_API_KEY", script);
        Assert.DoesNotContain("echo \"$NZBDAV_MASTER_KEY\"", script);
        Assert.DoesNotContain("echo \"$FRONTEND_BACKEND_API_KEY\"", script);
    }

    [Fact]
    public void BootstrapScript_GeneratesPersistsAndReusesIndependentSecretsOnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The shell behavior test requires Linux file permissions.");
        Assert.SkipUnless(Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase),
            "Bootstrap must run as root to establish the secret-directory ownership invariant.");

        var configPath = CreateConfigPath();
        try
        {
            var first = RunBootstrap(configPath);
            Assert.Equal(0, first.ExitCode);
            var masterFirst = ReadSecret(configPath, "nzbdav-master-key");
            var apiFirst = ReadSecret(configPath, "frontend-backend-api-key");
            var sessionFirst = ReadSecret(configPath, "session-key");

            Assert.NotEmpty(masterFirst);
            Assert.NotEmpty(apiFirst);
            Assert.NotEmpty(sessionFirst);
            Assert.NotEqual(masterFirst, apiFirst);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(configPath, "bootstrap-secrets", "nzbdav-master-key")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(configPath, "bootstrap-secrets", "frontend-backend-api-key")));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(configPath, "bootstrap-secrets", "session-key")));
            Assert.DoesNotContain(masterFirst, first.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(apiFirst, first.Output, StringComparison.Ordinal);

            var second = RunBootstrap(configPath);
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(masterFirst, ReadSecret(configPath, "nzbdav-master-key"));
            Assert.Equal(apiFirst, ReadSecret(configPath, "frontend-backend-api-key"));
            Assert.Equal(sessionFirst, ReadSecret(configPath, "session-key"));
            Assert.DoesNotContain(masterFirst, second.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(apiFirst, second.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(configPath, recursive: true);
        }
    }

    [Fact]
    public void BootstrapScript_ExplicitEnvironmentValuesOverridePersistedSecretsOnLinux()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The shell behavior test requires Linux file permissions.");
        Assert.SkipUnless(Environment.UserName.Equals("root", StringComparison.OrdinalIgnoreCase),
            "Bootstrap must run as root to establish the secret-directory ownership invariant.");

        var configPath = CreateConfigPath();
        const string masterOverride = "VwJb7F5zWKx30P0yOsJJ1+ZvSEEhN9wJNq+2+PTnsOg=";
        const string apiOverride = "test-api-override-with-entropy";
        try
        {
            var result = RunBootstrap(configPath, masterOverride, apiOverride);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(masterOverride, ReadSecret(configPath, "nzbdav-master-key"));
            Assert.Equal(apiOverride, ReadSecret(configPath, "frontend-backend-api-key"));
            Assert.DoesNotContain(masterOverride, result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(apiOverride, result.Output, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(configPath, recursive: true);
        }
    }

    private static string ScriptPath()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "bootstrap-secrets.sh");
    }

    private static string CreateConfigPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "nzbdav-bootstrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string ReadSecret(string configPath, string name) =>
        File.ReadAllText(Path.Combine(configPath, "bootstrap-secrets", name)).TrimEnd('\r', '\n');

    private static ShellResult RunBootstrap(string configPath, string? master = null, string? api = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add(". " + ShellQuote(ScriptPath()));
        start.Environment["CONFIG_PATH"] = configPath;
        if (master is null)
            start.Environment.Remove("NZBDAV_MASTER_KEY");
        else
            start.Environment["NZBDAV_MASTER_KEY"] = master;
        if (api is null)
            start.Environment.Remove("FRONTEND_BACKEND_API_KEY");
        else
            start.Environment["FRONTEND_BACKEND_API_KEY"] = api;

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ShellResult(process.ExitCode, output);
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private sealed record ShellResult(int ExitCode, string Output);
}
