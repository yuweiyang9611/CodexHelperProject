[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

# Run after Generate-ThirdPartyInventory.ps1 has rewritten the legal files in
# place. The self-contained Sidecar ships the .NET runtime that comes with the
# building SDK, so the inventory's runtime row may name another patch of the
# recorded major.minor runtime, and the runtime pack's notices may change with
# it. Any other difference from the committed files fails.

$projectRoot = Split-Path -Parent $PSScriptRoot
$inventoryPath = 'THIRD-PARTY-INVENTORY.md'
$runtimeNoticesPath = 'LICENSES/dotnet-runtime-ThirdPartyNotices.txt'
$legalPaths = @($inventoryPath, 'THIRD-PARTY-LICENSES.txt', 'LICENSES/dotnet-runtime-MIT.txt', $runtimeNoticesPath)
$runtimeRowPattern = '^\| \.NET runtime \| Microsoft\.NETCore\.App\.Runtime\.win-x64 \| (?<version>(?<majorMinor>\d+\.\d+)\.\d+) \| '

function Invoke-Git {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $ErrorActionPreference = 'Continue'
    $output = @(& git -C $projectRoot -c core.safecrlf=false @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
    return $output
}

function Get-RuntimeRow {
    param([string]$Line)
    $match = [regex]::Match($Line, $runtimeRowPattern)
    if (-not $match.Success) { return $null }
    $version = $match.Groups['version'].Value
    $majorMinor = $match.Groups['majorMinor'].Value
    return [pscustomobject]@{
        Version = $version
        MajorMinor = $majorMinor
        Normalized = $Line.Replace($version, $majorMinor)
    }
}

$changedPaths = @(Invoke-Git (@('diff', '--name-only', '--') + $legalPaths))
if ($changedPaths.Count -eq 0) {
    Write-Output 'Generated legal files match the committed copies.'
    return
}

$recorded = $null
$generated = $null
if ($changedPaths -contains $inventoryPath) {
    $removed = New-Object System.Collections.Generic.List[string]
    $added = New-Object System.Collections.Generic.List[string]
    $inHunk = $false
    foreach ($line in (Invoke-Git @('diff', '--unified=0', '--no-color', '--no-ext-diff', '--', $inventoryPath))) {
        if ($line.StartsWith('@@')) { $inHunk = $true; continue }
        if (-not $inHunk) { continue }
        if ($line.StartsWith('-')) { $removed.Add($line.Substring(1)) }
        elseif ($line.StartsWith('+')) { $added.Add($line.Substring(1)) }
    }
    if ($removed.Count -eq 1 -and $added.Count -eq 1) {
        $recorded = Get-RuntimeRow $removed[0]
        $generated = Get-RuntimeRow $added[0]
    }
}

$runtimePatchDrift = $null -ne $recorded -and $null -ne $generated -and
    $recorded.MajorMinor -eq $generated.MajorMinor -and
    $recorded.Normalized -ceq $generated.Normalized
$unexpectedPaths = @($changedPaths | Where-Object {
    -not ($runtimePatchDrift -and ($_ -eq $inventoryPath -or $_ -eq $runtimeNoticesPath))
})
if ($unexpectedPaths.Count -gt 0) {
    throw "Generated legal files differ from the committed copies beyond a .NET runtime patch: $($unexpectedPaths -join ', '). Run tools/Generate-ThirdPartyInventory.ps1 and commit the reviewed result."
}

Write-Output "Accepted .NET runtime patch drift: committed $($recorded.Version), generated $($generated.Version) (.NET $($recorded.MajorMinor)). Changed: $($changedPaths -join ', ')."
