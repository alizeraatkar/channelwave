$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
Push-Location $workspace
try {
    $staging = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/tdweb-secure'))
    if (!$staging.StartsWith([IO.Path]::GetFullPath($workspace) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid browser staging destination.' }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    docker build -f scripts/tdweb.Dockerfile --target artifact --output type=local,dest=artifacts/tdweb-secure .
    if ($LASTEXITCODE -ne 0) { throw 'Pinned tdweb build failed. See build output; do not substitute an unrelated npm version.' }
    node scripts/verify-tdweb.mjs artifacts/tdweb-secure
    if ($LASTEXITCODE -ne 0) { throw 'Browser engine security verification failed.' }
    $destination = [IO.Path]::GetFullPath((Join-Path $workspace 'src/ChannelWave.Host.Web/wwwroot/tdweb'))
    if (!$destination.StartsWith([IO.Path]::GetFullPath($workspace) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid browser asset destination.' }
    if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $workspace 'artifacts/tdweb-secure') -Destination $destination -Recurse
} finally { Pop-Location }
