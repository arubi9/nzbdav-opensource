#!/bin/sh
# Run the discovery ABI probe in the exact production Alpine images.
# Usage: tools/test-arr-discovery-abi.sh [x64|arm64|all]
set -eu

requested=${1:-all}
case "$requested" in
  x64) platforms='linux/amd64' ;;
  arm64) platforms='linux/arm64' ;;
  all) platforms='linux/amd64 linux/arm64' ;;
  *) echo "usage: $0 [x64|arm64|all]" >&2; exit 2 ;;
esac

for platform in $platforms; do
  arch=${platform#linux/}
  echo "== Arr discovery ABI probe: Alpine 3.22 $arch =="
  docker run --rm --platform "$platform" alpine:3.22 sh -eu -c '
    apk add --no-cache build-base dotnet8-sdk >/dev/null
    work=$(mktemp -d)
    trap "rm -rf \"$work\"" EXIT
    cat >"$work/native.c" <<'EOF'
#define _GNU_SOURCE
#include <errno.h>
#include <fcntl.h>
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <sys/stat.h>
#include <unistd.h>

int main(void) {
#if defined(__x86_64__)
  const int nofollow = 0x20000, directory = 0x10000;
  if (sizeof(struct stat) != 144 || offsetof(struct stat, st_mode) != 24) return 10;
#elif defined(__aarch64__)
  const int nofollow = 0x8000, directory = 0x4000;
  if (sizeof(struct stat) != 128 || offsetof(struct stat, st_mode) != 16) return 11;
#else
  return 12;
#endif
  if (O_NONBLOCK != 0x800 || O_CLOEXEC != 0x80000 || O_PATH != 0x200000 ||
      O_NOFOLLOW != nofollow || O_DIRECTORY != directory ||
      offsetof(struct stat, st_size) != 48) return 13;

  char template[] = "/tmp/nzbdav-abi-XXXXXX";
  char *dir = mkdtemp(template);
  if (!dir) return 14;
  char target[256], final_link[256], parent_link[256];
  snprintf(target, sizeof target, "%s/target", dir);
  snprintf(final_link, sizeof final_link, "%s/final", dir);
  snprintf(parent_link, sizeof parent_link, "%s/parent", dir);
  int made = open(target, O_WRONLY | O_CREAT | O_CLOEXEC, 0600);
  if (made < 0) return 15;
  if (write(made, "1234567", 7) != 7) return 16;
  close(made);
  int parent = openat(AT_FDCWD, dir, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
  if (parent < 0) return 17;
  int anchor = openat(parent, "target", O_PATH | O_NOFOLLOW | O_CLOEXEC);
  if (anchor < 0) return 18;
  struct stat before;
  if (fstat(anchor, &before) || !S_ISREG(before.st_mode) || before.st_size != 7) return 19;
  char proc[64]; snprintf(proc, sizeof proc, "/proc/self/fd/%d", anchor);
  int readable = openat(AT_FDCWD, proc, O_RDONLY | O_CLOEXEC);
  if (readable < 0) return 20;
  struct stat after;
  if (fstat(readable, &after) || before.st_dev != after.st_dev ||
      before.st_ino != after.st_ino || before.st_mode != after.st_mode) return 21;
  close(readable); close(anchor);

  if (symlink(target, final_link) || symlink(dir, parent_link)) return 22;
  errno = 0;
  int rejected = openat(parent, "final", O_PATH | O_NOFOLLOW | O_CLOEXEC);
  if (rejected < 0 || fstat(rejected, &after) != 0 || !S_ISLNK(after.st_mode)) return 23;
  close(rejected);
  int outside = openat(AT_FDCWD, parent_link, O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
  if (outside >= 0) return 24;
  unlink(final_link); unlink(parent_link); unlink(target); close(parent); rmdir(dir);
  return 0;
}
EOF
    cc -std=c11 -Wall -Wextra -Werror "$work/native.c" -o "$work/native"
    "$work/native"

    cat >"$work/abi.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><InvariantGlobalization>true</InvariantGlobalization></PropertyGroup></Project>
EOF
    cat >"$work/Program.cs" <<'EOF'
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

static class P {
    const int AT_FDCWD = -100, O_RDONLY = 0, O_CLOEXEC = 0x80000, O_PATH = 0x200000;
    const uint S_IFMT = 0xf000, S_IFREG = 0x8000;
    [DllImport("libc", SetLastError=true)] static extern int openat(int d, string p, int f);
    [DllImport("libc", SetLastError=true)] static extern int fstat(int fd, IntPtr stat);
    static int Fd(SafeFileHandle h) => checked((int)h.DangerousGetHandle().ToInt64());
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    public static int Main() {
        bool arm = RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
        int modeOffset = arm ? 16 : 24;
        int sizeOffset = 48;
        int statSize = arm ? 128 : 144;
        var dir = Path.Combine("/tmp", "nzbdav-managed-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.xml"); File.WriteAllBytes(path, "1234567"u8.ToArray());
        try {
            int nofollow = arm ? 0x8000 : 0x20000, directory = arm ? 0x4000 : 0x10000;
            using var parent = new SafeFileHandle((IntPtr)openat(AT_FDCWD, dir, O_RDONLY | directory | nofollow | O_CLOEXEC), true);
            Check(!parent.IsInvalid, "parent open");
            using var anchor = new SafeFileHandle((IntPtr)openat(Fd(parent), "config.xml", O_PATH | nofollow | O_CLOEXEC), true);
            Check(!anchor.IsInvalid, "anchor open");
            long size; uint mode; ulong dev, ino;
            IntPtr stat = Marshal.AllocHGlobal(statSize);
            try {
                Check(fstat(Fd(anchor), stat) == 0, "anchor stat");
                size = Marshal.ReadInt64(stat, sizeOffset); mode = unchecked((uint)Marshal.ReadInt32(stat, modeOffset));
                dev = unchecked((ulong)Marshal.ReadInt64(stat, 0)); ino = unchecked((ulong)Marshal.ReadInt64(stat, 8));
            } finally { Marshal.FreeHGlobal(stat); }
            Check((mode & S_IFMT) == S_IFREG && size == 7, "regular file extraction");
            using var readable = new SafeFileHandle((IntPtr)openat(AT_FDCWD, $"/proc/self/fd/{Fd(anchor)}", O_RDONLY | O_CLOEXEC), true);
            Check(!readable.IsInvalid, "proc reopen");
            stat = Marshal.AllocHGlobal(statSize);
            try {
                Check(fstat(Fd(readable), stat) == 0 &&
                    unchecked((ulong)Marshal.ReadInt64(stat, 0)) == dev &&
                    unchecked((ulong)Marshal.ReadInt64(stat, 8)) == ino &&
                    unchecked((uint)Marshal.ReadInt32(stat, modeOffset)) == mode, "identity");
            } finally { Marshal.FreeHGlobal(stat); }
            File.CreateSymbolicLink(Path.Combine(dir, "final-link"), path);
            using var finalLink = new SafeFileHandle((IntPtr)openat(Fd(parent), "final-link", O_PATH | nofollow | O_CLOEXEC), true);
            Check(!finalLink.IsInvalid, "final probe");
            stat = Marshal.AllocHGlobal(statSize);
            try {
                Check(fstat(Fd(finalLink), stat) == 0 &&
                    (unchecked((uint)Marshal.ReadInt32(stat, modeOffset)) & S_IFMT) != S_IFREG, "final symlink rejection");
            } finally { Marshal.FreeHGlobal(stat); }
            File.CreateSymbolicLink(Path.Combine(dir, "parent-link"), dir);
            Check(openat(AT_FDCWD, Path.Combine(dir, "parent-link"), O_RDONLY | directory | nofollow | O_CLOEXEC) < 0, "parent symlink rejection");
            return 0;
        } finally { Directory.Delete(dir, true); }
    }
}
EOF
    dotnet run --project "$work/abi.csproj" --configuration Release
  '
done
