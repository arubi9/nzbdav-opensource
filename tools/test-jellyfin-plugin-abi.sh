#!/bin/sh
# Verify Jellyfin's Linux stat and open-flag ABI against native libc.
# Usage: tools/test-jellyfin-plugin-abi.sh [x64|arm64|all]
set -eu

requested=${1:-all}
case "$requested" in
  x64) platforms='linux/amd64' ;;
  arm64) platforms='linux/arm64' ;;
  all) platforms='linux/amd64 linux/arm64' ;;
  *) echo "usage: $0 [x64|arm64|all]" >&2; exit 2 ;;
esac

repo=$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)
for platform in $platforms; do
  arch=${platform#linux/}
  [ "$arch" = amd64 ] && arch=x64
  echo "== Jellyfin plugin ABI probe: Alpine 3.22 $arch =="
  MSYS_NO_PATHCONV=1 docker run --rm --platform "$platform" -e PROBE_ARCH="$arch" -e DOTNET_NOLOGO=1 -e DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 -v "$repo:/src" alpine:3.22@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce sh -eu -c '
    apk add --no-cache build-base dotnet9-sdk >/dev/null
    arch=$PROBE_ARCH
    work=$(mktemp -d)
    trap "rm -rf \"$work\"" EXIT
    cat >"$work/native.c" <<'EOF'
#define _GNU_SOURCE
#include <stddef.h>
#include <stdio.h>
#include <stdlib.h>
#include <errno.h>
#include <fcntl.h>
#include <sys/stat.h>
#include <unistd.h>
int main(void) {
  printf("%zu %zu %zu %d %d %d %d %d %d %d %d", sizeof(struct stat), offsetof(struct stat, st_mode), offsetof(struct stat, st_dev), O_RDONLY, O_WRONLY, O_NOFOLLOW, O_DIRECTORY, O_CREAT, O_EXCL, O_TRUNC, O_CLOEXEC);
  char dir[] = "/tmp/nzbdav-open-XXXXXX";
  if (!mkdtemp(dir)) return 2;
  char target[256], link[256];
  snprintf(target, sizeof(target), "%s/target", dir);
  snprintf(link, sizeof(link), "%s/link", dir);
  int created = open(target, O_WRONLY | O_CREAT | O_CLOEXEC, 0600);
  if (created < 0) return 3;
  close(created);
  if (symlink(target, link) != 0) return 4;
  errno = 0;
  int rejected = open(link, O_RDONLY | O_NOFOLLOW | O_CLOEXEC);
  int result = rejected < 0 && errno == ELOOP ? 0 : 1;
  if (rejected >= 0) close(rejected);
  unlink(link); unlink(target); rmdir(dir);
  printf(" nofollow=%s\\n", result == 0 ? "ok" : "failed");
  return result;
}
EOF
    cc -std=c11 -Wall -Wextra -Werror "$work/native.c" -o "$work/native"
    native=$($work/native)

    cat >"$work/abi-probe.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><InvariantGlobalization>true</InvariantGlobalization></PropertyGroup><ItemGroup><ProjectReference Include="/src/jellyfin-plugin/Jellyfin.Plugin.Nzbdav/Jellyfin.Plugin.Nzbdav.csproj" /><PackageReference Include="Jellyfin.Controller" Version="10.11.8" /><PackageReference Include="Jellyfin.Model" Version="10.11.8" /></ItemGroup></Project>
EOF
    cat >"$work/Program.cs" <<'EOF'
using System.Reflection;
using System.Runtime.InteropServices;
using Jellyfin.Plugin.Nzbdav;
var statProperty = typeof(NzbdavLibrarySyncTask).GetProperty("LinuxAbi", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("LinuxAbi property missing");
var openProperty = typeof(NzbdavLibrarySyncTask).GetProperty("LinuxOpen", BindingFlags.Static | BindingFlags.NonPublic)
    ?? throw new Exception("LinuxOpen property missing");
var abi = statProperty.GetValue(null) ?? throw new Exception("LinuxAbi value missing");
var open = openProperty.GetValue(null) ?? throw new Exception("LinuxOpen value missing");
var statType = abi.GetType();
var openType = open.GetType();
int Get(Type type, string name) => (int)(type.GetProperty(name)?.GetValue(type == statType ? abi : open) ?? throw new Exception(name + " missing"));
string arch = (string)(statType.GetProperty("Architecture")?.GetValue(abi) ?? throw new Exception("Architecture missing"));
Console.WriteLine($"{Get(statType, "FstatBufferSize")} {Get(statType, "FstatModeOffset")} {Get(statType, "FstatDeviceOffset")} {Get(openType, "ReadOnly")} {Get(openType, "WriteOnly")} {Get(openType, "NoFollow")} {Get(openType, "Directory")} {Get(openType, "Create")} {Get(openType, "Exclusive")} {Get(openType, "Truncate")} {Get(openType, "CloseOnExec")} {arch}");
EOF
    dotnet restore "$work/abi-probe.csproj" >"$work/restore.stdout"
    cat "$work/restore.stdout" >&2
    dotnet build "$work/abi-probe.csproj" --configuration Release --no-restore \
      --verbosity quiet -warnaserror -p:TreatWarningsAsErrors=true >&2
    managed=$(dotnet "$work/bin/Release/net9.0/abi-probe.dll")
    case "$arch" in
      x64) expected="${native% nofollow=*} x64" ;;
      arm64) expected="${native% nofollow=*} arm64" ;;
      *) echo "unexpected container architecture: $arch" >&2; exit 1 ;;
    esac
    [ "$managed" = "$expected" ] || { echo "native: $native" >&2; echo "managed: $managed" >&2; exit 1; }
    case "$native" in *"nofollow=ok") ;; *) echo "native O_NOFOLLOW behavior failed: $native" >&2; exit 1 ;; esac
    # The managed probe is intentionally separate from native.c: it reads the
    # plugin compiled ABI constants, while native.c checks headers and actual
    # symlink rejection.
  '
done
