$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw
$productVersion = [string]$buildProps.Project.PropertyGroup.Version
$webPackage = Get-Content (Join-Path $projectRoot 'src\CodexU.Web\package.json') -Raw | ConvertFrom-Json
[xml]$appManifest = Get-Content (Join-Path $projectRoot 'src\CodexU.App\app.manifest') -Raw

if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw 'Directory.Build.props does not define Version.'
}

if ($webPackage.version -ne $productVersion) {
    throw "Version mismatch: .NET=$productVersion, web=$($webPackage.version)"
}

$manifestVersion = [string]$appManifest.assembly.assemblyIdentity.version
if ($manifestVersion -ne "$productVersion.0") {
    throw "Version mismatch: .NET=$productVersion, app manifest=$manifestVersion"
}

Write-Host "Version consistency verified: $productVersion"
