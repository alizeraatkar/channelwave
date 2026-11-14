param([Parameter(Mandatory)][ValidateSet('windows', 'linux')][string]$Platform)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$root = Split-Path $PSScriptRoot -Parent
$env:CHANNELWAVE_SMOKE_AUDIO = "$root/tests/browser/fixtures/stream-tone.mp3"
$staging = "$root/artifacts/smoke-package-$Platform-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $staging | Out-Null
if ($Platform -eq 'windows') {
    $archives = @(Get-ChildItem "$root/artifacts/release-windows" -Filter '*.zip')
    if ($archives.Count -ne 1) { throw 'Expected one Windows portable archive' }
    Expand-Archive -LiteralPath $archives[0].FullName -DestinationPath $staging
    $targets = @(@{ name = 'zip'; executable = "$staging/ChannelWave.Host.Maui.exe" })
} else {
    $archives = @(Get-ChildItem "$root/artifacts/release-linux" -Filter '*.tar.gz')
    if ($archives.Count -ne 1) { throw 'Expected one Linux portable archive' }
    & tar -xzf $archives[0].FullName -C $staging
    $targets = @(@{ name = 'deb'; executable = '/usr/bin/channelwave' }, @{ name = 'tar'; executable = "$staging/ChannelWave.Host.Linux" })
}
foreach ($target in $targets) {
    $env:CHANNELWAVE_SMOKE_REPORT = "$root/artifacts/smoke-$Platform-$($target.name).json"
    $env:CHANNELWAVE_SMOKE_ROOT = "$staging/profile-$($target.name)"
    if (Test-Path $env:CHANNELWAVE_SMOKE_REPORT) { Remove-Item -LiteralPath $env:CHANNELWAVE_SMOKE_REPORT }
    if ($Platform -eq 'windows') {
        $process = Start-Process -FilePath $target.executable -ArgumentList '--smoke-test' -WindowStyle Hidden -PassThru
    } else {
        $process = Start-Process -FilePath xvfb-run -ArgumentList @('-a', 'dbus-run-session', '--', "`"$($target.executable)`"", '--smoke-test') -PassThru
    }
    try {
        if (!$process.WaitForExit(60000)) { throw 'Native startup/audio smoke test timed out' }
        if ($process.ExitCode -ne 0) { throw "Native smoke failed with exit code $($process.ExitCode)" }
        $result = Get-Content $env:CHANNELWAVE_SMOKE_REPORT -Raw | ConvertFrom-Json
        if ($result.result -ne 'pass') { throw 'Native smoke report did not pass' }
        $result | ConvertTo-Json
    } finally { if (!$process.HasExited) { $process.Kill($true) } }
}
