#!/usr/bin/env bash
set -euo pipefail
workspace=${1:-/workspace}
scratch=$(mktemp -d /tmp/channelwave-profile.XXXXXX)
trap 'rm -rf -- "$scratch"' EXIT
chmod 755 "$scratch"
mkdir "$scratch/probe"
cp "$workspace/src/ChannelWave.Host.Linux/LinuxProfileSecurity.cs" "$scratch/probe/"
cat > "$scratch/probe/probe.csproj" <<'PROJECT'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
PROJECT
cat > "$scratch/probe/Program.cs" <<'CSHARP'
using ChannelWave.Host.Linux;
try { LinuxProfileSecurity.Prepare(args[0]); return 0; }
catch (IOException) { return 1; }
CSHARP
dotnet build "$scratch/probe/probe.csproj" --verbosity quiet
probe="$scratch/probe/bin/Debug/net10.0/probe.dll"
umask 022
mkdir "$scratch/profile"
echo 'synthetic library metadata' > "$scratch/profile/library.db"
runuser -u nobody -- cat "$scratch/profile/library.db" >/dev/null
dotnet "$probe" "$scratch/profile"
test "$(stat -c %a "$scratch/profile")" = 700
if runuser -u nobody -- cat "$scratch/profile/library.db" 2>/dev/null; then
    echo 'FAIL: a second OS user could read private metadata' >&2; exit 1
fi
mkdir "$scratch/target"
chmod 755 "$scratch/target"
ln -s "$scratch/target" "$scratch/link"
if dotnet "$probe" "$scratch/link" >/dev/null 2>&1; then echo 'FAIL: accepted symlink' >&2; exit 1; fi
test "$(stat -c %a "$scratch/target")" = 755
mkdir "$scratch/foreign"
chown nobody "$scratch/foreign"
if dotnet "$probe" "$scratch/foreign" >/dev/null 2>&1; then echo 'FAIL: accepted foreign owner' >&2; exit 1; fi
test "$(stat -c %a "$scratch/foreign")" = 755
echo 'PASS: umask 022 legacy profile is private; second-user read denied; links and foreign owners rejected.'
