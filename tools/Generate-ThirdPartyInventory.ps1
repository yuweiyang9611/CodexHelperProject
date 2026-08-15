param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$projectRootPath = (Resolve-Path $ProjectRoot).Path
$rows = New-Object System.Collections.Generic.List[object]
$licenseDocuments = New-Object System.Collections.Generic.List[object]
$licenseFallbacks = New-Object System.Collections.Generic.List[object]

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
        $_.Name -match '^(LICENSE|LICENCE|NOTICE|COPYING)(\..*)?$'
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

$assetsPath = Join-Path $projectRootPath 'src\CodexU.App\obj\project.assets.json'
if (-not (Test-Path $assetsPath -PathType Leaf)) {
    throw "NuGet assets file not found. Run dotnet restore first: $assetsPath"
}

$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
$packageFolder = @($assets.packageFolders.PSObject.Properties.Name)[0]
foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
    $library = $libraryProperty.Value
    if ($library.type -ne 'package') { continue }

    $separator = $libraryProperty.Name.LastIndexOf('/')
    $name = $libraryProperty.Name.Substring(0, $separator)
    $version = $libraryProperty.Name.Substring($separator + 1)
    $packageDirectory = Join-Path (Join-Path $packageFolder $name.ToLowerInvariant()) $version
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

$npmTreeJson = & npm.cmd ls --prefix $webRoot --omit=dev --all --json 2>$null | Out-String
$npmTree = $npmTreeJson | ConvertFrom-Json
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
            Add-LicenseDocument $fallback.Ecosystem $fallback.Name $fallback.Version 'SPDX-Apache-2.0.txt' $apacheText
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

$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('# Shipped dependency inventory')
$lines.Add('')
$lines.Add('Generated by `tools/Generate-ThirdPartyInventory.ps1` from the restored application assets and production npm dependency graph. Build-only and test-only packages are excluded.')
$lines.Add('')
$lines.Add('| Ecosystem | Package | Version | Declared license | Project/source |')
$lines.Add('| --- | --- | --- | --- | --- |')
foreach ($row in ($rows | Sort-Object Ecosystem, Name, Version -Unique)) {
    $lines.Add("| $(Escape-MarkdownCell $row.Ecosystem) | $(Escape-MarkdownCell $row.Name) | $(Escape-MarkdownCell $row.Version) | $(Escape-MarkdownCell $row.License) | $(Escape-MarkdownCell $row.Source) |")
}
$lines.Add('')
$lines.Add('Complete package license and notice texts are distributed in `THIRD-PARTY-LICENSES.txt`. Self-contained .NET runtime notices and separately retained upstream/build-tool licenses are distributed in `LICENSES/`.')
$lines.Add('')
$lines.Add('`UNKNOWN - review required` is a release blocker; it must be resolved before distribution.')

$outputPath = Join-Path $projectRootPath 'THIRD-PARTY-INVENTORY.md'
[IO.File]::WriteAllLines($outputPath, $lines, [Text.UTF8Encoding]::new($false))

$licenseLines = New-Object System.Collections.Generic.List[string]
$licenseLines.Add('THIRD-PARTY LICENSES AND NOTICES')
$licenseLines.Add('')
$licenseLines.Add('Generated from the restored NuGet packages and the production npm dependency graph.')
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

$licensesOutputPath = Join-Path $projectRootPath 'THIRD-PARTY-LICENSES.txt'
if ($licenseLines.Count -gt 0 -and $licenseLines[$licenseLines.Count - 1] -eq '') {
    $licenseLines.RemoveAt($licenseLines.Count - 1)
}
[IO.File]::WriteAllLines($licensesOutputPath, $licenseLines, [Text.UTF8Encoding]::new($false))
Write-Host "Wrote $outputPath with $($rows.Count) dependency entries."
Write-Host "Wrote $licensesOutputPath with $($licenseDocuments.Count) package legal documents."
if ($rows.Where({ $_.License -eq 'UNKNOWN - review required' }).Count -gt 0) {
    throw 'One or more shipped dependencies have no declared license.'
}
