#Requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ApplicationDirectory,

    [ValidateRange(10, 600)]
    [int]$TimeoutSeconds = 90
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$buildPropsPath = Join-Path $projectRoot 'Directory.Build.props'
[xml]$buildProps = Get-Content -LiteralPath $buildPropsPath -Raw
$expectedBackendVersion = ([string]$buildProps.Project.PropertyGroup.Version).Trim()
if ($expectedBackendVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Directory.Build.props contains an invalid product version: '$expectedBackendVersion'."
}

function Assert-NonEmptyFile {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not [System.IO.File]::Exists($Path)) {
        throw "$Description is missing: '$Path'."
    }

    if ([System.IO.FileInfo]::new($Path).Length -le 0) {
        throw "$Description is empty: '$Path'."
    }
}

function Assert-FileContains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Pattern,

        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if (-not (Select-String -LiteralPath $Path -Pattern $Pattern -Quiet)) {
        throw $FailureMessage
    }
}

function Get-ProcessesForExecutablePaths {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ExecutablePaths
    )

    $normalizedPaths = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    $processNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($executablePath in $ExecutablePaths) {
        [void]$normalizedPaths.Add([System.IO.Path]::GetFullPath($executablePath))
        [void]$processNames.Add([System.IO.Path]::GetFileNameWithoutExtension($executablePath))
    }

    $matches = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($processName in $processNames) {
        foreach ($candidate in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
            try {
                if ($normalizedPaths.Contains([System.IO.Path]::GetFullPath($candidate.Path))) {
                    $matches.Add($candidate)
                }
                else {
                    $candidate.Dispose()
                }
            }
            catch {
                # A process whose executable path cannot be inspected is not safe to
                # identify as one of this test's processes.
                $candidate.Dispose()
            }
        }
    }

    return $matches.ToArray()
}

function Stop-ProcessTree {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process]$Process
    )

    try {
        $Process.Refresh()
        if ($Process.HasExited) {
            return
        }

        $Process.Kill($true)
        if (-not $Process.WaitForExit(15000)) {
            throw "Process tree rooted at PID $($Process.Id) did not exit after termination."
        }
    }
    catch [System.InvalidOperationException] {
        # The process exited between Refresh and Kill.
    }
}

function Stop-AndReportResidualProcesses {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$ExecutablePaths,

        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$Deadline
    )

    do {
        $remaining = @(Get-ProcessesForExecutablePaths -ExecutablePaths $ExecutablePaths)
        if ($remaining.Count -eq 0) {
            return
        }

        foreach ($candidate in $remaining) {
            $candidate.Dispose()
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)

    $remaining = @(Get-ProcessesForExecutablePaths -ExecutablePaths $ExecutablePaths)
    if ($remaining.Count -eq 0) {
        return
    }

    $processIds = @($remaining | ForEach-Object { $_.Id })
    foreach ($candidate in $remaining) {
        try {
            Stop-ProcessTree -Process $candidate
        }
        finally {
            $candidate.Dispose()
        }
    }

    throw "Packaged Electron smoke test leaked process PID(s): $($processIds -join ', ')."
}

function Get-RemainingMilliseconds {
    param([Parameter(Mandatory = $true)][DateTimeOffset]$Deadline)

    $remaining = ($Deadline - [DateTimeOffset]::UtcNow).TotalMilliseconds
    if ($remaining -le 0) {
        return 0
    }

    return [Math]::Min([int]::MaxValue, [Math]::Ceiling($remaining))
}

function Receive-TextTaskBeforeDeadline {
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]]$Task,

        [Parameter(Mandatory = $true)]
        [DateTimeOffset]$Deadline,

        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    $remainingMilliseconds = Get-RemainingMilliseconds -Deadline $Deadline
    if (-not $Task.Wait($remainingMilliseconds)) {
        throw "$Description did not close before the smoke-test deadline."
    }

    return $Task.GetAwaiter().GetResult()
}

function Remove-UniqueSmokeDirectory {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedTempRoot
    )

    $fullPath = [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $tempRoot = [System.IO.Path]::GetFullPath($ExpectedTempRoot).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $parent = [System.IO.Directory]::GetParent($fullPath)
    $leaf = [System.IO.Path]::GetFileName($fullPath)

    if ($null -eq $parent -or
        -not [string]::Equals($parent.FullName, $tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        $leaf -notmatch '^codexu-electron-smoke-[0-9a-f]{32}$') {
        throw "Refusing to remove an unexpected smoke-test path: '$fullPath'."
    }

    if ([System.IO.Directory]::Exists($fullPath)) {
        [System.IO.Directory]::Delete($fullPath, $true)
    }
}

$applicationRoot = (Resolve-Path -LiteralPath $ApplicationDirectory).Path
if (-not [System.IO.Directory]::Exists($applicationRoot)) {
    throw "Packaged Electron application directory was not found: '$ApplicationDirectory'."
}

$requiredFiles = [ordered]@{
    'CodexU.exe' = 'Electron application executable'
    'LICENSE' = 'Electron license'
    'LICENSES.chromium.html' = 'Chromium license inventory'
    'resources\app.asar' = 'Electron ASAR archive'
    'resources\dist\index.html' = 'Vue renderer entry point'
    'resources\backend\CodexU.Sidecar.exe' = '.NET sidecar executable'
    'resources\Assets\AppIcon.ico' = 'Windows application icon'
    'resources\Assets\AppIcon.png' = 'tray icon'
    'resources\LICENSE' = 'codexU project license'
    'resources\THIRD-PARTY-NOTICES.md' = 'third-party notices'
    'resources\THIRD-PARTY-INVENTORY.md' = 'third-party dependency inventory'
    'resources\THIRD-PARTY-LICENSES.txt' = 'third-party license texts'
    'resources\LICENSES\Apache-2.0.txt' = 'Apache 2.0 reference license'
    'resources\LICENSES\dotnet-runtime-MIT.txt' = '.NET runtime MIT license'
    'resources\LICENSES\dotnet-runtime-ThirdPartyNotices.txt' = '.NET runtime notices'
    'resources\LICENSES\Inno-Setup-license.txt' = 'Inno Setup license'
    'resources\LICENSES\shanggqm-codexU-MIT.txt' = 'shanggqm/codexU license'
    'resources\LICENSES\liu-codexU-windows-MIT.txt' = 'codexU-windows license'
}

foreach ($entry in $requiredFiles.GetEnumerator()) {
    Assert-NonEmptyFile `
        -Path (Join-Path $applicationRoot $entry.Key) `
        -Description $entry.Value
}

$inventoryPath = Join-Path $applicationRoot 'resources\THIRD-PARTY-INVENTORY.md'
$noticesPath = Join-Path $applicationRoot 'resources\THIRD-PARTY-NOTICES.md'
$projectLicensePath = Join-Path $applicationRoot 'resources\LICENSE'
$electronLicensePath = Join-Path $applicationRoot 'LICENSE'
$chromiumLicensesPath = Join-Path $applicationRoot 'LICENSES.chromium.html'

$inventoryRows = [System.Collections.Generic.List[object]]::new()
foreach ($line in Get-Content -LiteralPath $inventoryPath) {
    if ($line -notmatch '^\|') {
        continue
    }

    $cells = @($line.Trim('|').Split('|') | ForEach-Object { $_.Trim() })
    if ($cells.Count -lt 5 -or $cells[0] -in @('Ecosystem', '---')) {
        continue
    }

    $inventoryRows.Add([pscustomobject]@{
        Ecosystem = $cells[0]
        Package = $cells[1]
        Version = $cells[2]
        License = $cells[3]
        Source = $cells[4]
    })
}
$electronInventoryRows = @($inventoryRows | Where-Object {
    $_.Ecosystem -eq 'Electron' -and
    $_.Package -eq 'electron' -and
    $_.Version -match '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$' -and
    $_.License -eq 'MIT'
})
if ($electronInventoryRows.Count -ne 1) {
    throw 'Third-party inventory must contain exactly one versioned MIT Electron runtime row.'
}
$unresolvedRows = @($inventoryRows | Where-Object {
    [string]::IsNullOrWhiteSpace($_.License) -or
    $_.License -match '(?i)\b(?:UNKNOWN|NOASSERTION|UNLICENSED)\b'
})
if ($unresolvedRows.Count -gt 0) {
    $unresolvedPackages = @($unresolvedRows | ForEach-Object { "$($_.Ecosystem):$($_.Package)" })
    throw "Third-party inventory contains unresolved license entries: $($unresolvedPackages -join ', ')."
}
Assert-FileContains $noticesPath 'LICENSES\.chromium\.html' `
    'Third-party notices do not identify the packaged Chromium license inventory.'
Assert-FileContains $projectLicensePath '^MIT License' `
    'The packaged codexU project license is not the expected MIT license.'
Assert-FileContains $electronLicensePath '(?i)Electron contributors' `
    'The package-root Electron license is missing its expected attribution.'
Assert-FileContains $chromiumLicensesPath '(?i)Chromium' `
    'The packaged Chromium license inventory has unexpected content.'

$forbiddenEntries = @(Get-ChildItem -LiteralPath $applicationRoot -Recurse -Force | Where-Object {
    ($_.PSIsContainer -and $_.Name -eq '.git') -or
    (-not $_.PSIsContainer -and (
        $_.Extension -ieq '.pdb' -or
        $_.Name -ieq '.gitignore' -or
        $_.Name -ieq '.gitkeep'))
})
if ($forbiddenEntries.Count -gt 0) {
    $relativePaths = @($forbiddenEntries | ForEach-Object {
        [System.IO.Path]::GetRelativePath($applicationRoot, $_.FullName)
    })
    throw "Packaged Electron payload contains forbidden development files: $($relativePaths -join ', ')."
}

$applicationExecutable = Join-Path $applicationRoot 'CodexU.exe'
$sidecarExecutable = Join-Path $applicationRoot 'resources\backend\CodexU.Sidecar.exe'
$targetExecutablePaths = @($applicationExecutable, $sidecarExecutable)
$alreadyRunning = @(Get-ProcessesForExecutablePaths -ExecutablePaths $targetExecutablePaths)
if ($alreadyRunning.Count -gt 0) {
    $details = @($alreadyRunning | ForEach-Object { "PID=$($_.Id), Path=$($_.Path)" })
    foreach ($candidate in $alreadyRunning) {
        $candidate.Dispose()
    }
    throw "The target Electron package is already running; close it before smoke testing.`r`n$($details -join "`r`n")"
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$smokeRoot = Join-Path $tempRoot ("codexu-electron-smoke-{0}" -f [Guid]::NewGuid().ToString('N'))
if ([System.IO.Directory]::Exists($smokeRoot) -or [System.IO.File]::Exists($smokeRoot)) {
    throw "Unique Electron smoke-test path already exists: '$smokeRoot'."
}
[System.IO.Directory]::CreateDirectory($smokeRoot) | Out-Null

$process = $null
$standardOutputTask = $null
$standardErrorTask = $null
$standardOutput = ''
$standardError = ''
$testFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()

try {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $applicationExecutable
    $startInfo.WorkingDirectory = $applicationRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add('--smoke-test')
    $startInfo.Environment['CODEXU_DATA_DIRECTORY'] = Join-Path $smokeRoot 'data'
    $startInfo.Environment['CODEXU_ELECTRON_USER_DATA_DIRECTORY'] = Join-Path $smokeRoot 'electron-user-data'

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        throw 'Packaged Electron process could not be started.'
    }

    $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
    $standardErrorTask = $process.StandardError.ReadToEndAsync()
    $smokeDeadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    if (-not $process.WaitForExit((Get-RemainingMilliseconds -Deadline $smokeDeadline))) {
        $processId = $process.Id
        Stop-ProcessTree -Process $process
        throw "Packaged Electron smoke test timed out after $TimeoutSeconds seconds (PID $processId)."
    }
    $process.WaitForExit()

    Stop-AndReportResidualProcesses `
        -ExecutablePaths $targetExecutablePaths `
        -Deadline $smokeDeadline
    $standardOutput = Receive-TextTaskBeforeDeadline `
        -Task $standardOutputTask `
        -Deadline $smokeDeadline `
        -Description 'Electron standard-output stream'
    $standardError = Receive-TextTaskBeforeDeadline `
        -Task $standardErrorTask `
        -Deadline $smokeDeadline `
        -Description 'Electron standard-error stream'
    if ($process.ExitCode -ne 0) {
        throw "Packaged Electron smoke test failed with exit code $($process.ExitCode)."
    }
    $successPattern = '^CODEXU_ELECTRON_SMOKE_OK: app-loaded backend=(?<backend>\S+) host-state=false reverse-rpc=rates\.export-cancelled$'
    $successLines = @($standardOutput -split '\r?\n' | Where-Object {
        $_ -match $successPattern
    })
    if ($successLines.Count -ne 1) {
        throw 'Packaged Electron smoke test did not report exactly one complete success record with safe host state and reverse-RPC cancellation.'
    }
    $successMatch = [Regex]::Match($successLines[0], $successPattern)
    $reportedBackendVersion = $successMatch.Groups['backend'].Value
    if (-not [string]::Equals(
        $reportedBackendVersion,
        $expectedBackendVersion,
        [StringComparison]::Ordinal)) {
        throw "Packaged Electron smoke test reported backend version '$reportedBackendVersion'; expected '$expectedBackendVersion'."
    }

    $unexpectedExports = @(Get-ChildItem `
        -LiteralPath $smokeRoot `
        -Recurse `
        -File `
        -Filter 'codexU-rate-catalog-*.json')
    if ($unexpectedExports.Count -gt 0) {
        $exportPaths = @($unexpectedExports | ForEach-Object { $_.FullName })
        throw "Packaged Electron smoke test unexpectedly wrote rate-catalog exports: $($exportPaths -join ', ')."
    }

}
catch {
    $testFailure = $_.Exception
}
finally {
    if ($null -ne $process) {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                Stop-ProcessTree -Process $process
            }
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }

    try {
        Stop-AndReportResidualProcesses `
            -ExecutablePaths $targetExecutablePaths `
            -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(15))
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }

    try {
        $outputCleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(5)
        if ($null -ne $standardOutputTask -and $standardOutput.Length -eq 0) {
            $standardOutput = Receive-TextTaskBeforeDeadline `
                -Task $standardOutputTask `
                -Deadline $outputCleanupDeadline `
                -Description 'Electron standard-output stream cleanup'
        }
        if ($null -ne $standardErrorTask -and $standardError.Length -eq 0) {
            $standardError = Receive-TextTaskBeforeDeadline `
                -Task $standardErrorTask `
                -Deadline $outputCleanupDeadline `
                -Description 'Electron standard-error stream cleanup'
        }
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }

    if ($null -ne $process) {
        $process.Dispose()
    }

    try {
        Remove-UniqueSmokeDirectory -Path $smokeRoot -ExpectedTempRoot $tempRoot
    }
    catch {
        $cleanupFailures.Add($_.Exception)
    }
}

if (-not [string]::IsNullOrWhiteSpace($standardOutput)) {
    Write-Host '[Electron smoke stdout]'
    Write-Host $standardOutput.TrimEnd()
}
if (-not [string]::IsNullOrWhiteSpace($standardError)) {
    Write-Host '[Electron smoke stderr]'
    Write-Host $standardError.TrimEnd()
}

$failureMessages = [System.Collections.Generic.List[string]]::new()
if ($null -ne $testFailure) {
    $failureMessages.Add($testFailure.Message)
}
foreach ($cleanupFailure in $cleanupFailures) {
    $failureMessages.Add("Electron smoke-test cleanup failed: $($cleanupFailure.Message)")
}
if ($failureMessages.Count -gt 0) {
    $innerException = if ($null -ne $testFailure) { $testFailure } else { $cleanupFailures[0] }
    throw [System.InvalidOperationException]::new(
        ($failureMessages -join "`r`n"),
        $innerException)
}

Write-Host "Packaged Electron application verified: '$applicationRoot'."
