using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using Microsoft.Win32.SafeHandles;

namespace NzbWebDAV.Setup.Discovery;

/// <summary>
/// Reads an Arr service's existing config.xml without modifying the filesystem.
/// The paths are fixed when this instance is constructed.
/// </summary>
public sealed class ArrApiKeyDiscovery
{
    private const string ExpectedFileName = "config.xml";
    private const string ExpectedRootElement = "Config";
    private const string ExpectedApiKeyElement = "ApiKey";
    private const int MaxXmlDepth = 64;
    private readonly ArrConfigDiscoveryOptions _options;

    /// <summary>
    /// Creates discovery using the fixed bootstrap locations for the three Arr
    /// services. Paths are not part of the production API.
    /// </summary>
    public ArrApiKeyDiscovery()
        : this(ArrConfigDiscoveryOptions.Production)
    {
    }

    // Kept internal so tests can exercise filesystem races and errno behavior
    // without making arbitrary path selection a public capability.
    internal ArrApiKeyDiscovery(ArrConfigDiscoveryOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<ArrApiKeyDiscoveryResult> DiscoverAsync(
        ArrService service,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Enum.IsDefined(service))
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidOptions);

        if (_options.Paths is null ||
            _options.MaxFileBytes <= 0 ||
            _options.MaxFileBytes > ArrConfigDiscoveryOptions.AbsoluteMaxFileBytes)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidOptions);
        }

        // The public production instance has no Windows path interpretation:
        // its Linux-only bootstrap paths are never probed or opened.
        if (OperatingSystem.IsWindows() &&
            _options.TestOpenedFileFactory is null &&
            ReferenceEquals(_options.Paths, ArrConfigDiscoveryOptions.Production.Paths))
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.PlatformNotSupported);
        }

        string path;
        try
        {
            path = _options.Paths.GetPath(service);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidOptions);
        }

        if (!IsExpectedConfigPath(path))
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidPath);

        var contents = await ReadBoundedAsync(path, service, cancellationToken).ConfigureAwait(false);
        return await ParseApiKeyAsync(contents.Buffer, contents.Length, service, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<(byte[] Buffer, int Length)> ReadBoundedAsync(
        string path,
        ArrService service,
        CancellationToken cancellationToken)
    {
        try
        {
            // Parser tests may supply a handle that they opened explicitly.
            // This is intentionally checked before platform selection and is
            // not a path-opening capability.
            if (_options.TestOpenedFileFactory is not null)
            {
                var testStream = _options.TestOpenedFileFactory(service)
                    ?? throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
                return await ReadBoundedFromStreamAsync(testStream, service, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!OperatingSystem.IsLinux())
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.PlatformNotSupported);

            var opened = OpenLinux(path, service);
            try
            {
                var streamFromHandle = new FileStream(
                    opened.File!,
                    FileAccess.Read,
                    bufferSize: 1,
                    isAsync: false);
                opened.DetachFile(); // ownership is now held by FileStream
                return await ReadBoundedFromStreamAsync(streamFromHandle, service, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                opened.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArrConfigDiscoveryException)
        {
            throw;
        }
        catch (FileNotFoundException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Missing);
        }
        catch (DirectoryNotFoundException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Missing);
        }
        catch (UnauthorizedAccessException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
        }
        catch (ArgumentException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidPath);
        }
        catch (NotSupportedException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidPath);
        }
        catch (IOException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
        }
    }

    private async Task<(byte[] Buffer, int Length)> ReadBoundedFromStreamAsync(
        Stream stream,
        ArrService service,
        CancellationToken cancellationToken)
    {
        await using (stream.ConfigureAwait(false))
        {
            var maxBytes = checked((int)_options.MaxFileBytes);
            var initialLength = stream.Length;
            if (initialLength < 0 || initialLength > _options.MaxFileBytes)
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.TooLarge);
            _options.AfterInitialLengthObserved?.Invoke();

            // There is exactly one payload buffer, and it is only max+1 bytes.
            // Every read is limited to the remaining portion of that buffer.
            var contents = new byte[checked(maxBytes + 1)];
            var total = 0;
            while (total < contents.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(
                        contents.AsMemory(total, contents.Length - total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;

                total += read;
                if (total > maxBytes)
                    throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.TooLarge);
            }

            // A growth that happened after the initial size check is caught
            // without reading anything beyond the max+1 budget.
            if (stream.Length > _options.MaxFileBytes)
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.TooLarge);

            return (contents, total);
        }
    }

    private async Task<ArrApiKeyDiscoveryResult> ParseApiKeyAsync(
        byte[] contents,
        int contentLength,
        ArrService service,
        CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            Async = true,
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = Math.Max(1, contentLength),
            MaxCharactersFromEntities = 0,
            IgnoreComments = false,
            IgnoreProcessingInstructions = false,
            IgnoreWhitespace = false
        };

        try
        {
            await using var stream = new MemoryStream(contents, 0, contentLength, writable: false, publiclyVisible: true);
            using var reader = XmlReader.Create(stream, settings);
            var rootSeen = false;
            var apiKeyCount = 0;
            var inApiKey = false;
            var apiKeyText = new StringBuilder();
            var openElements = new Stack<string>();
            _options.ParseStarted?.Invoke();

            while (await reader.ReadAsync().ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.Depth > MaxXmlDepth)
                    throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);

                if (reader.NodeType == XmlNodeType.Element)
                {
                    // Every element is policy data, not just Config and
                    // ApiKey. Reject ordinary attributes as well as namespace
                    // declarations before interpreting the element.
                    if (reader.AttributeCount != 0 ||
                        !string.IsNullOrEmpty(reader.NamespaceURI) ||
                        !string.IsNullOrEmpty(reader.Prefix) ||
                        HasNamespaceDeclaration(reader))
                        throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);

                    if (reader.Depth == 0)
                    {
                        if (rootSeen || !IsExactElement(reader, ExpectedRootElement))
                            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);

                        rootSeen = true;
                        if (!reader.IsEmptyElement)
                            openElements.Push(reader.Name);
                        continue;
                    }

                    if (IsExactElement(reader, ExpectedApiKeyElement))
                    {
                        // An ApiKey anywhere other than directly below Config,
                        // or more than one direct child, is never a substitute.
                        if (reader.Depth != 1 || ++apiKeyCount != 1 || inApiKey)
                            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);

                        inApiKey = !reader.IsEmptyElement;
                        if (inApiKey)
                            openElements.Push(reader.Name);
                    }
                    else if (inApiKey)
                    {
                        // Do not flatten a nested element into the secret.
                        throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
                    }
                    else if (!reader.IsEmptyElement)
                    {
                        // Arr config files contain scalar sibling elements;
                        // keep their text in the XML structure without ever
                        // treating it as the API key.
                        openElements.Push(reader.Name);
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (openElements.Count == 0)
                        throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);

                    var closing = openElements.Pop();
                    if (inApiKey)
                    {
                        if (reader.Depth != 1 || !string.Equals(closing, ExpectedApiKeyElement, StringComparison.Ordinal))
                            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
                        inApiKey = false;
                    }
                }
                else if (inApiKey)
                {
                    if (reader.NodeType is XmlNodeType.Text or
                        XmlNodeType.CDATA or
                        XmlNodeType.Whitespace or
                        XmlNodeType.SignificantWhitespace)
                    {
                        apiKeyText.Append(reader.Value);
                    }
                    else
                    {
                        // Comments, processing instructions, and entity
                        // declarations are not content of a direct ApiKey.
                        throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
                    }
                }
                else if ((reader.NodeType is XmlNodeType.Text or
                          XmlNodeType.CDATA or
                          XmlNodeType.Whitespace or
                          XmlNodeType.SignificantWhitespace) &&
                         !string.IsNullOrWhiteSpace(reader.Value) &&
                         openElements.Count <= 1)
                {
                    // Non-whitespace text directly in Config is not a scalar
                    // sibling; text inside a sibling element is allowed.
                    throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
                }
            }

            if (!rootSeen || inApiKey)
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
            if (apiKeyCount == 0)
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.MissingApiKey);

            var apiKey = apiKeyText.ToString();
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.EmptyApiKey);

            return new ArrApiKeyDiscoveryResult(service, apiKey.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArrConfigDiscoveryException)
        {
            throw;
        }
        catch (XmlException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
        }
        catch (ArgumentException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
        }
        catch (InvalidOperationException)
        {
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Malformed);
        }
    }

    private static bool IsExactElement(XmlReader reader, string expectedName) =>
        string.Equals(reader.Name, expectedName, StringComparison.Ordinal) &&
        string.IsNullOrEmpty(reader.NamespaceURI);

    private static bool HasNamespaceDeclaration(XmlReader reader)
    {
        for (var i = 0; i < reader.AttributeCount; i++)
        {
            reader.MoveToAttribute(i);
            if (string.Equals(reader.Prefix, "xmlns", StringComparison.Ordinal) ||
                string.Equals(reader.Name, "xmlns", StringComparison.Ordinal) ||
                !string.IsNullOrEmpty(reader.NamespaceURI))
            {
                reader.MoveToElement();
                return true;
            }
        }

        reader.MoveToElement();
        return false;
    }

    private sealed class OpenedFile : IDisposable
    {
        private readonly List<SafeFileHandle> _componentHandles;
        internal SafeFileHandle? File { get; private set; }

        internal OpenedFile(SafeFileHandle file, List<SafeFileHandle> componentHandles)
        {
            File = file;
            _componentHandles = componentHandles;
        }

        internal void DetachFile() => File = null;

        public void Dispose()
        {
            File?.Dispose();
            File = null;
            for (var i = _componentHandles.Count - 1; i >= 0; i--)
                _componentHandles[i].Dispose();
            _componentHandles.Clear();
        }
    }

    private OpenedFile OpenLinux(string path, ArrService service)
    {
        // Resolve one component at a time. In particular, do not let a
        // replaced bootstrap or service directory redirect the final open.
        var components = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if ((path.Length == 0 || path[0] != '/') || components.Length < 3 ||
            components[^1] != ExpectedFileName || components.Any(c => c is "." or ".."))
            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.InvalidPath);

        List<SafeFileHandle>? componentHandles = [];
        SafeFileHandle? anchor = null;
        try
        {
            var directory = OpenLinuxAt(NativeLinux.AT_FDCWD, "/", NativeLinux.DirectoryFlags, service);
            componentHandles.Add(directory);
            for (var i = 0; i < components.Length - 1; i++)
            {
                var next = OpenLinuxAt(GetFd(directory), components[i], NativeLinux.DirectoryFlags, service);
                componentHandles.Add(next);
                directory = next;
            }

            _options.BeforeFinalOpen?.Invoke();

            // First obtain an O_PATH descriptor without following the final
            // component. fstat happens before any readable open, so FIFOs,
            // devices and directories can never invoke a device handler.
            anchor = OpenLinuxAt(GetFd(directory), components[^1], NativeLinux.AnchorFlags, service);
            if (!NativeLinux.TryGetFileIdentity(anchor, out var anchored) ||
                !NativeLinux.IsRegular(anchored.Mode) ||
                anchored.Size < 0)
            {
                anchor.Dispose();
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
            }

            if (anchored.Size > ArrConfigDiscoveryOptions.AbsoluteMaxFileBytes)
            {
                anchor.Dispose();
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.TooLarge);
            }

            // Reopen the already-anchored object through procfs, never through
            // the attacker-controlled final path. Compare identity and mode
            // again after the readable open to catch ABI/descriptor mistakes.
            var file = NativeLinux.OpenProcFd(GetFd(anchor), NativeLinux.ReadableFlags, service);
            if (!NativeLinux.TryGetFileIdentity(file, out var reopened) ||
                !NativeLinux.IsRegular(reopened.Mode) ||
                reopened.Size < 0 ||
                reopened.Device != anchored.Device ||
                reopened.Inode != anchored.Inode ||
                reopened.Mode != anchored.Mode)
            {
                file.Dispose();
                anchor.Dispose();
                throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
            }

            anchor.Dispose();
            anchor = null;
            var result = new OpenedFile(file, componentHandles);
            componentHandles = null;
            return result;
        }
        finally
        {
            anchor?.Dispose();
            if (componentHandles is not null)
            {
                for (var i = componentHandles.Count - 1; i >= 0; i--)
                    componentHandles[i].Dispose();
            }
        }
    }

    private static SafeFileHandle OpenLinuxAt(int directoryFd, string component, int flags, ArrService service)
    {
        int fd;
        do
        {
            fd = NativeLinux.OpenAt(directoryFd, component, flags);
        }
        while (fd < 0 && Marshal.GetLastWin32Error() == NativeLinux.EINTR);

        if (fd >= 0)
            return new SafeFileHandle((IntPtr)fd, ownsHandle: true);

        var error = Marshal.GetLastWin32Error();
        var isSymlinkParent = error == NativeLinux.ENOTDIR &&
            (flags & NativeLinux.O_DIRECTORY) != 0 &&
            NativeLinux.IsSymbolicLinkAt(directoryFd, component);
        var failure = error == NativeLinux.ENOENT ||
                      (error == NativeLinux.ENOTDIR && !isSymlinkParent)
            ? ArrConfigDiscoveryFailure.Missing
            : ArrConfigDiscoveryFailure.Unreadable;
        throw new ArrConfigDiscoveryException(service, failure);
    }

    private static int GetFd(SafeFileHandle handle) =>
        checked((int)handle.DangerousGetHandle().ToInt64());

    private static bool IsExpectedConfigPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return false;

        try
        {
            // Do not normalize, search, or derive a path: this is the exact
            // path supplied by trusted server configuration.
            return string.Equals(Path.GetFileName(path), ExpectedFileName, StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    // This is deliberately an explicit ABI seam rather than a byte buffer with
    // architecture-dependent magic offsets. The offsets are verified by tests
    // and match Alpine 3.22 musl/glibc: amd64 mode=24,size=48 and arm64
    // mode=16,size=48.
    internal static class NativeLinux
    {
        internal const int O_RDONLY = 0;
        internal const int O_NONBLOCK = 0x800;
        internal const int O_CLOEXEC = 0x80000;
        // O_NOFOLLOW is an ABI flag, not a portable constant: x86_64 uses
        // 0x20000 while arm64 uses 0x8000. Keep it selected at runtime for
        // every attacker-path openat call. The readable reopen uses procfs
        // for the already-anchored descriptor and does not traverse the path.
        internal static int O_NOFOLLOW => NoFollowFor(RuntimeInformation.ProcessArchitecture);
        // O_LARGEFILE is an x86_64-only ABI flag for musl/glibc large-offset files.
        internal static int O_LARGEFILEFor(Architecture architecture) => architecture switch
        {
            Architecture.X64 => 0x8000,
            _ => 0
        };
        internal static int O_DIRECTORY => DirectoryFor(RuntimeInformation.ProcessArchitecture);
        internal const int O_PATH = 0x200000;
        internal const int AT_FDCWD = -100;
        internal const int AT_SYMLINK_NOFOLLOW = 0x100;
        // O_RDONLY|O_DIRECTORY makes a symlink parent fail with ELOOP on
        // both x86_64 and arm64 (O_PATH may instead report ENOTDIR).
        internal static int DirectoryFlags => GetDirectoryFlags(RuntimeInformation.ProcessArchitecture);
        internal static int AnchorFlags => AnchorFlagsFor(RuntimeInformation.ProcessArchitecture);
        internal static int FileFlags => GetFileFlags(RuntimeInformation.ProcessArchitecture);
        internal static int AnchorFlagsFor(Architecture architecture) =>
            O_PATH | NoFollowFor(architecture) | O_CLOEXEC;
        internal static int ReadableFlags => O_RDONLY | O_CLOEXEC;

        internal static int NoFollowFor(Architecture architecture) => architecture switch
        {
            Architecture.Arm64 => 0x8000,
            Architecture.X64 or Architecture.X86 => 0x20000,
            _ => throw new PlatformNotSupportedException("Unsupported Linux architecture")
        };

        internal static int DirectoryFor(Architecture architecture) => architecture switch
        {
            Architecture.Arm64 => 0x4000,
            Architecture.X64 or Architecture.X86 => 0x10000,
            _ => throw new PlatformNotSupportedException("Unsupported Linux architecture")
        };

        internal static int GetDirectoryFlags(Architecture architecture) =>
            O_RDONLY | DirectoryFor(architecture) | NoFollowFor(architecture) | O_CLOEXEC;

        internal static int GetFileFlags(Architecture architecture) =>
            O_RDONLY | O_NONBLOCK | NoFollowFor(architecture) | O_CLOEXEC | O_LARGEFILEFor(architecture);

        internal readonly record struct FileIdentity(ulong Device, ulong Inode, uint Mode, long Size);

        internal static bool IsRegular(uint mode) => (mode & 0xF000) == 0x8000;
        internal const int EINTR = 4;
        internal const int ENOENT = 2;
        internal const int ENOTDIR = 20;
        internal const int ELOOP = 40;

        [StructLayout(LayoutKind.Sequential, Size = 144)]
        internal struct Amd64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal ulong LinkCount;
            internal uint Mode;
            internal uint UserId;
            internal uint GroupId;
            internal uint Padding;
            internal ulong DeviceType;
            internal long Size;
        }

        [StructLayout(LayoutKind.Sequential, Size = 128)]
        internal struct Arm64Stat
        {
            internal ulong Device;
            internal ulong Inode;
            internal uint Mode;
            internal uint LinkCount;
            internal uint UserId;
            internal uint GroupId;
            internal ulong DeviceType;
            internal ulong PaddingBeforeSize;
            internal long Size;
        }

        [DllImport("libc", EntryPoint = "openat", SetLastError = true, CharSet = CharSet.Ansi)]
        internal static extern int OpenAt(int directoryFd, string path, int flags);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStatAmd64(int fd, out Amd64Stat stat);

        [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
        private static extern int FStatArm64(int fd, out Arm64Stat stat);

        [DllImport("libc", EntryPoint = "fstatat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int FStatAtAmd64(int directoryFd, string path, out Amd64Stat stat, int flags);

        [DllImport("libc", EntryPoint = "fstatat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int FStatAtArm64(int directoryFd, string path, out Arm64Stat stat, int flags);

        internal static bool IsSymbolicLinkAt(int directoryFd, string path)
        {
            uint mode;
            int result;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                {
                    Amd64Stat stat;
                    do
                    {
                        result = FStatAtAmd64(directoryFd, path, out stat, AT_SYMLINK_NOFOLLOW);
                    }
                    while (result != 0 && Marshal.GetLastWin32Error() == EINTR);
                    if (result != 0)
                        return false;
                    mode = stat.Mode;
                    break;
                }
                case Architecture.Arm64:
                {
                    Arm64Stat stat;
                    do
                    {
                        result = FStatAtArm64(directoryFd, path, out stat, AT_SYMLINK_NOFOLLOW);
                    }
                    while (result != 0 && Marshal.GetLastWin32Error() == EINTR);
                    if (result != 0)
                        return false;
                    mode = stat.Mode;
                    break;
                }
                default:
                    return false;
            }

            return (mode & 0xF000) == 0xA000;
        }

        internal static bool TryGetRegularFileSize(SafeFileHandle handle, out long size)
        {
            size = 0;
            return TryGetFileIdentity(handle, out var identity) &&
                IsRegular(identity.Mode) && (size = identity.Size) >= 0;
        }

        internal static bool TryGetFileIdentity(SafeFileHandle handle, out FileIdentity identity)
        {
            identity = default;
            if (handle.IsInvalid)
                return false;

            var fd = checked((int)handle.DangerousGetHandle().ToInt64());
            uint mode;
            ulong device;
            ulong inode;
            long size;
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                {
                    int result;
                    Amd64Stat amd64;
                    do
                    {
                        result = FStatAmd64(fd, out amd64);
                    }
                    while (result != 0 && Marshal.GetLastWin32Error() == EINTR);
                    if (result != 0)
                        return false;
                    mode = amd64.Mode;
                    device = amd64.Device;
                    inode = amd64.Inode;
                    size = amd64.Size;
                    break;
                }
                case Architecture.Arm64:
                {
                    int result;
                    Arm64Stat arm64;
                    do
                    {
                        result = FStatArm64(fd, out arm64);
                    }
                    while (result != 0 && Marshal.GetLastWin32Error() == EINTR);
                    if (result != 0)
                        return false;
                    mode = arm64.Mode;
                    device = arm64.Device;
                    inode = arm64.Inode;
                    size = arm64.Size;
                    break;
                }
                default:
                    return false;
            }

            identity = new FileIdentity(device, inode, mode, size);
            return true;
        }

        internal static SafeFileHandle OpenProcFd(int fd, int flags, ArrService service)
        {
            var path = $"/proc/self/fd/{fd}";
            int result;
            do
            {
                result = OpenAt(AT_FDCWD, path, flags);
            }
            while (result < 0 && Marshal.GetLastWin32Error() == EINTR);

            if (result >= 0)
                return new SafeFileHandle((IntPtr)result, ownsHandle: true);

            throw new ArrConfigDiscoveryException(service, ArrConfigDiscoveryFailure.Unreadable);
        }
    }
}
