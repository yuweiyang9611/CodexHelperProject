$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw
$productVersion = [string]$buildProps.Project.PropertyGroup.Version
$webPackage = Get-Content (Join-Path $projectRoot 'src\CodexU.Web\package.json') -Raw | ConvertFrom-Json
$electronPackage = Get-Content (Join-Path $projectRoot 'src\CodexU.Electron\package.json') -Raw | ConvertFrom-Json
$electronPackageLockPath = Join-Path $projectRoot 'src\CodexU.Electron\package-lock.json'
$electronLockVersionReader = Join-Path $PSScriptRoot 'Get-ElectronLockVersions.cjs'
$electronLockVersionJson = & node $electronLockVersionReader $electronPackageLockPath
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to read Electron package-lock.json with Node.js.'
}
$electronLockVersions = $electronLockVersionJson | ConvertFrom-Json
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

if ([string]::IsNullOrWhiteSpace([string]$electronLockVersions.rootVersion)) {
    throw 'Electron package-lock.json does not contain a root package entry.'
}

if ($electronLockVersions.lockVersion -ne $productVersion -or
    $electronLockVersions.rootVersion -ne $productVersion) {
    throw "Version mismatch: .NET=$productVersion, Electron lock=$($electronLockVersions.lockVersion)"
}

$manifestVersion = [string]$appManifest.assembly.assemblyIdentity.version
if ($manifestVersion -ne "$productVersion.0") {
    throw "Version mismatch: .NET=$productVersion, app manifest=$manifestVersion"
}

Write-Host "Version consistency verified: $productVersion"
