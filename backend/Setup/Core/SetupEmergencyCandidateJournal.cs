using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Win32.SafeHandles;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Services;

namespace NzbWebDAV.Setup.Core;

/// <summary>
/// A single secondary candidate record used only to bridge the interval
/// between Jellyfin authentication and durable candidate persistence. The
/// database remains the primary record; this file is never loaded into the
/// normal setup cache.
///
/// The journal is deliberately implemented as a small file protocol rather
/// than as ordinary File.* calls. Every operation takes an OS-visible lock,
/// opens the record with a no-follow handle, validates that handle, and only
/// then reads, replaces, or retires its inode.
/// </summary>
internal static class SetupEmergencyCandidateJournal
{
    private const string DirectoryName = ".setup-emergency";
    private const string FileName = "setup-candidate-emergency.journal";
    private const string LockFileName = ".lock";
    private const string TempPrefix = ".setup-candidate-emergency.";
    private const int MaxTokenLength = 4096;
    private const int MaxOperationLength = 256;
    private const int MaxPlaintextLength = 8192;
    private const int MaxCiphertextLength = 16384;
    private const int MaxPathLength = 4096;

    // Linux open(2) flags. O_NONBLOCK is important: opening a FIFO must never
    // allow a recovery/read path to wait for a writer before fstat rejects it.
    private const int O_RDONLY = 0;
    private const int O_RDWR = 2;
    private const int O_WRONLY = 1;
    private const int O_CREAT = 64;
    private const int O_EXCL = 128;
    private const int O_CLOEXEC = 0x80000;
    private const int O_DIRECTORY = 0x10000;
    private const int O_NOFOLLOW = 0x20000;
    private const int O_NONBLOCK = 0x800;
    private const int O_SYNC = 0x101000;
    private const int LOCK_EX = 2;
    private const int LOCK_NB = 4;
    private const int LOCK_UN = 8;
    private const int AT_REMOVEDIR = 0x200;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenAlways = 4;
    private const uint CreateNew = 1;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint FileTypeDisk = 1;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;
    private const uint MoveFileReplaceExisting = 1;
    private const uint MoveFileWriteThrough = 8;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    internal static string JournalPath
    {
        get
        {
            var directory = EmergencyDirectoryPath;
            var path = Path.Combine(directory, FileName);
            if (path.Length > MaxPathLength || !IsWithinDirectory(path, directory))
                throw new InvalidOperationException("The setup emergency journal path is invalid.");
            return path;
        }
    }

    private static string EmergencyDirectoryPath
    {
        get
        {
            var root = DavDatabaseContext.ConfigPath;
            if (string.IsNullOrWhiteSpace(root) || root.Length > MaxPathLength)
                throw new InvalidOperationException("The setup emergency journal path is invalid.");

            var fullRoot = Path.GetFullPath(root);
            var directory = Path.Combine(fullRoot, DirectoryName);
            if (directory.Length > MaxPathLength || !IsWithinDirectory(directory, fullRoot, allowDirectoryName: true))
                throw new InvalidOperationException("The setup emergency journal path is invalid.");
            return directory;
        }
    }

    internal static async Task WriteAsync(
        ConfigManager configManager,
        string token,
        string operationId,
        CancellationToken cancellationToken)
    {
        _ = await WriteIfEmptyAsync(configManager, token, operationId, allowReplacement: true, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task<bool> WriteIfEmptyAsync(
        ConfigManager configManager,
        string token,
        string operationId,
        bool allowReplacement,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ValidateValue(token, MaxTokenLength, nameof(token));
        ValidateValue(operationId, MaxOperationLength, nameof(operationId));
        cancellationToken.ThrowIfCancellationRequested();

        byte[] documentBytes = Array.Empty<byte>();
        try
        {
            var encrypted = EncryptDocument(configManager, token, operationId, out documentBytes);
            await using var journalLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
            if (!allowReplacement && File.Exists(JournalPath))
                return false;
            await WriteEncryptedCoreAsync(journalLock, encrypted, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(documentBytes);
        }
    }

    internal static async Task<CandidateJournalEntry?> ReadAsync(
        ConfigManager configManager,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        cancellationToken.ThrowIfCancellationRequested();

        await using var journalLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var opened = await OpenAndReadCoreAsync(journalLock, configManager, cancellationToken)
            .ConfigureAwait(false);
        if (opened is null)
            return null;

        CandidateJournalEntry entry;
        var requiresUpgrade = opened.UsedOldKey || opened.RequiresFormatUpgrade;
        await using (opened.ConfigureAwait(false))
            entry = opened.Entry;

        // A journal can outlive a key rotation restart. Do not merely report
        // that its old/v1 ciphertext was readable: upgrade it while the same
        // exclusive OS lock is still held and before the old key can retire.
        if (requiresUpgrade)
            await RewriteEntryCoreAsync(journalLock, configManager, entry, cancellationToken)
                .ConfigureAwait(false);

        return entry;
    }

    /// <summary>
    /// Startup maintenance invokes this explicitly so a journal is migrated
    /// even when no setup request causes a normal read.
    /// </summary>
    internal static async Task MigrateAsync(
        ConfigManager configManager,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        cancellationToken.ThrowIfCancellationRequested();

        await using var journalLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var opened = await OpenAndReadCoreAsync(journalLock, configManager, cancellationToken)
            .ConfigureAwait(false);
        if (opened is null)
            return;

        var requiresUpgrade = opened.UsedOldKey || opened.RequiresFormatUpgrade;
        var entry = opened.Entry;
        await opened.DisposeAsync().ConfigureAwait(false);
        if (requiresUpgrade)
            await RewriteEntryCoreAsync(journalLock, configManager, entry, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>Checks for a journal using the same strict handle and lock as
    /// read/write. This is used when startup has no primary key and therefore
    /// cannot decrypt an emergency record.</summary>
    internal static async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using var journalLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var opened = await OpenExistingFileAsync(journalLock, cancellationToken).ConfigureAwait(false);
        if (opened is null)
            return false;

        await opened.Stream.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    internal static async Task<bool> ClearIfMatchesAsync(
        ConfigManager configManager,
        string expectedToken,
        string expectedOperationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configManager);
        ValidateValue(expectedToken, MaxTokenLength, nameof(expectedToken));
        ValidateValue(expectedOperationId, MaxOperationLength, nameof(expectedOperationId));
        cancellationToken.ThrowIfCancellationRequested();

        await using var journalLock = await AcquireLockAsync(cancellationToken).ConfigureAwait(false);
        var opened = await OpenAndReadCoreAsync(journalLock, configManager, cancellationToken)
            .ConfigureAwait(false);
        if (opened is null)
            return false;

        await using (opened.ConfigureAwait(false))
        {
            // Compare the complete authenticated operation and token before
            // retiring anything. Hashing makes unequal-length values take the
            // same fixed-time comparison path as equal values.
            var tokenMatches = ConstantTimeEquals(opened.Entry.Token, expectedToken);
            var operationMatches = ConstantTimeEquals(opened.Entry.OperationId, expectedOperationId);
            if (!tokenMatches || !operationMatches)
            {
                // A mismatching old-format record is still upgraded while we
                // hold the lock, preserving it for the next recovery attempt.
                if (opened.UsedOldKey || opened.RequiresFormatUpgrade)
                {
                    var entry = opened.Entry;
                    await opened.DisposeAsync().ConfigureAwait(false);
                    await RewriteEntryCoreAsync(journalLock, configManager, entry, cancellationToken)
                        .ConfigureAwait(false);
                }
                return false;
            }

            // The opened handle was fstat/handle-validated before the compare.
            // Unlinking while it is open retires that inode, not a pathname that
            // a different service may have installed after a read/close gap.
            RetireOpenedFile(journalLock, opened.Stream);
            return true;
        }
    }

    private static string EncryptDocument(
        ConfigManager configManager,
        string token,
        string operationId,
        out byte[] documentBytes)
    {
        var document = JsonSerializer.Serialize(
            new CandidateJournalPlaintext(token, operationId), JsonOptions);
        documentBytes = StrictUtf8.GetBytes(document);
        if (documentBytes.Length > MaxPlaintextLength)
        {
            CryptographicOperations.ZeroMemory(documentBytes);
            throw new InvalidOperationException("The setup emergency journal value is too large.");
        }

        // ConfigManager's existing AES-GCM service supplies the installation's
        // master-key encryption and authenticated candidate-key AAD.
        var encrypted = configManager.PrepareSetupForStorage(new List<Database.Models.ConfigItem>
        {
            new()
            {
                ConfigName = SetupConfigKeys.CandidateSessionToken,
                ConfigValue = document,
                IsEncrypted = false,
            },
        }).Single().ConfigValue;
        if (!ConfigEncryptionService.IsEncryptedFormat(encrypted)
            || encrypted.Length > MaxCiphertextLength)
            throw new InvalidOperationException("The setup emergency journal ciphertext is invalid.");
        return encrypted;
    }

    private static async Task RewriteEntryCoreAsync(
        JournalLock journalLock,
        ConfigManager configManager,
        CandidateJournalEntry entry,
        CancellationToken cancellationToken)
    {
        var encrypted = EncryptDocument(configManager, entry.Token, entry.OperationId, out var documentBytes);
        try
        {
            await WriteEncryptedCoreAsync(journalLock, encrypted, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(documentBytes);
        }
    }

    private static async Task WriteEncryptedCoreAsync(
        JournalLock journalLock,
        string encrypted,
        CancellationToken cancellationToken)
    {
        var bytes = StrictUtf8.GetBytes(encrypted);
        if (bytes.Length <= 0 || bytes.Length > MaxCiphertextLength)
            throw new InvalidOperationException("The setup emergency journal ciphertext is invalid.");

        var tempName = TempPrefix + Guid.NewGuid().ToString("N");
        var tempPath = Path.Combine(EmergencyDirectoryPath, tempName);
        try
        {
            await using (var stream = await OpenNewFileAsync(journalLock, tempName).ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
                ValidateOpenedFile(stream, expectedMode: 0x180, expectedLength: bytes.Length);
            }

            RenameIntoPlace(journalLock, tempName, tempPath);
            FsyncDirectory(journalLock);
        }
        finally
        {
            TryDeleteTemp(journalLock, tempName, tempPath);
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<OpenedJournal?> OpenAndReadCoreAsync(
        JournalLock journalLock,
        ConfigManager configManager,
        CancellationToken cancellationToken)
    {
        var opened = await OpenExistingFileAsync(journalLock, cancellationToken).ConfigureAwait(false);
        if (opened is null)
            return null;

        try
        {
            var bytes = await ReadExactAsync(opened, cancellationToken).ConfigureAwait(false);
            try
            {
                var encrypted = StrictUtf8.GetString(bytes);
                if (!ConfigEncryptionService.IsEncryptedFormat(encrypted))
                    throw new InvalidOperationException("The setup emergency journal is not authenticated.");

                var (plaintext, usedOldKey, requiresFormatUpgrade) =
                    configManager.DecryptSetupValue(SetupConfigKeys.CandidateSessionToken, encrypted);
                if (StrictUtf8.GetByteCount(plaintext) > MaxPlaintextLength)
                    throw new InvalidOperationException("The setup emergency journal value is too large.");

                CandidateJournalPlaintext document;
                try
                {
                    document = JsonSerializer.Deserialize<CandidateJournalPlaintext>(plaintext, JsonOptions)
                               ?? throw new InvalidOperationException("The setup emergency journal is malformed.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("The setup emergency journal is malformed.", exception);
                }

                ValidateValue(document.Token, MaxTokenLength, nameof(document.Token));
                ValidateValue(document.OperationId, MaxOperationLength, nameof(document.OperationId));
                opened.Entry = new CandidateJournalEntry(
                    document.Token,
                    document.OperationId,
                    usedOldKey,
                    requiresFormatUpgrade);
                opened.UsedOldKey = usedOldKey;
                opened.RequiresFormatUpgrade = requiresFormatUpgrade;
                return opened;
            }
            catch (CryptographicException exception)
            {
                throw new InvalidOperationException(
                    "The setup emergency journal authentication failed.",
                    exception);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch
        {
            await opened.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<byte[]> ReadExactAsync(
        OpenedFile opened,
        CancellationToken cancellationToken)
    {
        var length = opened.Length;
        if (length <= 0 || length > MaxCiphertextLength)
            throw new InvalidOperationException("The setup emergency journal is malformed.");

        var bytes = new byte[length];
        var read = 0;
        while (read < bytes.Length)
        {
            var count = await opened.Stream.ReadAsync(bytes.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                CryptographicOperations.ZeroMemory(bytes);
                throw new InvalidOperationException("The setup emergency journal is truncated.");
            }
            read += count;
        }

        if (opened.Stream.ReadByte() != -1)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new InvalidOperationException("The setup emergency journal is too large.");
        }

        ValidateOpenedFile(opened.Stream, expectedMode: 0x180, expectedLength: length);
        return bytes;
    }

    private static async Task<OpenedJournal?> OpenExistingFileAsync(
        JournalLock journalLock,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OperatingSystem.IsLinux())
        {
            var handle = Native.OpenAt(
                journalLock.DirectoryHandle,
                FileName,
                O_RDONLY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK,
                0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                if (error == 2)
                    return null;
                throw OpenFailure("The setup emergency journal could not be opened.", error);
            }

            var metadata = ValidateLinuxHandle(handle, expectedMode: 0x180, expectedRegular: true);
            var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            return new OpenedJournal(stream, metadata.Size, metadata);
        }

        var windowsHandle = Native.CreateFile(
            Path.Combine(EmergencyDirectoryPath, FileName),
            GenericRead | DeleteAccess,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagOverlapped,
            0);
        if (windowsHandle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            windowsHandle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return null;
            throw OpenFailure("The setup emergency journal could not be opened.", error);
        }

        ValidateWindowsHandle(windowsHandle, expectedRegular: true);
        var windowsStream = new FileStream(windowsHandle, FileAccess.Read, 4096, isAsync: true);
        var length = windowsStream.Length;
        return new OpenedJournal(windowsStream, length, null);
    }

    private static async Task<FileStream> OpenNewFileAsync(JournalLock journalLock, string name)
    {
        if (OperatingSystem.IsLinux())
        {
            var handle = Native.OpenAt(
                journalLock.DirectoryHandle,
                name,
                O_WRONLY | O_CREAT | O_EXCL | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK | O_SYNC,
                0x180);
            if (handle.IsInvalid)
                throw OpenFailure("The setup emergency journal could not be created.", Marshal.GetLastWin32Error());

            ValidateLinuxHandle(handle, expectedMode: 0x180, expectedRegular: true);
            return new FileStream(handle, FileAccess.Write, 4096, isAsync: false);
        }

        var path = Path.Combine(EmergencyDirectoryPath, name);
        var handleWindows = Native.CreateFile(
            path,
            GenericWrite,
            0,
            0,
            CreateNew,
            FileFlagOpenReparsePoint | FileFlagWriteThrough | FileFlagOverlapped,
            0);
        if (handleWindows.IsInvalid)
            throw OpenFailure("The setup emergency journal could not be created.", Marshal.GetLastWin32Error());

        ValidateWindowsHandle(handleWindows, expectedRegular: true);
        return new FileStream(handleWindows, FileAccess.Write, 4096, isAsync: true);
    }

    private static async Task<JournalLock> AcquireLockAsync(CancellationToken cancellationToken)
    {
        var directory = EnsureEmergencyDirectory();
        if (OperatingSystem.IsLinux())
        {
            var directoryHandle = Native.OpenDirectory(directory);
            if (directoryHandle.IsInvalid)
                throw OpenFailure("The setup emergency journal directory could not be opened.", Marshal.GetLastWin32Error());

            try
            {
                ValidateLinuxHandle(directoryHandle, expectedMode: 0x1c0, expectedRegular: false);
                var lockHandle = Native.OpenAt(
                    directoryHandle,
                    LockFileName,
                    O_RDWR | O_CREAT | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK,
                    0x180);
                if (lockHandle.IsInvalid)
                    throw OpenFailure("The setup emergency journal lock could not be opened.", Marshal.GetLastWin32Error());

                ValidateLinuxHandle(lockHandle, expectedMode: 0x180, expectedRegular: true);
                while (Native.flock(lockHandle.DangerousGetHandle().ToInt32(), LOCK_EX | LOCK_NB) != 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is not 11 and not 4)
                        throw OpenFailure("The setup emergency journal lock could not be acquired.", error);
                    await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
                }

                return new JournalLock(directoryHandle, lockHandle, directory, unix: true);
            }
            catch
            {
                directoryHandle.Dispose();
                throw;
            }
        }

        var directoryHandleWindows = Native.OpenWindowsDirectory(directory);
        if (directoryHandleWindows.IsInvalid)
            throw OpenFailure("The setup emergency journal directory could not be opened.", Marshal.GetLastWin32Error());

        try
        {
            ValidateWindowsDirectoryHandle(directoryHandleWindows);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lockPath = Path.Combine(directory, LockFileName);
                var lockHandle = Native.CreateFile(
                    lockPath,
                    GenericRead | GenericWrite,
                    0,
                    0,
                    OpenAlways,
                    FileFlagOpenReparsePoint | FileFlagOverlapped,
                    0);
                if (!lockHandle.IsInvalid)
                {
                    ValidateWindowsHandle(lockHandle, expectedRegular: true);
                    return new JournalLock(directoryHandleWindows, lockHandle, directory, unix: false);
                }

                var error = Marshal.GetLastWin32Error();
                lockHandle.Dispose();
                if (error is not ErrorSharingViolation and not ErrorLockViolation)
                    throw OpenFailure("The setup emergency journal lock could not be acquired.", error);
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            directoryHandleWindows.Dispose();
            throw;
        }
    }

    private static string EnsureEmergencyDirectory()
    {
        var directory = EmergencyDirectoryPath;
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static void RetireOpenedFile(JournalLock journalLock, FileStream stream)
    {
        if (journalLock.Unix)
        {
            if (Native.UnlinkAt(journalLock.DirectoryHandle, FileName) != 0)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 2)
                    return;
                throw OpenFailure("The setup emergency journal could not be retired.", error);
            }
        }
        else if (!Native.DeleteByHandle(stream.SafeFileHandle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
                return;
            throw OpenFailure("The setup emergency journal could not be retired.", error);
        }

        FsyncDirectory(journalLock);
    }

    private static void RenameIntoPlace(JournalLock journalLock, string tempName, string tempPath)
    {
        if (journalLock.Unix)
        {
            if (Native.RenameAt(journalLock.DirectoryHandle, tempName, journalLock.DirectoryHandle, FileName) != 0)
                throw OpenFailure("The setup emergency journal could not be installed.", Marshal.GetLastWin32Error());
            return;
        }

        if (!Native.MoveFileEx(
                tempPath,
                Path.Combine(journalLock.DirectoryPath, FileName),
                MoveFileReplaceExisting | MoveFileWriteThrough))
            throw OpenFailure("The setup emergency journal could not be installed.", Marshal.GetLastWin32Error());
    }

    private static void TryDeleteTemp(JournalLock journalLock, string name, string path)
    {
        try
        {
            if (journalLock.Unix)
            {
                _ = Native.UnlinkAt(journalLock.DirectoryHandle, name);
            }
            else
            {
                _ = Native.DeleteFile(path);
            }
        }
        catch
        {
            // A stale private temporary file is harmless; never replace an
            // operation's durable outcome with cleanup diagnostics.
        }
    }

    private static void FsyncDirectory(JournalLock journalLock)
    {
        if (!journalLock.Unix)
            return;
        if (Native.fsync(journalLock.DirectoryHandle.DangerousGetHandle().ToInt32()) != 0)
            throw OpenFailure("The setup emergency journal directory could not be synchronized.", Marshal.GetLastWin32Error());
    }

    private static void ValidateOpenedFile(FileStream stream, int expectedMode, long expectedLength)
    {
        if (OperatingSystem.IsLinux())
        {
            var metadata = ValidateLinuxHandle(stream.SafeFileHandle, expectedMode, expectedRegular: true);
            if (metadata.Size != expectedLength)
                throw new InvalidOperationException("The setup emergency journal length changed while it was open.");
        }
        else
        {
            ValidateWindowsOpenedFile(stream, expectedLength);
        }
    }

    private static void ValidateWindowsOpenedFile(FileStream stream, long expectedLength)
    {
        ValidateWindowsHandle(stream.SafeFileHandle, expectedRegular: true);
        if (stream.Length != expectedLength)
            throw new InvalidOperationException("The setup emergency journal length changed while it was open.");
    }

    [SupportedOSPlatform("linux")]
    private static UnixMetadata ValidateLinuxHandle(
        SafeFileHandle handle,
        int expectedMode,
        bool expectedRegular)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (Native.fstat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
                throw OpenFailure("The setup emergency journal handle could not be inspected.", Marshal.GetLastWin32Error());

            // Linux x86-64 and arm64 have different stat layouts. These are
            // the stable fields in the musl/glibc layouts used by our images.
            var arm64 = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
            var nlinkOffset = arm64 ? 20 : 16;
            var modeOffset = arm64 ? 16 : 24;
            var uidOffset = arm64 ? 24 : 28;
            var sizeOffset = arm64 ? 40 : 48;
            var inodeOffset = 8;
            var deviceOffset = 0;
            var mode = unchecked((uint)Marshal.ReadInt32(buffer, modeOffset));
            var nlink = unchecked((uint)Marshal.ReadInt32(buffer, nlinkOffset));
            var uid = unchecked((uint)Marshal.ReadInt32(buffer, uidOffset));
            var size = Marshal.ReadInt64(buffer, sizeOffset);
            var metadata = new UnixMetadata(
                unchecked((ulong)Marshal.ReadInt64(buffer, deviceOffset)),
                unchecked((ulong)Marshal.ReadInt64(buffer, inodeOffset)),
                mode,
                nlink,
                uid,
                size);

            if (expectedRegular && (mode & 0xF000) != 0x8000)
                throw new IOException("The setup emergency journal is not a regular file.");
            if (!expectedRegular && (mode & 0xF000) != 0x4000)
                throw new IOException("The setup emergency journal directory is not a directory.");
            if ((expectedRegular && nlink != 1) || (!expectedRegular && nlink < 2))
                throw new IOException("The setup emergency journal has an unexpected link count.");
            if (uid != Native.geteuid())
                throw new IOException("The setup emergency journal has an unexpected owner.");
            if ((mode & 0x1FF) != expectedMode)
                throw new IOException("The setup emergency journal has an unexpected mode.");
            if (size < 0)
                throw new IOException("The setup emergency journal has an invalid length.");
            return metadata;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void ValidateWindowsDirectoryHandle(SafeFileHandle handle)
    {
        if (Native.GetFileType(handle.DangerousGetHandle()) != FileTypeDisk)
            throw new IOException("The setup emergency journal directory is not on a disk.");
        var information = GetWindowsInformation(handle);
        if ((information.FileAttributes & FileAttributeDirectory) == 0
            || (information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException("The setup emergency journal directory is not a normal directory.");
    }

    private static void ValidateWindowsHandle(SafeFileHandle handle, bool expectedRegular)
    {
        if (Native.GetFileType(handle.DangerousGetHandle()) != FileTypeDisk)
            throw new IOException("The setup emergency journal is not a disk file.");
        var information = GetWindowsInformation(handle);
        if (!expectedRegular && (information.FileAttributes & FileAttributeDirectory) == 0)
            throw new IOException("The setup emergency journal handle is not a directory.");
        if (expectedRegular && (information.FileAttributes & FileAttributeDirectory) != 0)
            throw new IOException("The setup emergency journal is not a regular file.");
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException("The setup emergency journal is not a regular file.");
        if (information.NumberOfLinks != 1)
            throw new IOException("The setup emergency journal must have exactly one link.");
    }

    private static Native.ByHandleFileInformation GetWindowsInformation(SafeFileHandle handle)
    {
        if (!Native.GetFileInformationByHandle(handle.DangerousGetHandle(), out var information))
            throw OpenFailure("The setup emergency journal handle could not be inspected.", Marshal.GetLastWin32Error());
        return information;
    }

    private static bool ConstantTimeEquals(string left, string right)
    {
        var leftBytes = StrictUtf8.GetBytes(left);
        var rightBytes = StrictUtf8.GetBytes(right);
        var leftDigest = SHA256.HashData(leftBytes);
        var rightDigest = SHA256.HashData(rightBytes);
        try
        {
            return CryptographicOperations.FixedTimeEquals(leftDigest, rightDigest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
            CryptographicOperations.ZeroMemory(leftDigest);
            CryptographicOperations.ZeroMemory(rightDigest);
        }
    }

    private static void ValidateValue(string value, int maxLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new InvalidOperationException($"The setup emergency journal {name} is invalid.");
    }

    private static bool IsWithinDirectory(string path, string directory, bool allowDirectoryName = false)
    {
        var root = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                   + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal)
               && (allowDirectoryName || string.Equals(Path.GetFileName(path), FileName, StringComparison.Ordinal));
    }

    private static Exception OpenFailure(string message, int error)
        => new IOException(message, new System.ComponentModel.Win32Exception(error));

    private sealed record CandidateJournalPlaintext(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("operationId")] string OperationId);

    private class OpenedFile(FileStream stream, long length, UnixMetadata? unixMetadata) : IAsyncDisposable
    {
        internal FileStream Stream { get; } = stream;
        internal long Length { get; } = length;
        internal UnixMetadata? UnixMetadata { get; } = unixMetadata;

        public ValueTask DisposeAsync() => Stream.DisposeAsync();
    }

    private sealed class OpenedJournal(FileStream stream, long length, UnixMetadata? unixMetadata)
        : OpenedFile(stream, length, unixMetadata)
    {
        internal CandidateJournalEntry Entry { get; set; } = null!;
        internal bool UsedOldKey { get; set; }
        internal bool RequiresFormatUpgrade { get; set; }
    }

    private sealed class JournalLock(
        SafeFileHandle directoryHandle,
        SafeFileHandle lockHandle,
        string directoryPath,
        bool unix) : IAsyncDisposable
    {
        internal SafeFileHandle DirectoryHandle { get; } = directoryHandle;
        internal string DirectoryPath { get; } = directoryPath;
        internal bool Unix { get; } = unix;
        // Linux's open(2) handle is synchronous; the Windows lock handle is
        // opened with FILE_FLAG_OVERLAPPED. Match FileStream to each handle.
        private FileStream LockStream { get; } = new(lockHandle, FileAccess.ReadWrite, 4096, isAsync: !unix);

        public async ValueTask DisposeAsync()
        {
            if (Unix)
                _ = Native.flock(LockStream.SafeFileHandle.DangerousGetHandle().ToInt32(), LOCK_UN);
            await LockStream.DisposeAsync().ConfigureAwait(false);
            DirectoryHandle.Dispose();
        }
    }

    private readonly record struct UnixMetadata(
        ulong Device,
        ulong Inode,
        uint Mode,
        uint NumberOfLinks,
        uint UserId,
        long Size);

    private static class Native
    {
        [DllImport("libc", EntryPoint = "open", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern SafeFileHandle open(string path, int flags, int mode);

        [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern SafeFileHandle openat(SafeFileHandle directory, string path, int flags, int mode);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        internal static extern int fstat(int fd, nint statBuffer);

        [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
        internal static extern int flock(int fd, int operation);

        [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
        internal static extern int fsync(int fd);

        [DllImport("libc", EntryPoint = "unlinkat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int unlinkat(SafeFileHandle directory, string path, int flags = 0);

        [DllImport("libc", EntryPoint = "renameat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int renameat(
            SafeFileHandle oldDirectory,
            string oldPath,
            SafeFileHandle newDirectory,
            string newPath);

        [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
        internal static extern uint geteuid();

        internal static SafeFileHandle OpenDirectory(string path)
            => open(path, O_RDONLY | O_DIRECTORY | O_CLOEXEC | O_NOFOLLOW | O_NONBLOCK, 0);

        internal static SafeFileHandle OpenAt(SafeFileHandle directory, string path, int flags, int mode)
            => openat(directory, path, flags, mode);

        internal static int UnlinkAt(SafeFileHandle directory, string path)
            => unlinkat(directory, path);

        internal static int RenameAt(
            SafeFileHandle oldDirectory,
            string oldPath,
            SafeFileHandle newDirectory,
            string newPath)
            => renameat(oldDirectory, oldPath, newDirectory, newPath);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            nint securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            nint templateFile);

        [DllImport("kernel32.dll", EntryPoint = "GetFileType", SetLastError = true)]
        internal static extern uint GetFileType(nint fileHandle);

        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(
            nint fileHandle,
            out ByHandleFileInformation information);

        [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool MoveFileEx(string existing, string replacement, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "DeleteFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeleteFile(string path);

        [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            nint fileHandle,
            int fileInformationClass,
            ref FileDispositionInformation information,
            int bufferSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileDispositionInformation
        {
            internal byte DeleteFile;
        }

        internal static bool DeleteByHandle(SafeFileHandle handle)
        {
            var information = new FileDispositionInformation { DeleteFile = 1 };
            return SetFileInformationByHandle(
                handle.DangerousGetHandle(),
                fileInformationClass: 4,
                ref information,
                Marshal.SizeOf<FileDispositionInformation>());
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct ByHandleFileInformation
        {
            internal uint FileAttributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
            internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
            internal uint VolumeSerialNumber;
            internal uint FileSizeHigh;
            internal uint FileSizeLow;
            internal uint NumberOfLinks;
            internal uint FileIndexHigh;
            internal uint FileIndexLow;
        }

        internal static SafeFileHandle OpenWindowsDirectory(string path)
            => CreateFile(
                path,
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint,
                0);
    }
}

internal sealed record CandidateJournalEntry(
    string Token,
    string OperationId,
    bool UsedOldKey = false,
    bool RequiresFormatUpgrade = false);

internal sealed class SetupPersistenceTransaction(IDbContextTransaction transaction)
{
    private IDbContextTransaction Transaction { get; } = transaction ?? throw new ArgumentNullException(nameof(transaction));
    internal SetupTransactionState State { get; private set; } = SetupTransactionState.Active;
    internal bool CommitAttempted { get; private set; }
    private bool IsDisposed { get; set; }

    internal async Task CommitAsync(CancellationToken cancellationToken)
    {
        CommitAttempted = true;
        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        State = SetupTransactionState.Committed;
    }

    internal async Task<Exception?> TryRollbackAsync()
    {
        if (IsDisposed || State is SetupTransactionState.Committed or SetupTransactionState.RolledBack)
            return null;
        try
        {
            await Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            State = SetupTransactionState.RolledBack;
            return null;
        }
        catch (Exception exception)
        {
            State = SetupTransactionState.Unknown;
            return exception;
        }
    }

    internal async Task<Exception?> TryDisposeAsync()
    {
        if (IsDisposed)
            return null;
        try
        {
            await Transaction.DisposeAsync().ConfigureAwait(false);
            IsDisposed = true;
            return null;
        }
        catch (Exception exception)
        {
            State = SetupTransactionState.Unknown;
            return exception;
        }
    }
}
