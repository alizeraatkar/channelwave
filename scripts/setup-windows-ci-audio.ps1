# CI only: add a virtual output so the shipped Windows engine can run its audio smoke test.
$ErrorActionPreference = 'Stop'
if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Audio provisioning is restricted to disposable hosted runners.' }
$feature = Get-WindowsFeature Server-Media-Foundation
if (!$feature.Installed) {
    $result = Install-WindowsFeature Server-Media-Foundation
    if (!$result.Success -or $result.RestartNeeded -eq 'Yes') { throw 'Media Foundation setup needs a runner restart.' }
}
foreach ($name in @('AudioEndpointBuilder', 'Audiosrv')) {
    Set-Service $name -StartupType Automatic
    Start-Service $name
}
$folder = Join-Path $env:RUNNER_TEMP 'channelwave-ci-audio'
New-Item -ItemType Directory -Force $folder | Out-Null
$archive = Join-Path $folder 'Scream3.6.zip'
Invoke-WebRequest https://github.com/duncanthrax/scream/releases/download/3.6/Scream3.6.zip -OutFile $archive
if ((Get-FileHash $archive -Algorithm SHA256).Hash -ne '25ea5e778b4e6995a98d448b9b5f6d321f681663f1aeeec69d8e63183d008b19') { throw 'CI audio archive checksum mismatch' }
Expand-Archive -LiteralPath $archive -DestinationPath $folder -Force
$driver = Join-Path $folder 'Install/driver'
$signature = Get-AuthenticodeSignature (Join-Path $driver 'Scream.sys')
if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate) { throw 'CI audio driver signature is invalid' }
$store = [System.Security.Cryptography.X509Certificates.X509Store]::new('TrustedPublisher', 'LocalMachine')
try { $store.Open('ReadWrite'); $store.Add($signature.SignerCertificate) } finally { $store.Close() }
$install = Start-Process -FilePath "$folder/Install/helpers/devcon.exe" -ArgumentList @('install', "`"$driver/Scream.inf`"", '*Scream') -WorkingDirectory $driver -WindowStyle Hidden -Wait -PassThru
if ($install.ExitCode -notin 0, 1) { throw "CI audio device installation failed: $($install.ExitCode)" }
foreach ($name in @('AudioEndpointBuilder', 'Audiosrv')) { Start-Service $name }
$deadline = (Get-Date).AddSeconds(45)
do {
    $endpoint = Get-PnpDevice -Class AudioEndpoint -ErrorAction SilentlyContinue | Where-Object { $_.FriendlyName -like '*Scream*' -and $_.Status -eq 'OK' }
    if ($endpoint) { $endpoint | Select-Object FriendlyName, Status | ConvertTo-Json; break }
    Start-Sleep -Milliseconds 500
} while ((Get-Date) -lt $deadline)
if (!$endpoint) { throw 'No virtual CI audio endpoint became available; playback checks remain required.' }
