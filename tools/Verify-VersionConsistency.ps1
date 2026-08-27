$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
[xml]$buildProps = Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw
$productVersion = ([string]$buildProps.Project.PropertyGroup.Version).Trim()
$assemblyVersion = ([string]$buildProps.Project.PropertyGroup.AssemblyVersion).Trim()
$fileVersion = ([string]$buildProps.Project.PropertyGroup.FileVersion).Trim()
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

$productVersionMatch = [Regex]::Match(
    $productVersion,
    '^(?<numeric>\d+\.\d+\.\d+)(?:-[0-9A-Za-z.-]+)?$')
if (-not $productVersionMatch.Success) {
    throw "Directory.Build.props contains an invalid Version: '$productVersion'. Expected a three-part semantic version with an optional prerelease suffix."
}

$fourPartNumericVersionPattern = '^\d+\.\d+\.\d+\.\d+$'
if ($assemblyVersion -notmatch $fourPartNumericVersionPattern) {
    throw "Directory.Build.props contains an invalid AssemblyVersion: '$assemblyVersion'. Expected four numeric components."
}
if ($fileVersion -notmatch $fourPartNumericVersionPattern) {
    throw "Directory.Build.props contains an invalid FileVersion: '$fileVersion'. Expected four numeric components."
}

$expectedBinaryVersion = "$($productVersionMatch.Groups['numeric'].Value).0"
if ($assemblyVersion -ne $expectedBinaryVersion) {
    throw "Version mismatch: product=$productVersion requires AssemblyVersion=$expectedBinaryVersion, found $assemblyVersion"
}
if ($fileVersion -ne $expectedBinaryVersion) {
    throw "Version mismatch: product=$productVersion requires FileVersion=$expectedBinaryVersion, found $fileVersion"
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
if ($manifestVersion -ne $assemblyVersion) {
    throw "Version mismatch: AssemblyVersion=$assemblyVersion, app manifest=$manifestVersion"
}

Write-Host "Version consistency verified: product=$productVersion, assembly=$assemblyVersion, file=$fileVersion"
