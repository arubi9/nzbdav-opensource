using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NzbWebDAV.Setup.Discovery;

namespace NzbWebDAV.Tests.Setup.Discovery;

public sealed class ArrDiscoveryInteropTests
{
    [Fact]
    public void LinuxStatAbiHasTheVerifiedAlpineOffsets()
    {
        Assert.Equal(24, Marshal.OffsetOf<ArrApiKeyDiscovery.NativeLinux.Amd64Stat>(nameof(ArrApiKeyDiscovery.NativeLinux.Amd64Stat.Mode)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ArrApiKeyDiscovery.NativeLinux.Amd64Stat>(nameof(ArrApiKeyDiscovery.NativeLinux.Amd64Stat.Size)).ToInt32());
        Assert.Equal(16, Marshal.OffsetOf<ArrApiKeyDiscovery.NativeLinux.Arm64Stat>(nameof(ArrApiKeyDiscovery.NativeLinux.Arm64Stat.Mode)).ToInt32());
        Assert.Equal(48, Marshal.OffsetOf<ArrApiKeyDiscovery.NativeLinux.Arm64Stat>(nameof(ArrApiKeyDiscovery.NativeLinux.Arm64Stat.Size)).ToInt32());
        Assert.Equal(144, Marshal.SizeOf<ArrApiKeyDiscovery.NativeLinux.Amd64Stat>());
        Assert.Equal(128, Marshal.SizeOf<ArrApiKeyDiscovery.NativeLinux.Arm64Stat>());
    }

    [Fact]
    public void LinuxOpenFlagsUseTheExactCombinedMasksOnBothSupportedArchitectures()
    {
        Assert.Equal(0x800, ArrApiKeyDiscovery.NativeLinux.O_NONBLOCK);
        Assert.Equal(0x80000, ArrApiKeyDiscovery.NativeLinux.O_CLOEXEC);
        Assert.Equal(0x200000, ArrApiKeyDiscovery.NativeLinux.O_PATH);

        Assert.Equal(0xB0000, ArrApiKeyDiscovery.NativeLinux.GetDirectoryFlags(Architecture.X64));
        Assert.Equal(0xA8800, ArrApiKeyDiscovery.NativeLinux.GetFileFlags(Architecture.X64));
        Assert.Equal(0x8C000, ArrApiKeyDiscovery.NativeLinux.GetDirectoryFlags(Architecture.Arm64));
        Assert.Equal(0x88800, ArrApiKeyDiscovery.NativeLinux.GetFileFlags(Architecture.Arm64));
        Assert.Equal(0x2A0000, ArrApiKeyDiscovery.NativeLinux.AnchorFlagsFor(Architecture.X64));
        Assert.Equal(0x288000, ArrApiKeyDiscovery.NativeLinux.AnchorFlagsFor(Architecture.Arm64));
        Assert.Equal(0x80000, ArrApiKeyDiscovery.NativeLinux.ReadableFlags);

        var architecture = RuntimeInformation.ProcessArchitecture;
        Assert.Equal(architecture == Architecture.Arm64 ? 0x8C000 : 0xB0000,
            ArrApiKeyDiscovery.NativeLinux.DirectoryFlags);
        Assert.Equal(architecture == Architecture.Arm64 ? 0x88800 : 0xA8800,
            ArrApiKeyDiscovery.NativeLinux.FileFlags);
    }

    [Fact]
    public void NativeMuslCompatibleCProbeCompilesExactFlagsAndRejectsSymlinks()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The native C probe requires Linux.");
        var directory = Path.Combine(Path.GetTempPath(), $"nzbdav-c-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "probe.c");
        var executable = Path.Combine(directory, "probe");
        File.WriteAllText(source, NativeProbeSource);
        try
        {
            var compile = StartProcess("cc", source, "-O2", "-o", executable);
            Assert.SkipWhen(compile is null, "No C compiler is installed for the native target probe.");
            compile!.WaitForExit();
            var compileDiagnostics = compile.StandardOutput.ReadToEnd() + compile.StandardError.ReadToEnd();
            Assert.True(compile.ExitCode == 0, compileDiagnostics);

            var run = StartProcess(executable);
            Assert.NotNull(run);
            run!.WaitForExit();
            Assert.True(run.ExitCode == 0, run.StandardOutput.ReadToEnd() + run.StandardError.ReadToEnd());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LinuxNativeProbeUsesArchitectureFlagAndFstatsTheOpenedHandle()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The native probe requires Linux.");
        var name = $"nzbdav-discovery-probe-{Guid.NewGuid():N}.xml";
        var path = Path.Combine("/tmp", name);
        File.WriteAllText(path, "1234567");
        SafeFileHandle? root = null;
        SafeFileHandle? parent = null;
        SafeFileHandle? file = null;
        try
        {
            root = OpenDirectory(ArrApiKeyDiscovery.NativeLinux.AT_FDCWD, "/");
            parent = OpenDirectory(GetFd(root), "tmp");
            var fd = ArrApiKeyDiscovery.NativeLinux.OpenAt(
                GetFd(parent), name, ArrApiKeyDiscovery.NativeLinux.FileFlags);
            Assert.True(fd >= 0, $"openat failed with errno {Marshal.GetLastWin32Error()}");
            file = new SafeFileHandle((IntPtr)fd, ownsHandle: true);

            Assert.True(ArrApiKeyDiscovery.NativeLinux.TryGetRegularFileSize(file, out var size));
            Assert.Equal(7, size);

            var symlink = Path.Combine("/tmp", $"{name}.link");
            File.CreateSymbolicLink(symlink, path);
            try
            {
                var rejected = ArrApiKeyDiscovery.NativeLinux.OpenAt(
                    GetFd(parent), Path.GetFileName(symlink), ArrApiKeyDiscovery.NativeLinux.FileFlags);
                Assert.True(rejected < 0);
                Assert.Equal(ArrApiKeyDiscovery.NativeLinux.ELOOP, Marshal.GetLastWin32Error());
            }
            finally
            {
                File.Delete(symlink);
            }
        }
        finally
        {
            file?.Dispose();
            parent?.Dispose();
            root?.Dispose();
            File.Delete(path);
        }
    }

    private static Process? StartProcess(string fileName, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        try
        {
            var process = Process.Start(startInfo);
            if (process is not null)
                return process;
        }
        catch (Win32Exception)
        {
        }

        return null;
    }

    private const string NativeProbeSource = """
        #define _GNU_SOURCE
        #include <errno.h>
        #include <fcntl.h>
        #include <stdio.h>
        #include <stdlib.h>
        #include <string.h>
        #include <sys/stat.h>
        #include <unistd.h>
        #ifndef O_LARGEFILE
        #define O_LARGEFILE 0
        #endif

        int main(void) {
        #if defined(__aarch64__)
          const int nofollow = 0x8000, directory = 0x4000, largefile = 0;
        #elif defined(__x86_64__)
          const int nofollow = 0x20000, directory = 0x10000, largefile = 0x8000;
        #else
          return 77;
        #endif
          if (O_NONBLOCK != 0x800 || O_CLOEXEC != 0x80000 || O_PATH != 0x200000 ||
              O_NOFOLLOW != nofollow || O_DIRECTORY != directory ||
              (O_LARGEFILE != largefile && largefile != 0))
            return 1;
          int flags = O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC;
          int file_flags = O_RDONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC | O_LARGEFILE;
          char template[] = "/tmp/nzbdav-native-XXXXXX";
          char *dir = mkdtemp(template);
          if (!dir) return 2;
          char target[256], link[256];
          snprintf(target, sizeof(target), "%s/target", dir);
          snprintf(link, sizeof(link), "%s/link", dir);
          int created = open(target, O_WRONLY | O_CREAT | O_CLOEXEC, 0600);
          if (created < 0) return 3;
          close(created);
          int parent = openat(AT_FDCWD, dir, flags);
          if (parent < 0) return 4;
          if (symlink(target, link) != 0) return 5;
          errno = 0;
          int rejected = openat(parent, "link", file_flags);
          int result = rejected < 0 && errno == ELOOP ? 0 : 6;
          if (rejected >= 0) close(rejected);
          int accepted = openat(parent, "target", file_flags);
          if (accepted < 0) result = 7;
          if (accepted >= 0) close(accepted);
          close(parent);
          unlink(link); unlink(target); rmdir(dir);
          return result;
        }
        """;

    private static SafeFileHandle OpenDirectory(int parent, string name)
    {
        var fd = ArrApiKeyDiscovery.NativeLinux.OpenAt(parent, name, ArrApiKeyDiscovery.NativeLinux.DirectoryFlags);
        Assert.True(fd >= 0, $"openat directory failed with errno {Marshal.GetLastWin32Error()}");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static int GetFd(SafeFileHandle handle) => checked((int)handle.DangerousGetHandle().ToInt64());
}
