param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$projectRootPath = (Resolve-Path $ProjectRoot).Path
$outputRootPath = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $projectRootPath
} else {
    [IO.Path]::GetFullPath($OutputRoot)
}
$rows = New-Object System.Collections.Generic.List[object]
$licenseDocuments = New-Object System.Collections.Generic.List[object]
$licenseFallbacks = New-Object System.Collections.Generic.List[object]
$runtimeLegalPayloads = New-Object System.Collections.Generic.List[object]

function Add-InventoryRow {
    param(
        [string]$Ecosystem,
        [string]$Name,
        [string]$Version,
        [string]$License,
        [string]$Source
    )

    $rows.Add([pscustomobject]@{
        Ecosystem = $Ecosystem
        Name = $Name
        Version = $Version
        License = $(if ([string]::IsNullOrWhiteSpace($License)) { 'UNKNOWN - review required' } else { $License.Trim() })
        Source = $Source
    })
}

function Get-NormalizedLegalText {
    param([string]$Text)

    $normalizedLines = (($Text -replace "`r`n", "`n" -replace "`r", "`n").Trim() -split "`n") |
        ForEach-Object { $_.TrimEnd() }
    return (($normalizedLines -join "`n") + "`n")
}

function Add-LicenseDocument {
    param(
        [string]$Ecosystem,
        [string]$Name,
        [string]$Version,
        [string]$DocumentName,
        [string]$Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        throw "$Ecosystem package $Name $Version has an empty legal document: $DocumentName"
    }

    $normalized = Get-NormalizedLegalText $Text
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = ([BitConverter]::ToString($sha256.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
    }

    $licenseDocuments.Add([pscustomobject]@{
        Ecosystem = $Ecosystem
        Name = $Name
        Version = $Version
        DocumentName = $DocumentName
        Hash = $hash
        Text = $normalized
    })
}

function Add-PackageLegalFiles {
    param(
        [string]$Ecosystem,
        [string]$Name,
        [string]$Version,
        [string]$PackageDirectory
    )

    $legalFiles = @(Get-ChildItem -LiteralPath $PackageDirectory -File | Where-Object {
        $_.Name -match '^(?i:(?:LICENSE|LICENCE|NOTICE|COPYING)(?:[._ -].*)?|THIRD[-_. ]?PARTY[-_. ]?(?:LICENSE|LICENCE|NOTICE).*)$'
    } | Sort-Object Name)

    foreach ($legalFile in $legalFiles) {
        Add-LicenseDocument $Ecosystem $Name $Version $legalFile.Name (Get-Content -LiteralPath $legalFile.FullName -Raw -Encoding utf8)
    }

    return $legalFiles.Count
}

function Add-LicenseFallback {
    param(
        [string]$Ecosystem,
        [string]$Name,
        [string]$Version,
        [string]$License,
        [string]$Copyright
    )

    $licenseFallbacks.Add([pscustomobject]@{
        Ecosystem = $Ecosystem
        Name = $Name
        Version = $Version
        License = $License.Trim()
        Copyright = $Copyright.Trim()
    })
}

$assetsPath = Join-Path $projectRootPath 'src\CodexU.Sidecar\obj\project.assets.json'
if (-not (Test-Path $assetsPath -PathType Leaf)) {
    throw "Sidecar NuGet assets file not found. Run dotnet restore first: $assetsPath"
}

$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
if ($packageFolders.Count -eq 0) {
    throw "NuGet assets contain no package folders: $assetsPath"
}

function Resolve-NuGetPackageDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Version,
        [string[]]$RequiredFiles = @()
    )

    foreach ($folder in $packageFolders) {
        $candidate = Join-Path (Join-Path $folder $Name.ToLowerInvariant()) $Version
        if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }

        $nuspecPath = Join-Path $candidate ($Name.ToLowerInvariant() + '.nuspec')
        if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) { continue }

        $hasRequiredFiles = $true
        foreach ($requiredFile in $RequiredFiles) {
            $requiredPath = Join-Path $candidate $requiredFile
            if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or
                (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
                $hasRequiredFiles = $false
                break
            }
        }
        if ($hasRequiredFiles) { return $candidate }
    }
    throw "No complete restored NuGet package directory was found for $Name $Version. Searched: $($packageFolders -join ', ')"
}

$runtimeConfigPath = Join-Path $projectRootPath 'src\CodexU.Electron\backend\CodexU.Sidecar.runtimeconfig.json'
if (-not (Test-Path $runtimeConfigPath -PathType Leaf)) {
    throw "Published Sidecar runtime metadata not found. Publish the win-x64 self-contained Sidecar first: $runtimeConfigPath"
}

$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
$runtimeFramework = @($runtimeConfig.runtimeOptions.includedFrameworks) |
    Where-Object { $_.name -eq 'Microsoft.NETCore.App' } |
    Select-Object -First 1
if (-not $runtimeFramework -or [string]::IsNullOrWhiteSpace([string]$runtimeFramework.version)) {
    throw "Published Sidecar is not a self-contained Microsoft.NETCore.App deployment: $runtimeConfigPath"
}

$runtimeVersion = [string]$runtimeFramework.version
$runtimePackName = 'Microsoft.NETCore.App.Runtime.win-x64'
$runtimePackDirectory = Resolve-NuGetPackageDirectory $runtimePackName $runtimeVersion @(
    'LICENSE.TXT',
    'THIRD-PARTY-NOTICES.TXT'
)
$runtimeLicenseSource = Join-Path $runtimePackDirectory 'LICENSE.TXT'
$runtimeNoticesSource = Join-Path $runtimePackDirectory 'THIRD-PARTY-NOTICES.TXT'

foreach ($runtimeLegalFile in @(
    @($runtimeLicenseSource, 'LICENSES\dotnet-runtime-MIT.txt'),
    @($runtimeNoticesSource, 'LICENSES\dotnet-runtime-ThirdPartyNotices.txt')
)) {
    $runtimeLegalBytes = [IO.File]::ReadAllBytes($runtimeLegalFile[0])
    $runtimeLegalPayloads.Add([pscustomobject]@{
        RelativePath = $runtimeLegalFile[1]
        Bytes = $runtimeLegalBytes
    })
}

Add-InventoryRow '.NET runtime' $runtimePackName $runtimeVersion 'MIT' "https://www.nuget.org/packages/$runtimePackName/$runtimeVersion"

foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
    $library = $libraryProperty.Value
    if ($library.type -ne 'package') { continue }

    $separator = $libraryProperty.Name.LastIndexOf('/')
    $name = $libraryProperty.Name.Substring(0, $separator)
    $version = $libraryProperty.Name.Substring($separator + 1)
    $packageDirectory = Resolve-NuGetPackageDirectory $name $version
    $nuspecPath = Join-Path $packageDirectory ($name.ToLowerInvariant() + '.nuspec')
    $license = ''
    $copyright = ''
    $source = "https://www.nuget.org/packages/$name/$version"
    if (Test-Path $nuspecPath -PathType Leaf) {
        [xml]$nuspec = Get-Content $nuspecPath -Raw
        $metadata = $nuspec.package.metadata
        if ($metadata.license) {
            $license = [string]$metadata.license.InnerText
        }
        elseif ($metadata.licenseUrl) {
            $license = [string]$metadata.licenseUrl
        }
        if ($metadata.projectUrl) { $source = [string]$metadata.projectUrl }
        if ($metadata.copyright) { $copyright = [string]$metadata.copyright }
        elseif ($metadata.authors) { $copyright = "Copyright holders: $([string]$metadata.authors)" }
    }

    Add-InventoryRow 'NuGet' $name $version $license $source
    if ((Add-PackageLegalFiles 'NuGet' $name $version $packageDirectory) -eq 0) {
        Add-LicenseFallback 'NuGet' $name $version $license $copyright
    }
}

$webRoot = Join-Path $projectRootPath 'src\CodexU.Web'
$nodeModules = Join-Path $webRoot 'node_modules'
if (-not (Test-Path $nodeModules -PathType Container)) {
    throw "Installed npm dependencies not found. Run npm ci first: $nodeModules"
}

$npmErrorPath = [IO.Path]::GetTempFileName()
try {
    $npmTreeJson = & npm.cmd ls --prefix $webRoot --omit=dev --all --json 2> $npmErrorPath | Out-String
    $npmExitCode = $LASTEXITCODE
    $npmError = Get-Content -LiteralPath $npmErrorPath -Raw -ErrorAction SilentlyContinue
}
finally {
    [IO.File]::Delete($npmErrorPath)
}
if ($npmExitCode -ne 0) {
    throw "npm production dependency graph is invalid (exit $npmExitCode).`n$npmError`n$npmTreeJson"
}
$npmTree = $npmTreeJson | ConvertFrom-Json
if ($npmTree.PSObject.Properties['problems'] -and @($npmTree.problems).Count -gt 0) {
    throw "npm production dependency graph reports problems: $(@($npmTree.problems) -join '; ')"
}
$productionPackages = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
function Add-ProductionDependencies($Dependencies) {
    if (-not $Dependencies) { return }
    foreach ($property in $Dependencies.PSObject.Properties) {
        $dependency = $property.Value
        if ($dependency.extraneous -eq $true) { continue }
        if ($dependency.version) {
            [void]$productionPackages.Add("$($property.Name)@$($dependency.version)")
        }
        Add-ProductionDependencies $dependency.dependencies
    }
}
Add-ProductionDependencies $npmTree.dependencies

$seenNpmPackages = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($packageJsonFile in Get-ChildItem $nodeModules -Filter package.json -File -Recurse) {
    $package = Get-Content $packageJsonFile.FullName -Raw -Encoding utf8 | ConvertFrom-Json
    $packageKey = "$($package.name)@$($package.version)"
    if (-not $productionPackages.Contains($packageKey) -or -not $seenNpmPackages.Add($packageKey)) { continue }

    $license = if ($package.license -is [string]) { $package.license } else { ($package.license.type -join ', ') }
    $source = [string]$package.homepage
    if ([string]::IsNullOrWhiteSpace($source) -and $package.repository) {
        $source = if ($package.repository -is [string]) { $package.repository } else { [string]$package.repository.url }
    }
    $source = $source -replace '^git\+', '' -replace '\.git$', ''
    Add-InventoryRow 'npm' ([string]$package.name) ([string]$package.version) $license $source

    $packageDirectory = $packageJsonFile.DirectoryName
    if ((Add-PackageLegalFiles 'npm' ([string]$package.name) ([string]$package.version) $packageDirectory) -eq 0) {
        $copyright = if ($package.author -is [string]) { [string]$package.author } else { [string]$package.author.name }
        Add-LicenseFallback 'npm' ([string]$package.name) ([string]$package.version) $license $copyright
    }
}

$electronHostRoot = Join-Path $projectRootPath 'src\CodexU.Electron'
$electronHostPackageJsonPath = Join-Path $electronHostRoot 'package.json'
$electronRoot = Join-Path $electronHostRoot 'node_modules\electron'
$electronPackageJsonPath = Join-Path $electronRoot 'package.json'
if (-not (Test-Path $electronHostPackageJsonPath -PathType Leaf)) {
    throw "Electron host package manifest not found: $electronHostPackageJsonPath"
}
if (-not (Test-Path $electronPackageJsonPath -PathType Leaf)) {
    throw "Installed Electron runtime metadata not found. Run npm ci first: $electronPackageJsonPath"
}

$electronHostPackage = Get-Content $electronHostPackageJsonPath -Raw -Encoding utf8 | ConvertFrom-Json
$electronPackage = Get-Content $electronPackageJsonPath -Raw -Encoding utf8 | ConvertFrom-Json
$electronDeclaredVersion = [string]$electronHostPackage.devDependencies.electron
$electronVersion = [string]$electronPackage.version
$electronLicense = [string]$electronPackage.license
$electronSource = if ($electronPackage.repository -is [string]) {
    [string]$electronPackage.repository
} else {
    [string]$electronPackage.repository.url
}
$electronSource = $electronSource -replace '^git\+', '' -replace '\.git$', ''
if ([string]::IsNullOrWhiteSpace($electronVersion)) {
    throw "Installed Electron package has no version: $electronPackageJsonPath"
}
if ($electronDeclaredVersion -ne $electronVersion) {
    throw "Electron must be pinned to the exact installed runtime version. package.json declares '$electronDeclaredVersion', but npm installed '$electronVersion'."
}
if ($electronLicense -ne 'MIT') {
    throw "Expected the installed Electron runtime to declare the MIT license, but found '$electronLicense'."
}
if ([string]::IsNullOrWhiteSpace($electronSource)) {
    throw "Installed Electron package has no repository/source URL: $electronPackageJsonPath"
}

Add-InventoryRow 'Electron' 'electron' $electronVersion $electronLicense $electronSource
if ((Add-PackageLegalFiles 'Electron' 'electron' $electronVersion $electronRoot) -eq 0) {
    throw "Installed Electron runtime has no bundled LICENSE file: $electronRoot"
}

$standardMitBody = @'
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
'@
$apacheReferencePath = Join-Path $projectRootPath 'LICENSES\Apache-2.0.txt'
$apacheText = if (Test-Path $apacheReferencePath -PathType Leaf) {
    Get-Content -LiteralPath $apacheReferencePath -Raw -Encoding utf8
} else { '' }

foreach ($fallback in $licenseFallbacks) {
    switch -Regex ($fallback.License) {
        '^MIT$' {
            $notice = if ([string]::IsNullOrWhiteSpace($fallback.Copyright)) {
                "Copyright holders: $($fallback.Name) contributors"
            } else { $fallback.Copyright }
            Add-LicenseDocument $fallback.Ecosystem $fallback.Name $fallback.Version 'SPDX-MIT.txt' "MIT License`n`n$notice`n`n$standardMitBody"
            continue
        }
        '^Apache-2\.0$' {
            if ([string]::IsNullOrWhiteSpace($apacheText)) {
                throw "No complete Apache-2.0 reference text is available for $($fallback.Name) $($fallback.Version)."
            }
            $notice = if ([string]::IsNullOrWhiteSpace($fallback.Copyright)) {
                "Copyright holders: $($fallback.Name) contributors"
            } else { $fallback.Copyright }
            Add-LicenseDocument $fallback.Ecosystem $fallback.Name $fallback.Version 'SPDX-Apache-2.0.txt' "$notice`n`n$apacheText"
            continue
        }
        default {
            throw "$($fallback.Ecosystem) package $($fallback.Name) $($fallback.Version) has no bundled legal document and unsupported license '$($fallback.License)'."
        }
    }
}

function Escape-MarkdownCell([string]$Value) {
    return ($Value -replace '\|', '\|') -replace "`r?`n", ' '
}

function Install-GeneratedFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if ([IO.File]::Exists($Destination)) {
        $backupPath = $Destination + '.backup-' + [Guid]::NewGuid().ToString('N')
        try {
            [IO.File]::Replace($Source, $Destination, $backupPath)
        }
        finally {
            if ([IO.File]::Exists($backupPath)) {
                [IO.File]::Delete($backupPath)
            }
        }
    } else {
        [IO.File]::Move($Source, $Destination)
    }
}

if ($rows.Where({ $_.License -eq 'UNKNOWN - review required' }).Count -gt 0) {
    throw 'One or more shipped dependencies have no declared license.'
}

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Shipped dependency inventory')
$lines.Add('')
$lines.Add('Generated by `tools/Generate-ThirdPartyInventory.ps1` from the restored .NET Sidecar assets, the `npm ls --omit=dev` Web dependency graph and the installed Electron runtime metadata. Build/test dependencies absent from that graph are excluded; optional peer packages retained by npm are included conservatively.')
$lines.Add('')
$lines.Add('| Ecosystem | Package | Version | Declared license | Project/source |')
$lines.Add('| --- | --- | --- | --- | --- |')
foreach ($row in ($rows | Sort-Object Ecosystem, Name, Version -Unique)) {
    $lines.Add("| $(Escape-MarkdownCell $row.Ecosystem) | $(Escape-MarkdownCell $row.Name) | $(Escape-MarkdownCell $row.Version) | $(Escape-MarkdownCell $row.License) | $(Escape-MarkdownCell $row.Source) |")
}
$lines.Add('')
$lines.Add('Complete package license and notice texts, including Electron''s MIT license, are distributed in `THIRD-PARTY-LICENSES.txt`. The packaged Electron application retains `LICENSES.chromium.html` at its root as the complete Chromium and bundled third-party notice set. Self-contained .NET runtime notices and separately retained upstream/build-tool licenses are distributed in `resources/LICENSES/`.')
$lines.Add('')
$lines.Add('`UNKNOWN - review required` is a release blocker; it must be resolved before distribution.')

$licenseLines = New-Object System.Collections.Generic.List[string]
$licenseLines.Add('THIRD-PARTY LICENSES AND NOTICES')
$licenseLines.Add('')
$licenseLines.Add('Generated from the restored .NET Sidecar NuGet packages, the production Web npm dependency graph and the installed Electron runtime.')
$licenseLines.Add('The packaged Electron application also retains LICENSES.chromium.html at its root as the complete Chromium and bundled third-party notice set.')
$licenseLines.Add('Identical legal documents are printed once and list every component to which they apply.')
$licenseLines.Add('')
foreach ($group in ($licenseDocuments | Group-Object Hash | Sort-Object Name)) {
    $licenseLines.Add(('=' * 78))
    $licenseLines.Add('Components:')
    foreach ($document in ($group.Group | Sort-Object Ecosystem, Name, Version, DocumentName)) {
        $licenseLines.Add("- $($document.Ecosystem): $($document.Name) $($document.Version) [$($document.DocumentName)]")
    }
    $licenseLines.Add('')
    foreach ($textLine in ($group.Group[0].Text.TrimEnd() -split "`n")) {
        $licenseLines.Add($textLine)
    }
    $licenseLines.Add('')
}

if ($licenseLines.Count -gt 0 -and $licenseLines[$licenseLines.Count - 1] -eq '') {
    $licenseLines.RemoveAt($licenseLines.Count - 1)
}

[IO.Directory]::CreateDirectory($outputRootPath) | Out-Null
$stagingRoot = Join-Path $outputRootPath ('.codexu-legal-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($stagingRoot) | Out-Null
try {
    $stagedInventoryPath = Join-Path $stagingRoot 'THIRD-PARTY-INVENTORY.md'
    $stagedLicensesPath = Join-Path $stagingRoot 'THIRD-PARTY-LICENSES.txt'
    [IO.File]::WriteAllLines($stagedInventoryPath, $lines, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllLines($stagedLicensesPath, $licenseLines, [Text.UTF8Encoding]::new($false))

    foreach ($runtimeLegalPayload in $runtimeLegalPayloads) {
        $stagedRuntimePath = Join-Path $stagingRoot $runtimeLegalPayload.RelativePath
        [IO.Directory]::CreateDirectory((Split-Path -Parent $stagedRuntimePath)) | Out-Null
        [IO.File]::WriteAllBytes($stagedRuntimePath, $runtimeLegalPayload.Bytes)
    }

    $outputPath = Join-Path $outputRootPath 'THIRD-PARTY-INVENTORY.md'
    $licensesOutputPath = Join-Path $outputRootPath 'THIRD-PARTY-LICENSES.txt'
    $retainedLicensesDirectory = Join-Path $outputRootPath 'LICENSES'
    [IO.Directory]::CreateDirectory($retainedLicensesDirectory) | Out-Null

    foreach ($generatedFile in @(
        @($stagedInventoryPath, $outputPath),
        @($stagedLicensesPath, $licensesOutputPath),
        @((Join-Path $stagingRoot 'LICENSES\dotnet-runtime-MIT.txt'), (Join-Path $retainedLicensesDirectory 'dotnet-runtime-MIT.txt')),
        @((Join-Path $stagingRoot 'LICENSES\dotnet-runtime-ThirdPartyNotices.txt'), (Join-Path $retainedLicensesDirectory 'dotnet-runtime-ThirdPartyNotices.txt'))
    )) {
        Install-GeneratedFile $generatedFile[0] $generatedFile[1]
    }
}
finally {
    if (Test-Path -LiteralPath $stagingRoot -PathType Container) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}

Write-Host "Wrote $outputPath with $($rows.Count) dependency entries."
Write-Host "Wrote $licensesOutputPath with $($licenseDocuments.Count) package legal documents."
