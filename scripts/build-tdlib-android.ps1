param(
    [string]$ProxyUri,
    [ValidateSet('x86_64', 'arm64-v8a')][string]$Abi = 'x86_64',
    [ValidateRange(1, 4)][int]$BuildJobs = 2
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$context = Join-Path $projectRoot '.tools/tdlib-android'
$output = Join-Path $projectRoot 'artifacts/tdlib/android'
New-Item -ItemType Directory -Path $context -Force | Out-Null
function Get-VerifiedArchive([string]$Name, [string]$Url, [string]$Algorithm, [string]$ExpectedHash) {
    $path = Join-Path $context $Name
    if (!(Test-Path $path)) {
        $arguments = @('--fail', '--location', '--retry', '3', '--connect-timeout', '20', '--speed-time', '60', '--speed-limit', '1024', '--output', $path)
        if ($ProxyUri) { $arguments += @('--proxy', $ProxyUri) }
        $curl = if ($IsWindows) { 'curl.exe' } else { 'curl' }
        & $curl @arguments $Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $Name" }
    }
    if ((Get-FileHash $path -Algorithm $Algorithm).Hash -ne $ExpectedHash) { throw "Checksum mismatch: $path" }
}
Get-VerifiedArchive 'android-ndk-r28c-linux.zip' 'https://redirector.gvt1.com/edgedl/android/repository/android-ndk-r28c-linux.zip' 'SHA1' 'a7b54a5de87fecd125a17d54f73c446199e72a64'
Get-VerifiedArchive 'openssl-3.5.8.tar.gz' 'https://www.openssl.org/source/openssl-3.5.8.tar.gz' 'SHA256' 'a8f84a39918ec6415ce765d9b429d313ba97b8143169c172e734b9514464f5b2'
Copy-Item (Join-Path $PSScriptRoot 'tdlib-android-probe.c') (Join-Path $context 'tdlib-android-probe.c')
@('*', '!android-ndk-r28c-linux.zip', '!openssl-3.5.8.tar.gz', '!tdlib-android-probe.c') | Set-Content (Join-Path $context '.dockerignore')
Push-Location $projectRoot
try {
    & docker build -f scripts/tdlib.Dockerfile --build-arg "BUILD_JOBS=$BuildJobs" --target android-source -t channelwave-tdlib-android-source .
    if ($LASTEXITCODE -ne 0) { throw 'Pinned TDLib source build failed' }
    & docker build -f scripts/tdlib-android.Dockerfile --build-arg "BUILD_JOBS=$BuildJobs" --build-arg "ANDROID_ABI=$Abi" --target artifact --output "type=local,dest=$output" $context
    if ($LASTEXITCODE -ne 0) { throw 'Android TDLib build failed' }
    Write-Output "Built pinned Android $Abi TDLib in $output"
} finally { Pop-Location }
