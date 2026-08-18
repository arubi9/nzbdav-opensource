using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Nzbdav;
using Xunit;

namespace Jellyfin.Plugin.Nzbdav.Tests;

/// <summary>
/// Runs in tools/alpine-tmpfile-gate.sh. Linux tests intentionally skip unless
/// that gate supplied a real tmpfs, uid 1000, and CapEff == 0.
/// </summary>
public sealed class AlpineTmpfileGateTests
{
    private static readonly MethodInfo PersistMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("PersistInPlaceRecoveryJournal", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing fixed-slot transaction writer.");
    private static readonly MethodInfo RecoveryMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("RecoverPendingInPlaceUpdates", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing fixed-slot recovery entry point.");
    private static readonly MethodInfo CaptureIdentityMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("CaptureFileIdentity", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing identity capture.");
    private static readonly MethodInfo CreateStateMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("CreateRecoveryState", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing fixed-slot state constructor.");
    private static readonly MethodInfo WriteSlotMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "WriteRecoverySlotWithHook"
            && method.GetParameters().Length == 7)
        ?? throw new InvalidOperationException("Missing fixed-slot writer.");
    private static readonly MethodInfo WriteSlotWithHookMethod = WriteSlotMethod;
    private static readonly MethodInfo ReadSlotExpectationMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "ReadRecoverySlotExpectation"
            && method.GetParameters().Length == 3)
        ?? throw new InvalidOperationException("Missing fixed-slot expectation reader.");
    private static readonly MethodInfo NextGenerationMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("NextRecoveryGeneration", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing generation increment.");
    private static readonly MethodInfo IsNewerGenerationMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("IsRecoveryGenerationNewer", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing generation comparison.");
    private static readonly MethodInfo TryReadRecoveryStateBytesMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethod("TryReadRecoveryStateBytes", BindingFlags.NonPublic | BindingFlags.Static)
        ?? throw new InvalidOperationException("Missing recovery state validator.");
    private static readonly MethodInfo WriteTextMethod = typeof(NzbdavLibrarySyncTask)
        .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
        .Single(method => method.Name == "WriteTextAtomicallyAsync"
            && method.GetParameters().Length == 7)
        ?? throw new InvalidOperationException("Missing rooted text writer.");

    [Fact]
    public async Task NestedBindMountIsRejectedByRootedDescriptorTraversal()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var root = Environment.GetEnvironmentVariable("NZBDAV_NESTED_BIND_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            return;

        var nested = Path.Combine(root, "nested");
        Assert.True(IsMountPoint(nested), $"The privileged gate must provide a real nested bind mount: {nested}");
        var destination = Path.Combine(nested, "blocked.strm");
        File.WriteAllText(destination, "foreign");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            (Task)WriteTextMethod.Invoke(null,
                [root, destination, "must-not-cross-bind", CancellationToken.None, null, null, null])!);
        Assert.Equal("foreign", File.ReadAllText(destination));
    }

    [Fact]
    public void OTmpfileAbiConstantsAreExactForSupportedArchitectures()
    {
        Assert.Equal(0x410000, NzbdavLibrarySyncTask.LinuxOpenFlagsFor(Architecture.X64).TmpFile);
        Assert.Equal(0x404000, NzbdavLibrarySyncTask.LinuxOpenFlagsFor(Architecture.Arm64).TmpFile);
    }

    [Fact]
    public void RecoveryTransactionsRemainExactlyTwoFixedSlotsAfter10000Operations()
    {
        if (!GateEnabled()) return;
        var root = CreateGateRoot("bounded");
        var destination = Path.Combine(root, "movie.strm");
        try
        {
            const string oldContent = "old";
            File.WriteAllText(destination, oldContent);
            var identity = CaptureIdentity(root, destination);
            for (var operation = 0; operation < 10_000; operation++)
            {
                Persist(root, destination, identity, oldContent, "new-" + operation);
                Assert.True(Recover(root));
            }

            Assert.Equal(["slot0", "slot1"], SlotNames(root));
            var states = ReadSlotDocuments(root);
            Assert.Equal(2, states.Length);
            Assert.All(states, state => Assert.True(state.GetProperty("Generation").GetUInt64() != 0));
            var latest = states.OrderByDescending(state => state.GetProperty("Generation").GetUInt64()).First();
            Assert.Equal("complete", latest.GetProperty("Phase").GetString());
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(Path.Combine(root, ".nzbdav-recovery")), path =>
                Path.GetFileName(path) is "pointer" or "ready" or "old" or "complete"
                || Path.GetFileName(path).Contains("journal", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(path).Contains("operation", StringComparison.OrdinalIgnoreCase));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void EverySlotByteTruncationAndCorruptionFailsClosedUnlessTheSurvivorIsProvable()
    {
        if (!GateEnabled()) return;
        var root = CreateGateRoot("corruption");
        var destination = Path.Combine(root, "movie.strm");
        try
        {
            File.WriteAllText(destination, "old");
            var identity = CaptureIdentity(root, destination);
            Persist(root, destination, identity, "old", "new");
            Assert.True(Recover(root));
            var recovery = Path.Combine(root, ".nzbdav-recovery");
            var baseline = new[] { File.ReadAllBytes(Path.Combine(recovery, "slot0")), File.ReadAllBytes(Path.Combine(recovery, "slot1")) };
            var (latestSlot, latestGeneration, _, _) = LatestSlot(root);
            Assert.Equal("complete", StatePhase(baseline[latestSlot]));
            Assert.NotEqual(1UL, latestGeneration);
            Assert.Equal(2, SlotNames(root).Length);

            for (var slot = 0; slot < 2; slot++)
            {
                var path = Path.Combine(recovery, $"slot{slot}");
                for (var length = 0; length < baseline[slot].Length; length++)
                {
                    var corrupted = baseline[slot][..length];
                    var result = ExerciseCorruption(root, destination, baseline, slot, path, corrupted);
                    Assert.Equal(result.SurvivorIsProvable, result.Recovered);
                    if (slot == latestSlot)
                    {
                        Assert.False(result.SurvivorIsProvable);
                        Assert.False(result.Recovered);
                        Assert.Equal("old", File.ReadAllText(destination));
                    }
                    Assert.Equal("old", File.ReadAllText(destination));
                    if (!result.SurvivorIsProvable)
                        Assert.Equal(corrupted, File.ReadAllBytes(path));
                }

                for (var offset = 0; offset < baseline[slot].Length; offset++)
                {
                    var corrupted = baseline[slot].ToArray();
                    corrupted[offset] ^= 0x01;
                    var result = ExerciseCorruption(root, destination, baseline, slot, path, corrupted);
                    Assert.Equal(result.SurvivorIsProvable, result.Recovered);
                    if (slot == latestSlot)
                    {
                        Assert.False(result.SurvivorIsProvable);
                        Assert.False(result.Recovered);
                        Assert.Equal("old", File.ReadAllText(destination));
                    }
                    Assert.Equal("old", File.ReadAllText(destination));
                    if (!result.SurvivorIsProvable)
                        Assert.Equal(corrupted, File.ReadAllBytes(path));
                }

                if (slot == latestSlot)
                {
                    // The latest complete slot is non-genesis. Its surviving
                    // predecessor cannot prove it after the latest slot is
                    // corrupted, so recovery must preserve the destination.
                    Assert.False(IsProvableFromSlots(
                        File.ReadAllBytes(Path.Combine(recovery, $"slot{1 - slot}")),
                        File.ReadAllBytes(path)));
                    Assert.Equal("old", File.ReadAllText(destination));
                }
            }
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RecoveryPredecessorReplacementAfterTargetPublicationFailsClosedAndRetriesInTwoSlots()
    {
        if (!GateEnabled()) return;
        var root = CreateGateRoot("predecessor-race");
        var destination = Path.Combine(root, "movie.strm");
        try
        {
            File.WriteAllText(destination, "old");
            var identity = CaptureIdentity(root, destination);
            Persist(root, destination, identity, "old", "new");
            Assert.True(Recover(root));

            var recovery = Path.Combine(root, ".nzbdav-recovery");
            var (latestSlot, latestGeneration, latestBytes, latestHash) = LatestSlot(root);
            var inactiveSlot = 1 - latestSlot;
            var intent = CreateState(
                Next(latestGeneration), Guid.NewGuid().ToString("N"), "intent", latestHash,
                destination, identity, "old", "new");
            var targetExpectation = ReadExpectation(root, recovery, inactiveSlot);
            var predecessorExpectation = ReadExpectation(root, recovery, latestSlot);
            var predecessorPath = Path.Combine(recovery, $"slot{latestSlot}");
            var targetPath = Path.Combine(recovery, $"slot{inactiveSlot}");
            var targetBefore = File.ReadAllBytes(targetPath);
            var replaced = false;
            Action<string> hook = path =>
            {
                if (!replaced && string.Equals(path, predecessorPath, StringComparison.Ordinal)
                    && File.Exists(targetPath))
                {
                    replaced = true;
                    File.Delete(predecessorPath);
                    File.WriteAllText(predecessorPath, "foreign-predecessor");
                }
            };

            var error = Assert.Throws<TargetInvocationException>(() =>
                WriteSlotWithHookMethod.Invoke(null,
                    [root, recovery, inactiveSlot, intent, hook, targetExpectation, predecessorExpectation]));
            Assert.IsType<IOException>(error.InnerException);
            Assert.True(replaced);
            Assert.Equal("foreign-predecessor", File.ReadAllText(predecessorPath));
            // The target was held through publication and must be durably
            // restored to its exact prior slot bytes when predecessor
            // revalidation fails. The foreign predecessor remains untouched.
            Assert.Equal(targetBefore, File.ReadAllBytes(targetPath));
            Assert.False(Recover(root));
            Assert.Equal("old", File.ReadAllText(destination));

            // The operator removes the collision. The exact prior target is
            // still a normal fixed-slot predecessor for a bounded retry; no
            // third artifact or unbounded retry state is needed.
            File.Delete(predecessorPath);
            File.WriteAllBytes(predecessorPath, latestBytes);
            Persist(root, destination, identity, "old", "new-retry");
            Assert.True(Recover(root));
            Assert.Equal(["slot0", "slot1"], SlotNames(root));
            Assert.Equal("old", File.ReadAllText(destination));
        }
        finally { Delete(root); }
    }

    [Fact]
    public void RecoveryGenerationWrapAndTieAreDeterministicAndMonotonic()
    {
        Assert.Equal(1UL, Next(ulong.MaxValue));
        Assert.Equal(2UL, Next(1));
        Assert.True(IsNewer(1, ulong.MaxValue));
        Assert.False(IsNewer(ulong.MaxValue, 1));
        Assert.False(IsNewer(42, 42));
        Assert.False(IsNewer(1, 1UL + (1UL << 63)));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("ready")]
    [InlineData("complete")]
    public void CrashAtEachFixedSlotPhasePreservesTheLatestFullyValidState(string phase)
    {
        if (!GateEnabled()) return;
        var root = CreateGateRoot("phase-" + phase);
        var destination = Path.Combine(root, "movie.strm");
        try
        {
            File.WriteAllText(destination, "old");
            var identity = CaptureIdentity(root, destination);
            Persist(root, destination, identity, "old", "new");
            Assert.True(Recover(root));

            var (latestSlot, latestGeneration, latestBytes, latestHash) = LatestSlot(root);
            Assert.Equal("complete", StatePhase(latestBytes));
            var transactionId = Guid.NewGuid().ToString("N");
            var intentGeneration = Next(latestGeneration);
            var intent = CreateState(intentGeneration, transactionId, "intent", latestHash, destination, identity, "old", "new");
            var intentHash = Convert.ToHexString(SHA256.HashData(intent));
            var inactive = 1 - latestSlot;
            WriteSlot(root, inactive, intent);

            if (phase is "ready" or "complete")
            {
                var readyGeneration = Next(intentGeneration);
                var ready = CreateState(readyGeneration, transactionId, "ready", intentHash, destination, identity, "old", "new");
                var readyHash = Convert.ToHexString(SHA256.HashData(ready));
                WriteSlot(root, latestSlot, ready);
                if (phase == "complete")
                {
                    var complete = CreateState(Next(readyGeneration), transactionId, "complete",
                        readyHash, destination, identity, "old", "new");
                    WriteSlot(root, inactive, complete);
                }
            }

            Assert.True(Recover(root));
            Assert.Equal("old", File.ReadAllText(destination));
            Assert.Equal(["slot0", "slot1"], SlotNames(root));
            Assert.All(ReadSlotDocuments(root), state => Assert.NotEqual(0UL, state.GetProperty("Generation").GetUInt64()));
        }
        finally { Delete(root); }
    }

    [Fact]
    public async Task AnonymousTextCommitIsNoOverwriteAndUsesNoNamedTemporaryArtifact()
    {
        if (!OperatingSystem.IsLinux())
            return;
        var root = Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT");
        if (string.IsNullOrWhiteSpace(root))
            return;

        Assert.True(IsTmpfs(root), $"The gate root must be tmpfs: {root}");
        Assert.Equal(1000u, GetUid());
        Assert.Equal(0UL, GetEffectiveCapabilities());
        Directory.CreateDirectory(root);

        var write = typeof(NzbdavLibrarySyncTask).GetMethod(
            "WriteTextAtomicallyAsync", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing anonymous text commit.");
        var capture = typeof(NzbdavLibrarySyncTask).GetMethod(
            "CaptureFileIdentity", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Missing identity capture.");
        var path = Path.Combine(root, "no-overwrite.txt");
        try
        {
            await ((Task)write.Invoke(null, [root, path, "first", CancellationToken.None, null, null, null])!);
            var identity = capture.Invoke(null, [root, path]);
            await Assert.ThrowsAnyAsync<Exception>(() => (Task)write.Invoke(null, [root, path, "foreign-overwrite", CancellationToken.None, null, null, null])!);
            Assert.Equal("first", File.ReadAllText(path));
            Assert.Equal(identity, capture.Invoke(null, [root, path]));
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories), entry =>
                Path.GetFileName(entry).StartsWith(".nzbdav.tmp-", StringComparison.Ordinal)
                || Path.GetFileName(entry).StartsWith(".nzbdav.quarantine-tmp-", StringComparison.Ordinal)
                || Path.GetFileName(entry).StartsWith("prepare-", StringComparison.Ordinal));
        }
        finally { Delete(path); }
    }

    private static bool GateEnabled()
        => !OperatingSystem.IsLinux()
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT"));

    private static string CreateGateRoot(string name)
    {
        var parent = Environment.GetEnvironmentVariable("NZBDAV_O_TMPFILE_TEST_ROOT");
        var root = Path.Combine(string.IsNullOrWhiteSpace(parent) ? Path.Combine(Path.GetTempPath(), "nzbdav-recovery-tests") : parent,
            name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Delete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }

    private static string[] SlotNames(string root)
        => Directory.EnumerateFileSystemEntries(Path.Combine(root, ".nzbdav-recovery"))
            .Select(Path.GetFileName).OrderBy(value => value, StringComparer.Ordinal).ToArray()!;

    private static JsonElement[] ReadSlotDocuments(string root)
    {
        var recovery = Path.Combine(root, ".nzbdav-recovery");
        using var slot0 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recovery, "slot0")));
        using var slot1 = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(recovery, "slot1")));
        return [slot0.RootElement.Clone(), slot1.RootElement.Clone()];
    }

    private static (int Slot, ulong Generation, byte[] Bytes, string Hash) LatestSlot(string root)
    {
        var recovery = Path.Combine(root, ".nzbdav-recovery");
        var bytes = new[] { File.ReadAllBytes(Path.Combine(recovery, "slot0")), File.ReadAllBytes(Path.Combine(recovery, "slot1")) };
        var generation0 = StateGeneration(bytes[0]);
        var generation1 = StateGeneration(bytes[1]);
        var slot = generation0 > generation1 ? 0 : 1;
        return (slot, StateGeneration(bytes[slot]), bytes[slot], Convert.ToHexString(SHA256.HashData(bytes[slot])));
    }

    private static byte[] CreateState(ulong generation, string operationId, string phase, string previousHash,
        string destination, object identity, string oldContent, string newContent)
    {
        var state = CreateStateMethod.Invoke(null,
            [generation, operationId, phase, previousHash, Path.GetFileName(destination), identity,
                Encoding.UTF8.GetBytes(oldContent), Encoding.UTF8.GetBytes(newContent)])
            ?? throw new InvalidOperationException("State creation failed.");
        return JsonSerializer.SerializeToUtf8Bytes(state, state.GetType());
    }

    private static void WriteSlot(string root, int slot, byte[] bytes)
    {
        var recoveryRoot = Path.Combine(root, ".nzbdav-recovery");
        var target = ReadExpectation(root, recoveryRoot, slot);
        var predecessor = ReadExpectation(root, recoveryRoot, 1 - slot);
        WriteSlotMethod.Invoke(null, [root, recoveryRoot, slot, bytes, null, target, predecessor]);
    }

    private static object ReadExpectation(string root, string recoveryRoot, int slot)
        => ReadSlotExpectationMethod.Invoke(null, [root, Path.Combine(recoveryRoot, $"slot{slot}"), slot])
            ?? throw new InvalidOperationException("Recovery slot expectation was not returned.");

    private static void Persist(string root, string path, object identity, string oldContent, string newContent)
        => PersistMethod.Invoke(null, [root, path, identity, Encoding.UTF8.GetBytes(oldContent), Encoding.UTF8.GetBytes(newContent)]);

    private static bool Recover(string root)
        => (bool)(RecoveryMethod.Invoke(null, [root, CancellationToken.None])
            ?? throw new InvalidOperationException("Recovery returned no result."));

    private static (bool Recovered, bool SurvivorIsProvable) ExerciseCorruption(
        string root, string destination, byte[][] baseline, int corruptedSlot, string path, byte[] corrupted)
    {
        var recovery = Path.Combine(root, ".nzbdav-recovery");
        File.WriteAllBytes(Path.Combine(recovery, "slot0"), baseline[0]);
        File.WriteAllBytes(Path.Combine(recovery, "slot1"), baseline[1]);
        File.WriteAllBytes(path, corrupted);
        var survivingPath = Path.Combine(recovery, $"slot{1 - corruptedSlot}");
        var survivor = File.ReadAllBytes(survivingPath);
        var survivorIsProvable = IsProvableFromSlots(survivor, corrupted);
        var destinationBefore = File.ReadAllBytes(destination);
        var recovered = Recover(root);
        Assert.Equal(["slot0", "slot1"], SlotNames(root));
        Assert.Equal(survivor, File.ReadAllBytes(survivingPath));
        if (!survivorIsProvable)
            Assert.Equal(destinationBefore, File.ReadAllBytes(destination));
        else
            Assert.Equal("old", File.ReadAllText(destination));
        return (recovered, survivorIsProvable);
    }

    private static bool IsProvableFromSlots(byte[] survivor, byte[] predecessor)
    {
        if (!TryReadValidState(survivor, out var survivorDocument)) return false;
        var survivorGeneration = survivorDocument.GetProperty("Generation").GetUInt64();
        var previous = survivorDocument.GetProperty("PreviousStateSha256").GetString();
        if (survivorGeneration == 1)
            return survivorDocument.GetProperty("Phase").GetString() == "intent" && previous == "none";
        if (!TryReadValidState(predecessor, out var predecessorDocument)) return false;
        return survivorGeneration == Next(predecessorDocument.GetProperty("Generation").GetUInt64())
            && string.Equals(previous, Convert.ToHexString(SHA256.HashData(predecessor)), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadValidState(byte[] bytes, out JsonElement document)
    {
        try
        {
            var arguments = new object?[] { bytes, null, null };
            if (!(bool)(TryReadRecoveryStateBytesMethod.Invoke(null, arguments) ?? false))
            {
                document = default;
                return false;
            }

            using var parsed = JsonDocument.Parse(bytes);
            document = parsed.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            document = default;
            return false;
        }
    }

    private static ulong StateGeneration(byte[] bytes)
        => JsonDocument.Parse(bytes).RootElement.GetProperty("Generation").GetUInt64();

    private static string StatePhase(byte[] bytes)
        => JsonDocument.Parse(bytes).RootElement.GetProperty("Phase").GetString()!;

    private static object CaptureIdentity(string root, string path)
        => CaptureIdentityMethod.Invoke(null, [root, path])
            ?? throw new InvalidOperationException("Identity capture failed.");

    private static ulong Next(ulong generation) => (ulong)NextGenerationMethod.Invoke(null, [generation])!;

    private static bool IsNewer(ulong candidate, ulong current) => (bool)IsNewerGenerationMethod.Invoke(null, [candidate, current])!;

    private static bool IsTmpfs(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('/');
        return File.ReadLines("/proc/mounts")
            .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(parts => parts.Length >= 3
                && string.Equals(parts[1], full, StringComparison.Ordinal)
                && string.Equals(parts[2], "tmpfs", StringComparison.Ordinal));
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUid();

    private static ulong GetEffectiveCapabilities()
    {
        var line = File.ReadLines("/proc/self/status")
            .FirstOrDefault(value => value.StartsWith("CapEff:", StringComparison.Ordinal));
        return line is null ? ulong.MaxValue : Convert.ToUInt64(line["CapEff:".Length..].Trim(), 16);
    }

    private static bool IsMountPoint(string path)
    {
        var target = Path.GetFullPath(path).TrimEnd('/');
        foreach (var line in File.ReadLines("/proc/self/mountinfo"))
        {
            var separator = line.IndexOf(" - ", StringComparison.Ordinal);
            if (separator < 0)
                continue;
            var fields = line[..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5)
                continue;
            var mountPoint = fields[4]
                .Replace("\\040", " ", StringComparison.Ordinal)
                .Replace("\\011", "\t", StringComparison.Ordinal)
                .Replace("\\134", "\\", StringComparison.Ordinal)
                .Replace("\\012", "\n", StringComparison.Ordinal);
            if (string.Equals(mountPoint.TrimEnd('/'), target, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
