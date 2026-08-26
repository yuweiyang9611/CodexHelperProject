$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw
$productVersion = [string]$buildProps.Project.PropertyGroup.Version
$webPackage = Get-Content (Join-Path $projectRoot 'src\CodexU.Web\package.json') -Raw | ConvertFrom-Json
$electronPackage = Get-Content (Join-Path $projectRoot 'src\CodexU.Electron\package.json') -Raw | ConvertFrom-Json
$electronPackageLock = Get-Content (Join-Path $projectRoot 'src\CodexU.Electron\package-lock.json') -Raw | ConvertFrom-Json -AsHashtable
[xml]$appManifest = Get-Content (Join-Path $projectRoot 'src\CodexU.App\app.manifest') -Raw

if ([string]::IsNullOrWhiteSpace($productVersion)) {
    throw 'Directory.Build.props does not define Version.'
}

if ($webPackage.version -ne $productVersion) {
    throw "Version mismatch: .NET=$productVersion, web=$($webPackage.version)"
}

if ($electronPackage.version -ne $productVersion) {
    throw "Version mismatch: .NET=$productVersion, Electron=$($electronPackage.version)"
}

if ($electronPackageLock['version'] -ne $productVersion -or
    $electronPackageLock['packages']['']['version'] -ne $productVersion) {
    throw "Version mismatch: .NET=$productVersion, Electron lock=$($electronPackageLock['version'])"
}

$manifestVersion = [string]$appManifest.assembly.assemblyIdentity.version
if ($manifestVersion -ne "$productVersion.0") {
    throw "Version mismatch: .NET=$productVersion, app manifest=$manifestVersion"
}

Write-Host "Version consistency verified: $productVersion"
