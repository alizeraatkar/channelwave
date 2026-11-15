$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'This probe is for disposable hosted CI runners.' }
Get-WindowsFeature *Media* | Select-Object Name, Installed | ConvertTo-Json
Get-Service AudioEndpointBuilder, Audiosrv | Select-Object Name, Status, StartType | ConvertTo-Json
Get-CimInstance Win32_SoundDevice | Select-Object Name, Status | ConvertTo-Json
$probe = Join-Path $env:RUNNER_TEMP 'channelwave-media-probe'
New-Item -ItemType Directory -Force $probe | Out-Null
'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0-windows10.0.19041.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>' | Set-Content "$probe/Probe.csproj"
@"
using System.Text.Json;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using var player = new MediaPlayer { AutoPlay = false };
var ready = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
player.MediaOpened += (_, _) => ready.TrySetResult(new { result = "opened", duration = player.PlaybackSession.NaturalDuration.TotalSeconds });
player.MediaFailed += (_, e) => ready.TrySetResult(new { result = "failed", error = e.Error.ToString(), code = e.ExtendedErrorCode?.HResult.ToString("X8"), message = e.ErrorMessage });
try {
 var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector());
 Console.WriteLine(JsonSerializer.Serialize(new { audioDevices = devices.Count }));
 var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(args[0]));
 player.Source = MediaSource.CreateFromStorageFile(file);
 Console.WriteLine(JsonSerializer.Serialize(await ready.Task.WaitAsync(TimeSpan.FromSeconds(20))));
} catch (Exception e) { Console.WriteLine(JsonSerializer.Serialize(new { result = "exception", errorType = e.GetType().Name, code = e.HResult.ToString("X8"), message = e.Message })); }
"@ | Set-Content "$probe/Program.cs"
dotnet run --project "$probe/Probe.csproj" -- "$PWD/tests/browser/fixtures/stream-tone.mp3"
$feature = Get-WindowsFeature Server-Media-Foundation
if (!$feature.Installed) {
    $result = Install-WindowsFeature Server-Media-Foundation
    $result | Select-Object Success, RestartNeeded | ConvertTo-Json
    if (!$result.Success -or $result.RestartNeeded -eq 'Yes') { throw 'Media Foundation setup requires a fresh runner image.' }
}
foreach ($name in @('AudioEndpointBuilder', 'Audiosrv')) {
    Set-Service $name -StartupType Automatic
    Start-Service $name
}
dotnet run --project "$probe/Probe.csproj" --no-build -- "$PWD/tests/browser/fixtures/stream-tone.mp3"
