using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Setup.Core;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Setup.Core;

[Collection(nameof(backend.Tests.Config.EnvironmentVariableCollection))]
public sealed class SetupEmergencyCandidateJournalTests
{
    [Fact]
    public async Task Journal_IsEncryptedBoundedAndReadableAfterRestart()
    {
        using var environment = CreateEnvironment("roundtrip");
        await using var context = await CreateContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);

        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("fresh-token", "issue:operation-1");
        var path = SetupEmergencyCandidateJournal.JournalPath;
        var ciphertext = await File.ReadAllTextAsync(path);
        Assert.True(ConfigEncryptionService.IsEncryptedFormat(ciphertext));
        Assert.InRange(ciphertext.Length, 4, 16_384);

        var restartedManager = new ConfigManager(new ConfigEncryptionService());
        await restartedManager.LoadConfig();
        await using var restartedContext = await CreateContextAsync();
        var restartedPersistence = new SetupConfigPersistence(restartedManager, restartedContext);
        var recovered = await restartedPersistence.ReadEmergencyCandidateAsync();

        Assert.NotNull(recovered);
        Assert.Equal("fresh-token", recovered.Token);
        Assert.Equal("issue:operation-1", recovered.OperationId);
    }

    [Fact]
    public async Task Journal_OldKeyIsReencryptedBeforeRestartDropsOldKey()
    {
        using var environment = CreateEnvironment("key-rotation");
        var oldKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var newKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", oldKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", null);

        string oldCiphertext;
        using (var oldEncryption = new ConfigEncryptionService())
        {
            var document = JsonSerializer.Serialize(new { token = "old-token", operationId = "old-op" });
            oldCiphertext = oldEncryption.Encrypt(SetupConfigKeys.CandidateSessionToken, document);
        }

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY", newKey);
        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", oldKey);
        await using var context = await CreateContextAsync();
        var directory = Path.GetDirectoryName(SetupEmergencyCandidateJournal.JournalPath)!;
        if (OperatingSystem.IsLinux())
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        else
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(SetupEmergencyCandidateJournal.JournalPath, oldCiphertext);
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(
                SetupEmergencyCandidateJournal.JournalPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        var manager = new ConfigManager(new ConfigEncryptionService());
        var persistence = new SetupConfigPersistence(manager, context);
        var recovered = await persistence.ReadEmergencyCandidateAsync();
        var rotatedCiphertext = await File.ReadAllTextAsync(SetupEmergencyCandidateJournal.JournalPath);

        Assert.Equal(("old-token", "old-op"), (recovered!.Token, recovered.OperationId));
        Assert.NotEqual(oldCiphertext, rotatedCiphertext);

        Environment.SetEnvironmentVariable("NZBDAV_MASTER_KEY_OLD", null);
        var restartedManager = new ConfigManager(new ConfigEncryptionService());
        await using var restartedContext = await CreateContextAsync();
        var restartedPersistence = new SetupConfigPersistence(restartedManager, restartedContext);
        var afterOldKeyRetirement = await restartedPersistence.ReadEmergencyCandidateAsync();
        Assert.Equal(("old-token", "old-op"), (afterOldKeyRetirement!.Token, afterOldKeyRetirement.OperationId));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Journal_RejectsFifoWithoutBlocking()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The FIFO handle test requires Linux file types.");
        using var environment = CreateEnvironment("fifo");
        await using var context = await CreateContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token", "operation");

        var path = SetupEmergencyCandidateJournal.JournalPath;
        File.Delete(path);
        Assert.Equal(0, Native.mkfifo(path, 0x180));
        try
        {
            await Assert.ThrowsAsync<IOException>(() => persistence.ReadEmergencyCandidateAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Journal_RejectsCharacterDeviceFromOpenedHandle()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The device handle test requires Linux file types.");
        Assert.SkipUnless(string.Equals(Environment.UserName, "root", StringComparison.OrdinalIgnoreCase),
            "Creating a character device requires root.");
        using var environment = CreateEnvironment("device");
        await using var context = await CreateContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token", "operation");

        var path = SetupEmergencyCandidateJournal.JournalPath;
        File.Delete(path);
        Assert.SkipWhen(
            Native.mknod(path, 0x2180, (1 << 8) | 3) != 0,
            "The platform denied creating a character device.");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => persistence.ReadEmergencyCandidateAsync());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Journal_RejectsPartialAndTruncatedReplacement_AndLeavesCrashTemporaryFileAlone()
    {
        using var environment = CreateEnvironment("corruption");
        await using var context = await CreateContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token-a", "op-a");
        var path = SetupEmergencyCandidateJournal.JournalPath;
        var original = await File.ReadAllBytesAsync(path);

        await File.WriteAllBytesAsync(path, original[..Math.Max(1, original.Length / 2)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => persistence.ReadEmergencyCandidateAsync());

        // A crash before rename leaves only a private encrypted temporary
        // record; the last complete destination remains the recovery source.
        await File.WriteAllBytesAsync(path, original);
        await File.WriteAllBytesAsync(
            Path.Combine(Path.GetDirectoryName(path)!, ".setup-candidate-emergency.crashed"),
            original[..Math.Max(1, original.Length / 2)]);
        var recovered = await persistence.ReadEmergencyCandidateAsync();
        Assert.Equal(("token-a", "op-a"), (recovered!.Token, recovered.OperationId));
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public async Task Journal_ReplacementDoesNotFollowSymlinkOrModifyHardlinkTarget()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The inode replacement test requires Linux file handles.");

        using var environment = CreateEnvironment("replacement");
        await using var context = await CreateContextAsync();
        var manager = new ConfigManager(new ConfigEncryptionService());
        await manager.LoadConfig();
        var persistence = new SetupConfigPersistence(manager, context);
        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token-a", "op-a");
        var path = SetupEmergencyCandidateJournal.JournalPath;
        var directory = Path.GetDirectoryName(path)!;

        var outside = Path.Combine(directory, "outside-journal-target");
        await File.WriteAllTextAsync(outside, "outside");
        File.Delete(path);
        File.CreateSymbolicLink(path, outside);
        await Assert.ThrowsAsync<IOException>(() => persistence.ReadEmergencyCandidateAsync());
        File.Delete(path);

        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token-a", "op-a");
        var before = await File.ReadAllBytesAsync(path);
        var hardlinkTarget = Path.Combine(directory, "hardlink-journal-target");
        File.Copy(path, hardlinkTarget, overwrite: true);
        File.Delete(path);
        Assert.Equal(0, Native.link(hardlinkTarget, path));
        await persistence.SaveEmergencyCandidateUnderMutationGateAsync("token-b", "op-b");
        Assert.Equal(before, await File.ReadAllBytesAsync(hardlinkTarget));
        var recovered = await persistence.ReadEmergencyCandidateAsync();
        Assert.Equal(("token-b", "op-b"), (recovered!.Token, recovered.OperationId));
    }

    [Fact]
    public async Task EmergencyClear_CrossServiceCasNeverRemovesNewerCandidate()
    {
        using var environment = CreateEnvironment("emergency-cas-race");
        await using var firstContext = await CreateContextAsync();
        await using var secondContext = await CreateContextAsync();
        var firstManager = new ConfigManager(new ConfigEncryptionService());
        var secondManager = new ConfigManager(new ConfigEncryptionService());
        await firstManager.LoadConfig();
        await secondManager.LoadConfig();
        var first = new SetupConfigPersistence(firstManager, firstContext);
        var second = new SetupConfigPersistence(secondManager, secondContext);

        await first.SaveEmergencyCandidateUnderMutationGateAsync("token-a", "op-a");
        await Task.WhenAll(
            Task.Run(() => first.ClearEmergencyCandidateUnderMutationGateAsync("token-a", "op-a")),
            Task.Run(() => second.SaveEmergencyCandidateUnderMutationGateAsync("token-b", "op-b")));

        var recovered = await first.ReadEmergencyCandidateAsync();
        Assert.Equal(("token-b", "op-b"), (recovered!.Token, recovered.OperationId));
    }

    [Fact]
    public async Task CandidateClearIsCompareAndDeleteAndDoesNotRemoveReplacement()
    {
        using var environment = CreateEnvironment("cas");
        await using var firstContext = await CreateContextAsync();
        var firstManager = new ConfigManager(new ConfigEncryptionService());
        await firstManager.LoadConfig();
        var first = new SetupConfigPersistence(firstManager, firstContext);
        await first.SaveCandidateUnderMutationGateAsync("token-a", "op-a");

        await using var secondContext = await CreateContextAsync();
        var secondManager = new ConfigManager(new ConfigEncryptionService());
        await secondManager.LoadConfig();
        var second = new SetupConfigPersistence(secondManager, secondContext);
        await second.SaveCandidateUnderMutationGateAsync("token-b", "op-b");

        Assert.False(await first.ClearCandidateUnderMutationGateAsync("token-a", "op-a"));
        Assert.Equal("token-b", await first.ReadCandidateSessionTokenAsync(CancellationToken.None));
        Assert.Equal("op-b", await first.ReadCandidateSessionOperationAsync(CancellationToken.None));
        Assert.True(await second.ClearCandidateUnderMutationGateAsync("token-b", "op-b"));
    }

    private static backend.Tests.Config.TemporaryEnvironment CreateEnvironment(string name)
    {
        var configPath = Path.Combine(Path.GetTempPath(), "nzbdav-tests", $"setup-emergency-journal-{name}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(configPath);
        return new backend.Tests.Config.TemporaryEnvironment(
            ("CONFIG_PATH", configPath),
            ("DATABASE_URL", null),
            ("NZBDAV_FULL_STACK", "true"),
            ("SETUP_JELLYFIN_URL", "http://jellyfin.test"),
            ("NZBDAV_MASTER_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
    }

    private static async Task<DavDatabaseContext> CreateContextAsync()
    {
        var context = new DavDatabaseContext();
        await context.Database.MigrateAsync();
        return context;
    }

    [SupportedOSPlatform("linux")]
    private static class Native
    {
        [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int link(string oldPath, string newPath);

        [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int mkfifo(string path, int mode);

        [DllImport("libc", EntryPoint = "mknod", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int mknod(string path, int mode, long device);
    }
}
