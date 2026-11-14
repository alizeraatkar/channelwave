param([Parameter(Mandatory)][ValidateSet('windows', 'linux')][string]$Platform,
      [Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][int]$BuildNumber)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$rid = if ($Platform -eq 'windows') { 'win-x64' } else { 'linux-x64' }
$project = if ($Platform -eq 'windows') { 'src/ChannelWave.Host.Maui' } else { 'src/ChannelWave.Host.Linux' }
$publish = "$root/artifacts/publish-$Platform"
$output = "$root/artifacts/release-$Platform"
$native = "$root/artifacts/tdlib/$Platform"
python "$root/scripts/verify-native.py" $native $Platform
$properties = @('-p:ChannelWaveRelease=true', "-p:ChannelWaveReleaseRid=$rid", '-p:SelfContained=true')
if ($Platform -eq 'windows') {
    # Windows package identity requires a numeric three-part display version.
    $displayVersion = ($Version -split '-', 2)[0]
    $properties += @('-p:ChannelWaveTarget=net10.0-windows10.0.19041.0', '-p:WindowsAppSDKSelfContained=true', "-p:ChannelWaveTdlibWindowsRoot=$native", "-p:ApplicationVersion=$BuildNumber", "-p:ApplicationDisplayVersion=$displayVersion", "-p:InformationalVersion=$Version")
} else { $properties += "-p:ChannelWaveTdlibLinuxRoot=$native" }
dotnet restore $project --locked-mode -p:Configuration=Release @properties
$framework = if ($Platform -eq 'windows') { 'net10.0-windows10.0.19041.0' } else { 'net10.0' }
dotnet publish $project -c Release -f $framework --no-restore @properties -o $publish
Copy-Item "$native/licenses" "$publish/native-licenses" -Recurse
Copy-Item "$native/channelwave-build.json" "$publish/native-build.json"
Copy-Item "$root/docs/desktop-installation.md" "$publish/README.md"
New-Item -ItemType Directory -Force $output | Out-Null
if ($Platform -eq 'windows') {
    python "$root/scripts/security-check.py" artifacts $publish
    # Probe the exact shipped DLL, not a development copy from PATH.
    dotnet run --project tools/ChannelWave.Probe -c Release -- "$publish/tdjson.dll"
    Compress-Archive -Path "$publish/*" -DestinationPath "$output/ChannelWave-$Version-windows-x64.zip"
} else {
    Copy-Item "$root/artifacts/telegram.json" "$publish/telegram.json"
    python "$root/scripts/security-check.py" artifacts $publish
    dotnet run --project tools/ChannelWave.Probe -c Release -- "$publish/libtdjson.so"
    python "$root/scripts/verify-linux-dependencies.py" $publish --report "$root/artifacts/smoke-linux-dependencies.json"
    & tar -czf "$output/ChannelWave-$Version-linux-x64.tar.gz" -C $publish .
    $deb = "$root/artifacts/deb"
    New-Item -ItemType Directory -Force "$deb/DEBIAN", "$deb/opt/channelwave", "$deb/usr/bin", "$deb/usr/share/applications" | Out-Null
    Copy-Item "$publish/*" "$deb/opt/channelwave" -Recurse
    $debianVersion = $Version.Replace('-beta.', '~beta.')
    @"
Package: channelwave
Version: $debianVersion
Architecture: amd64
Maintainer: ChannelWave maintainers
Depends: libc6, libgcc-s1, libstdc++6, libicu74, libgssapi-krb5-2, libnotify4, libgtk-3-0t64, libwebkit2gtk-4.1-0, libvlc5, vlc-plugin-base, libsecret-tools, libssl3t64, zlib1g, dbus-user-session
Description: Local-first Telegram music client
"@ | Set-Content "$deb/DEBIAN/control" -Encoding utf8NoBOM
    "#!/bin/sh`nexec /opt/channelwave/ChannelWave.Host.Linux `"`$@`"`n" | Set-Content "$deb/usr/bin/channelwave" -Encoding utf8NoBOM
    "[Desktop Entry]`nType=Application`nName=ChannelWave`nExec=channelwave`nIcon=/opt/channelwave/wwwroot/icon-192.png`nCategories=AudioVideo;Audio;`n" | Set-Content "$deb/usr/share/applications/channelwave.desktop" -Encoding utf8NoBOM
    & chmod 755 "$deb/usr/bin/channelwave"
    python "$root/scripts/security-check.py" artifacts $deb
    & dpkg-deb --root-owner-group --build $deb "$output/ChannelWave-$Version-linux-x64.deb"
    & dpkg-deb --info "$output/ChannelWave-$Version-linux-x64.deb"
    & sudo apt-get install -y "$output/ChannelWave-$Version-linux-x64.deb"
    & test -x /usr/bin/channelwave
}
python "$root/scripts/release.py" package --platform $Platform --folder $output
