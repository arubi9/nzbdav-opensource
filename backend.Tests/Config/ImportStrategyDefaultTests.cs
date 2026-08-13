using NzbWebDAV.Config;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace backend.Tests.Config;

[Collection(nameof(EnvironmentVariableCollection))]
public sealed class ImportStrategyDefaultTests
{
    // The full-stack compose has no rclone mount. Defaulting to symlinks there
    // reported /mnt/nzbdav/completed-symlinks paths that exist in no container,
    // and Sonarr/Radarr rejected every completed job with
    // "No files found are eligible for import".
    [Fact]
    public void FullStackDefaultsToStrmWithoutAnExplicitOperatorValue()
    {
        using var _ = new TemporaryEnvironment(("NZBDAV_FULL_STACK", "true"));
        Assert.Equal("strm", new ConfigManager(new ConfigEncryptionService()).GetImportStrategy());
    }

    [Fact]
    public void StandaloneKeepsTheSymlinkDefault()
    {
        using var _ = new TemporaryEnvironment(("NZBDAV_FULL_STACK", null));
        Assert.Equal("symlinks", new ConfigManager(new ConfigEncryptionService()).GetImportStrategy());
    }

    [Fact]
    public void ExplicitOperatorValueStillWinsInFullStack()
    {
        using var _ = new TemporaryEnvironment(("NZBDAV_FULL_STACK", "true"));
        var configManager = new ConfigManager(new ConfigEncryptionService());
        configManager.UpdateValues([new ConfigItem { ConfigName = "api.import-strategy", ConfigValue = "symlinks" }]);

        Assert.Equal("symlinks", configManager.GetImportStrategy());
    }
}
