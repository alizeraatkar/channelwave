param([Parameter(Mandatory)][string[]]$Workloads)
$ErrorActionPreference = 'Stop'
$config = Get-Content (Join-Path $PSScriptRoot '../.github/release.json') -Raw | ConvertFrom-Json
if ((& dotnet --version).Trim() -ne $config.toolchain.sdk) { throw 'Unexpected .NET SDK' }
# Workload installers may seed NuGet caches from bundled/offline packs. Never reuse
# that cache for the application's integrity-checked, nuget.org-only restore.
$applicationCache = $env:NUGET_PACKAGES
try {
    $env:NUGET_PACKAGES = Join-Path ([IO.Path]::GetTempPath()) 'channelwave-workload-packages'
    & dotnet workload install @Workloads --version $config.toolchain.workload --source https://api.nuget.org/v3/index.json
    if ($LASTEXITCODE) { throw 'Pinned workload installation failed' }
    if ((& dotnet workload --version).Trim() -ne $config.toolchain.workload) { throw 'Unexpected workload set' }
    & dotnet workload --info
} finally { $env:NUGET_PACKAGES = $applicationCache }
