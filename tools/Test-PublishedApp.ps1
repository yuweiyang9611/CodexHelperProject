param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,
    [int]$TimeoutSeconds = 35,
    [switch]$VerifyStatusStrip
)

$ErrorActionPreference = 'Stop'

function Get-SettingsFingerprint {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            Length = $null
            LastWriteTimeUtcTicks = $null
            Sha256 = $null
        }
    }

    $item = Get-Item -LiteralPath $Path
    return [pscustomobject]@{
        Exists = $true
        Length = $item.Length
        LastWriteTimeUtcTicks = $item.LastWriteTimeUtc.Ticks
        Sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    }
}

function Assert-SettingsFingerprintUnchanged {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    if (($Before.Exists -ne $After.Exists) -or
        ($Before.Length -ne $After.Length) -or
        ($Before.LastWriteTimeUtcTicks -ne $After.LastWriteTimeUtcTicks) -or
        ($Before.Sha256 -ne $After.Sha256)) {
        throw 'Published EXE changed the real user settings.json during smoke verification.'
    }
}

function Get-StartupRegistrationFingerprint {
    $runKeyPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
    $valueName = 'codexU'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runKeyPath, $false)
    if ($null -eq $key) {
        return [pscustomobject]@{
            Exists = $false
            Kind = $null
            Value = $null
        }
    }

    try {
        if (-not ($key.GetValueNames() -contains $valueName)) {
            return [pscustomobject]@{
                Exists = $false
                Kind = $null
                Value = $null
            }
        }

        return [pscustomobject]@{
            Exists = $true
            Kind = $key.GetValueKind($valueName).ToString()
            Value = [Convert]::ToString(
                $key.GetValue(
                    $valueName,
                    $null,
                    [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
        }
    }
    finally {
        $key.Dispose()
    }
}

function Assert-StartupRegistrationFingerprintUnchanged {
    param(
        [Parameter(Mandatory = $true)]$Before,
        [Parameter(Mandatory = $true)]$After
    )

    if (($Before.Exists -ne $After.Exists) -or
        ($Before.Kind -cne $After.Kind) -or
        ($Before.Value -cne $After.Value)) {
        throw 'Published EXE changed HKCU Run\codexU during smoke verification.'
    }
}

function Get-ProcessDescription {
    param([Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process)

    $processPath = '<unavailable>'
    try {
        $candidatePath = $Process.Path
        if (-not [string]::IsNullOrWhiteSpace($candidatePath)) {
            $processPath = $candidatePath
        }
    }
    catch {
        $processPath = "<unavailable: $($_.Exception.Message)>"
    }

    $startTime = '<unavailable>'
    try {
        $startTime = $Process.StartTime.ToUniversalTime().ToString('o')
    }
    catch {
        $startTime = "<unavailable: $($_.Exception.Message)>"
    }

    return "PID=$($Process.Id), Path=$processPath, StartTimeUtc=$startTime"
}

function Assert-NoRunningCodexUApp {
    $runningProcesses = @(Get-Process -Name 'CodexU.App' -ErrorAction SilentlyContinue)
    if ($runningProcesses.Count -eq 0) {
        return
    }

    $details = @(
        $runningProcesses |
            Sort-Object -Property Id |
            ForEach-Object { Get-ProcessDescription -Process $_ }
    )
    throw "A CodexU.App process is already running. Close it before running the published-app smoke test.`r`n$($details -join "`r`n")"
}

function Assert-DirectoryWritable {
    param([Parameter(Mandatory = $true)][string]$Path)

    $probePath = Join-Path $Path ".codexu-smoke-write-$([Guid]::NewGuid().ToString('N')).tmp"
    $stream = $null
    $probeFailure = $null
    $cleanupFailure = $null

    try {
        [System.IO.Directory]::CreateDirectory($Path) | Out-Null
        $stream = [System.IO.File]::Open(
            $probePath,
            [System.IO.FileMode]::CreateNew,
            [System.IO.FileAccess]::Write,
            [System.IO.FileShare]::None)
        $stream.WriteByte(0)
    }
    catch {
        $probeFailure = $_.Exception
    }
    finally {
        if ($null -ne $stream) {
            try {
                $stream.Dispose()
            }
            catch {
                if ($null -eq $cleanupFailure) {
                    $cleanupFailure = $_.Exception
                }
            }
        }

        try {
            if ([System.IO.File]::Exists($probePath)) {
                [System.IO.File]::Delete($probePath)
            }
        }
        catch {
            if ($null -eq $cleanupFailure) {
                $cleanupFailure = $_.Exception
            }
        }
    }

    if ($null -ne $probeFailure) {
        throw [System.InvalidOperationException]::new(
            "The WebView2 data directory '$Path' is not writable. The published app and smoke test must run under a user that can create files there. $($probeFailure.Message)",
            $probeFailure)
    }

    if ($null -ne $cleanupFailure) {
        throw [System.InvalidOperationException]::new(
            "The WebView2 write probe succeeded, but its temporary file '$probePath' could not be removed. $($cleanupFailure.Message)",
            $cleanupFailure)
    }
}

function Copy-SmokeSeedData {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoot,
        [Parameter(Mandatory = $true)][string]$DestinationRoot
    )

    $relativePaths = @(
        'settings.json',
        'settings.json.bak',
        'todos.json',
        'todos.json.bak',
        'update-check.json',
        'session-index-v1.json',
        'claude-code\statusline-snapshot.json'
    )
    foreach ($relativePath in $relativePaths) {
        $sourcePath = Join-Path $SourceRoot $relativePath
        if (-not [System.IO.File]::Exists($sourcePath)) {
            continue
        }

        $destinationPath = Join-Path $DestinationRoot $relativePath
        [System.IO.Directory]::CreateDirectory(
            [System.IO.Path]::GetDirectoryName($destinationPath)) | Out-Null
        [System.IO.File]::Copy($sourcePath, $destinationPath, $false)
    }
}

function Remove-SmokeDataDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Nonce,
        [Parameter(Mandatory = $true)][string]$ExpectedTempRoot
    )

    $directorySeparators = [char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $tempRoot = [System.IO.Path]::GetFullPath(
        $ExpectedTempRoot).TrimEnd($directorySeparators)
    $expectedLeafName = "codexU-smoke-$Nonce"
    $expectedPath = [System.IO.Path]::GetFullPath((Join-Path $tempRoot $expectedLeafName))
    $actualPath = [System.IO.Path]::GetFullPath($Path)
    $actualParent = [System.IO.Directory]::GetParent($actualPath)

    if ($null -eq $actualParent -or
        -not [string]::Equals($actualPath, $expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            $actualParent.FullName.TrimEnd($directorySeparators),
            $tempRoot,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals(
            [System.IO.Path]::GetFileName($actualPath),
            $expectedLeafName,
            [StringComparison]::Ordinal)) {
        throw "Refusing to recursively remove an unexpected smoke data path: '$actualPath'."
    }

    $cleanupDeadline = [DateTime]::UtcNow.AddSeconds(10)
    $lastCleanupFailure = $null
    while ([System.IO.Directory]::Exists($actualPath)) {
        $item = Get-Item -LiteralPath $actualPath -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Refusing to recursively remove a reparse-point smoke data root: '$actualPath'."
        }

        try {
            Remove-Item -LiteralPath $actualPath -Recurse -Force -ErrorAction Stop
            $lastCleanupFailure = $null
        }
        catch {
            $lastCleanupFailure = $_.Exception
        }

        if (-not [System.IO.Directory]::Exists($actualPath)) {
            break
        }
        if ([DateTime]::UtcNow -ge $cleanupDeadline) {
            $detail = if ($null -eq $lastCleanupFailure) {
                'the directory remained present'
            } else {
                $lastCleanupFailure.Message
            }
            throw "Smoke data directory cleanup timed out for '$actualPath': $detail"
        }

        Start-Sleep -Milliseconds 250
    }
    if ([System.IO.Directory]::Exists($actualPath) -or [System.IO.File]::Exists($actualPath)) {
        throw "Smoke data directory still exists after cleanup: '$actualPath'."
    }
}

function Get-StartupLogCheckpoint {
    param([Parameter(Mandatory = $true)][string]$Path)

    try {
        if (-not [System.IO.File]::Exists($Path)) {
            return [pscustomobject]@{
                Exists = $false
                Length = 0L
                LastWriteTimeUtc = $null
                Error = $null
            }
        }

        $item = Get-Item -LiteralPath $Path
        return [pscustomobject]@{
            Exists = $true
            Length = [long]$item.Length
            LastWriteTimeUtc = $item.LastWriteTimeUtc
            Error = $null
        }
    }
    catch {
        return [pscustomobject]@{
            Exists = $null
            Length = 0L
            LastWriteTimeUtc = $null
            Error = $_.Exception.Message
        }
    }
}

function Get-StartupLogDelta {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Checkpoint
    )

    if ($null -ne $Checkpoint.Error) {
        return [pscustomobject]@{
            Text = $null
            Error = "The pre-run startup.log checkpoint could not be read: $($Checkpoint.Error)"
            Rotated = $false
        }
    }

    $stream = $null
    $reader = $null
    try {
        if (-not [System.IO.File]::Exists($Path)) {
            return [pscustomobject]@{
                Text = $null
                Error = $null
                Rotated = $false
            }
        }

        $item = Get-Item -LiteralPath $Path
        $rotated = $Checkpoint.Exists -and $item.Length -lt $Checkpoint.Length
        $offset = if ($Checkpoint.Exists -and -not $rotated) {
            [long]$Checkpoint.Length
        } else {
            0L
        }

        if ($item.Length -le $offset) {
            return [pscustomobject]@{
                Text = $null
                Error = $null
                Rotated = $rotated
            }
        }

        $fileShare = [System.IO.FileShare]::ReadWrite -bor [System.IO.FileShare]::Delete
        $stream = [System.IO.File]::Open(
            $Path,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::Read,
            $fileShare)
        $stream.Seek($offset, [System.IO.SeekOrigin]::Begin) | Out-Null
        $reader = [System.IO.StreamReader]::new(
            $stream,
            [System.Text.UTF8Encoding]::new($false),
            $true,
            4096,
            $false)
        $text = $reader.ReadToEnd().TrimEnd()

        return [pscustomobject]@{
            Text = if ([string]::IsNullOrWhiteSpace($text)) { $null } else { $text }
            Error = $null
            Rotated = $rotated
        }
    }
    catch {
        return [pscustomobject]@{
            Text = $null
            Error = $_.Exception.Message
            Rotated = $false
        }
    }
    finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
        elseif ($null -ne $stream) {
            $stream.Dispose()
        }
    }
}

function Get-StartupFailureDetails {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)]$Checkpoint
    )

    $delta = Get-StartupLogDelta -Path $Path -Checkpoint $Checkpoint
    if ($null -ne $delta.Error) {
        return "Could not isolate new startup.log entries for this run: $($delta.Error) Check file permissions and confirm that the smoke test and application use the same Windows user context."
    }

    if ([string]::IsNullOrWhiteSpace($delta.Text)) {
        $checkpointDescription = if ($Checkpoint.Exists) {
            "pre-run length=$($Checkpoint.Length), last-write UTC=$($Checkpoint.LastWriteTimeUtc.ToString('o'))"
        } else {
            'startup.log did not exist before launch'
        }
        return "No new startup.log entries were written for this run ($checkpointDescription). The application may not have reached startup logging; check file permissions and confirm that the smoke test and application use the same Windows user context."
    }

    $rotationNote = if ($delta.Rotated) { ' (the log rotated after the checkpoint)' } else { '' }
    return "New startup.log entries written after the pre-run checkpoint$rotationNote`:`r`n$($delta.Text)"
}

function Get-OwnedWebView2Processes {
    param(
        [Parameter(Mandatory = $true)][int]$RootProcessId,
        [Parameter(Mandatory = $true)][long]$RootStartTimeUtcTicks
    )

    try {
        $processRows = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
        $descendantIds = [System.Collections.Generic.HashSet[uint32]]::new()
        [void]$descendantIds.Add([uint32]$RootProcessId)

        do {
            $added = $false
            foreach ($row in $processRows) {
                $processId = [uint32]$row.ProcessId
                if ($processId -eq [uint32]$RootProcessId -or
                    $descendantIds.Contains($processId) -or
                    -not $descendantIds.Contains([uint32]$row.ParentProcessId)) {
                    continue
                }

                try {
                    if ($row.CreationDate.ToUniversalTime().Ticks -lt $RootStartTimeUtcTicks) {
                        continue
                    }
                }
                catch {
                    # A missing creation time makes ancestry unsafe to attribute to this run.
                    continue
                }

                [void]$descendantIds.Add($processId)
                $added = $true
            }
        } while ($added)

        $captured = @()
        foreach ($row in $processRows) {
            if ($row.Name -ine 'msedgewebview2.exe' -or
                -not $descendantIds.Contains([uint32]$row.ProcessId)) {
                continue
            }

            $child = Get-Process -Id ([int]$row.ProcessId) -ErrorAction SilentlyContinue
            if ($null -eq $child) {
                continue
            }

            try {
                $captured += [pscustomobject]@{
                    Id = $child.Id
                    StartTimeUtcTicks = $child.StartTime.ToUniversalTime().Ticks
                }
            }
            catch {
                # If identity cannot be confirmed, do not risk tracking another WebView2 instance.
            }
            finally {
                $child.Dispose()
            }
        }

        return [pscustomobject]@{
            CanIdentify = $true
            Processes = @($captured)
        }
    }
    catch {
        return [pscustomobject]@{
            CanIdentify = $false
            Processes = @()
        }
    }
}

function Stop-AndAssertCapturedProcessesExited {
    param(
        [Parameter(Mandatory = $true)][object[]]$Processes,
        [int]$TimeoutMilliseconds = 5000
    )

    if ($Processes.Count -eq 0) {
        return
    }

    $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMilliseconds)
    $remainingProcessIds = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($captured in $Processes) {
        $candidate = Get-Process -Id $captured.Id -ErrorAction SilentlyContinue
        if ($null -eq $candidate) {
            continue
        }

        try {
            if ($candidate.StartTime.ToUniversalTime().Ticks -ne $captured.StartTimeUtcTicks) {
                continue
            }

            if (-not $candidate.HasExited) {
                try {
                    $candidate.Kill()
                }
                catch {
                    $candidate.Refresh()
                    if (-not $candidate.HasExited) {
                        [void]$remainingProcessIds.Add($captured.Id)
                        continue
                    }
                }
            }

            $remainingMilliseconds = [Math]::Max(
                0,
                [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
            if (-not $candidate.HasExited -and
                -not $candidate.WaitForExit($remainingMilliseconds)) {
                [void]$remainingProcessIds.Add($captured.Id)
            }
        }
        catch {
            try {
                $candidate.Refresh()
                if (-not $candidate.HasExited -and
                    $candidate.StartTime.ToUniversalTime().Ticks -eq $captured.StartTimeUtcTicks) {
                    [void]$remainingProcessIds.Add($captured.Id)
                }
            }
            catch {
                # The captured process exited while its state was being checked.
            }
        }
        finally {
            $candidate.Dispose()
        }
    }

    if ($remainingProcessIds.Count -gt 0) {
        throw "Forced WebView2 child process cleanup timed out for PID(s): $($remainingProcessIds -join ', ')."
    }
}

function Stop-AndAssertProcessExited {
    param(
        [Parameter(Mandatory = $true)][System.Diagnostics.Process]$Process,
        [int]$TimeoutMilliseconds = 5000
    )

    $targetId = $Process.Id
    $targetStartTimeUtcTicks = $null
    try {
        $targetStartTimeUtcTicks = $Process.StartTime.ToUniversalTime().Ticks
    }
    catch {
        # HasExited on this Process object remains authoritative for the launched instance.
    }

    $ownedWebView2 = [pscustomobject]@{
        CanIdentify = $false
        Processes = @()
    }
    $wasRunning = $false
    try {
        $wasRunning = -not $Process.HasExited
    }
    catch {
        $wasRunning = $false
    }

    if ($wasRunning) {
        if ($null -ne $targetStartTimeUtcTicks) {
            $ownedWebView2 = Get-OwnedWebView2Processes `
                -RootProcessId $targetId `
                -RootStartTimeUtcTicks $targetStartTimeUtcTicks
        }
        try {
            $Process.Kill()
        }
        catch {
            $Process.Refresh()
            if (-not $Process.HasExited) {
                throw
            }
        }
    }

    if (-not $Process.HasExited -and -not $Process.WaitForExit($TimeoutMilliseconds)) {
        throw "Published EXE process PID=$targetId did not exit within $TimeoutMilliseconds ms after a forced stop."
    }

    $Process.Refresh()
    if (-not $Process.HasExited) {
        throw "Published EXE process PID=$targetId remains active after a forced stop."
    }

    $samePidProcess = Get-Process -Id $targetId -ErrorAction SilentlyContinue
    if ($null -ne $samePidProcess) {
        try {
            if ($null -ne $targetStartTimeUtcTicks -and
                $samePidProcess.StartTime.ToUniversalTime().Ticks -eq $targetStartTimeUtcTicks -and
                -not $samePidProcess.HasExited) {
                throw "Published EXE process PID=$targetId is still present after cleanup."
            }
        }
        finally {
            $samePidProcess.Dispose()
        }
    }

    if ($ownedWebView2.CanIdentify) {
        Stop-AndAssertCapturedProcessesExited `
            -Processes $ownedWebView2.Processes `
            -TimeoutMilliseconds $TimeoutMilliseconds
    }
}

Assert-NoRunningCodexUApp

$publish = (Resolve-Path $PublishDirectory).Path
$required = @(
    'CodexU.App.exe',
    'web\index.html',
    'LICENSE',
    'THIRD-PARTY-NOTICES.md',
    'THIRD-PARTY-INVENTORY.md',
    'THIRD-PARTY-LICENSES.txt',
    'LICENSES\Apache-2.0.txt',
    'LICENSES\Inno-Setup-license.txt',
    'LICENSES\dotnet-runtime-MIT.txt',
    'LICENSES\dotnet-runtime-ThirdPartyNotices.txt',
    'LICENSES\shanggqm-codexU-MIT.txt',
    'LICENSES\liu-codexU-windows-MIT.txt'
)
$missing = $required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $publish $_) -PathType Leaf) }
if ($missing) {
    throw "Publish payload is missing: $($missing -join ', ')"
}

$executable = Join-Path $publish 'CodexU.App.exe'
$localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
if ([string]::IsNullOrWhiteSpace($localAppData)) {
    throw 'Windows did not provide a LocalApplicationData directory for the current user.'
}

$smokeNonce = [Guid]::NewGuid().ToString('N')
$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
if (-not [System.IO.Directory]::Exists($tempRoot)) {
    throw "The current .NET temporary directory does not exist: '$tempRoot'."
}
$realAppDataRoot = Join-Path $localAppData 'codexU'
$smokeDataRoot = Join-Path $tempRoot "codexU-smoke-$smokeNonce"
$webViewDataDirectory = Join-Path $smokeDataRoot 'WebView2'
$startupLogPath = Join-Path $smokeDataRoot 'startup.log'
$realSettingsPath = Join-Path $realAppDataRoot 'settings.json'
$eventName = "Local\codexU-smoke-$smokeNonce"
$statusEventName = "Local\codexU-status-$smokeNonce"
$readyEvent = $null
$statusEvent = $null
$process = $null
$applicationLaunchAttempted = $false
$smokeDataRootCleanupRequired = $false
$startupLogBefore = $null
$settingsBefore = $null
$settingsFingerprintCaptured = $false
$startupRegistrationBefore = $null
$startupRegistrationFingerprintCaptured = $false
$successMessage = $null
$testFailure = $null
$cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
$processTreeCleanupSucceeded = $true

try {
    if ([System.IO.Directory]::Exists($smokeDataRoot) -or
        [System.IO.File]::Exists($smokeDataRoot)) {
        throw "Unique smoke data path already exists: '$smokeDataRoot'."
    }

    $smokeDataRootCleanupRequired = $true
    [System.IO.Directory]::CreateDirectory($smokeDataRoot) | Out-Null
    $settingsBefore = Get-SettingsFingerprint -Path $realSettingsPath
    $settingsFingerprintCaptured = $true
    $startupRegistrationBefore = Get-StartupRegistrationFingerprint
    $startupRegistrationFingerprintCaptured = $true
    Copy-SmokeSeedData -SourceRoot $realAppDataRoot -DestinationRoot $smokeDataRoot
    Assert-DirectoryWritable -Path $webViewDataDirectory
    $startupLogBefore = Get-StartupLogCheckpoint -Path $startupLogPath

    $createdNew = $false
    $readyEvent = [System.Threading.EventWaitHandle]::new(
        $false,
        [System.Threading.EventResetMode]::ManualReset,
        $eventName,
        [ref]$createdNew)
    if (-not $createdNew) {
        $readyEvent.Dispose()
        $readyEvent = $null
        throw 'Could not create a unique Web UI readiness event.'
    }

    if ($VerifyStatusStrip) {
        $statusCreatedNew = $false
        $statusEvent = [System.Threading.EventWaitHandle]::new(
            $false,
            [System.Threading.EventResetMode]::ManualReset,
            $statusEventName,
            [ref]$statusCreatedNew)
        if (-not $statusCreatedNew) {
            $statusEvent.Dispose()
            $statusEvent = $null
            throw 'Could not create a unique status-strip readiness event.'
        }
    }

    $previousReadyEvent = [Environment]::GetEnvironmentVariable('CODEXU_SMOKE_READY_EVENT', 'Process')
    $previousForceStatusStrip = [Environment]::GetEnvironmentVariable('CODEXU_SMOKE_FORCE_STATUS_STRIP', 'Process')
    $previousStatusEvent = [Environment]::GetEnvironmentVariable('CODEXU_SMOKE_STATUS_EVENT', 'Process')
    $previousAppDataDirectory = [Environment]::GetEnvironmentVariable('CODEXU_SMOKE_APP_DATA_DIRECTORY', 'Process')
    $launchFailure = $null
    $environmentRestoreFailures = [System.Collections.Generic.List[System.Exception]]::new()
    try {
        [Environment]::SetEnvironmentVariable('CODEXU_SMOKE_READY_EVENT', $eventName, 'Process')
        [Environment]::SetEnvironmentVariable(
            'CODEXU_SMOKE_APP_DATA_DIRECTORY',
            $smokeDataRoot,
            'Process')
        if ($VerifyStatusStrip) {
            [Environment]::SetEnvironmentVariable('CODEXU_SMOKE_FORCE_STATUS_STRIP', '1', 'Process')
            [Environment]::SetEnvironmentVariable('CODEXU_SMOKE_STATUS_EVENT', $statusEventName, 'Process')
        }
        else {
            [Environment]::SetEnvironmentVariable('CODEXU_SMOKE_FORCE_STATUS_STRIP', $null, 'Process')
            [Environment]::SetEnvironmentVariable('CODEXU_SMOKE_STATUS_EVENT', $null, 'Process')
        }

        $applicationLaunchAttempted = $true
        $process = Start-Process -FilePath $executable -PassThru -WindowStyle Hidden
    }
    catch {
        $launchFailure = $_.Exception
    }
    finally {
        $environmentValues = @(
            [pscustomobject]@{ Name = 'CODEXU_SMOKE_READY_EVENT'; Value = $previousReadyEvent },
            [pscustomobject]@{ Name = 'CODEXU_SMOKE_FORCE_STATUS_STRIP'; Value = $previousForceStatusStrip },
            [pscustomobject]@{ Name = 'CODEXU_SMOKE_STATUS_EVENT'; Value = $previousStatusEvent },
            [pscustomobject]@{ Name = 'CODEXU_SMOKE_APP_DATA_DIRECTORY'; Value = $previousAppDataDirectory }
        )
        foreach ($environmentValue in $environmentValues) {
            try {
                [Environment]::SetEnvironmentVariable(
                    $environmentValue.Name,
                    $environmentValue.Value,
                    'Process')
            }
            catch {
                $environmentRestoreFailures.Add(
                    [System.InvalidOperationException]::new(
                        "Could not restore process environment variable $($environmentValue.Name).",
                        $_.Exception))
            }
        }
    }

    if ($null -ne $launchFailure -or $environmentRestoreFailures.Count -gt 0) {
        $launchMessages = [System.Collections.Generic.List[string]]::new()
        if ($null -ne $launchFailure) {
            $launchMessages.Add($launchFailure.Message)
        }
        foreach ($restoreFailure in $environmentRestoreFailures) {
            $launchMessages.Add($restoreFailure.Message)
        }
        $launchInnerException = if ($null -ne $launchFailure) {
            $launchFailure
        } else {
            $environmentRestoreFailures[0]
        }
        throw [System.InvalidOperationException]::new(
            ($launchMessages -join "`r`n"),
            $launchInnerException)
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $process.Refresh()
        if ($process.HasExited) {
            throw "Published EXE exited early with code $($process.ExitCode)"
        }
        $webReady = $readyEvent.WaitOne(0)
        $statusReady = -not $VerifyStatusStrip -or $statusEvent.WaitOne(0)
    } while (($process.MainWindowHandle -eq 0 -or -not $webReady -or -not $statusReady) -and (Get-Date) -lt $deadline)

    if ($process.MainWindowHandle -eq 0) {
        throw "Published EXE did not create a main window within $TimeoutSeconds seconds"
    }

    if (-not $webReady) {
        throw "Published EXE created a window but its Web UI did not become ready within $TimeoutSeconds seconds"
    }

    if ($VerifyStatusStrip -and -not $statusReady) {
        throw "Published EXE Web UI became ready but its status strip did not render a real snapshot within $TimeoutSeconds seconds"
    }

    if ($VerifyStatusStrip) {
        $successMessage = "Published EXE, Web UI, and status-strip snapshot verified without changing real settings or startup registration: PID=$($process.Id), Handle=$($process.MainWindowHandle), Title=$($process.MainWindowTitle)"
    } else {
        $successMessage = "Published EXE and Web UI verified without changing real settings or startup registration: PID=$($process.Id), Handle=$($process.MainWindowHandle), Title=$($process.MainWindowTitle)"
    }
}
catch {
    $testFailure = $_.Exception
}
finally {
    if ($null -ne $process) {
        try {
            Stop-AndAssertProcessExited -Process $process
        }
        catch {
            $processTreeCleanupSucceeded = $false
            $cleanupFailures.Add($_.Exception)
        }
    }

    if ($null -ne $readyEvent) {
        try {
            $readyEvent.Dispose()
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    if ($null -ne $statusEvent) {
        try {
            $statusEvent.Dispose()
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
}

$targetProcessExited = $true
if ($null -ne $process) {
    try {
        $process.Refresh()
        $targetProcessExited = $process.HasExited
    }
    catch {
        $targetProcessExited = $false
        $processTreeCleanupSucceeded = $false
        $cleanupFailures.Add($_.Exception)
    }
}

$startupFailureDetails = $null
if ($null -ne $testFailure -and $applicationLaunchAttempted) {
    try {
        $startupFailureDetails =
            Get-StartupFailureDetails -Path $startupLogPath -Checkpoint $startupLogBefore
    }
    catch {
        $startupFailureDetails =
            "Could not collect new startup.log entries for this run: $($_.Exception.Message)"
    }
}

$settingsFailure = $null
if ($settingsFingerprintCaptured) {
    if ($targetProcessExited) {
        try {
            $settingsAfter = Get-SettingsFingerprint -Path $realSettingsPath
            Assert-SettingsFingerprintUnchanged -Before $settingsBefore -After $settingsAfter
        }
        catch {
            $settingsFailure = $_.Exception
        }
    }
    else {
        $settingsFailure = [System.InvalidOperationException]::new(
            'Post-run settings integrity could not be checked because the published process was not confirmed stopped.')
    }
}

$startupRegistrationFailure = $null
if ($startupRegistrationFingerprintCaptured) {
    if ($targetProcessExited) {
        try {
            $startupRegistrationAfter = Get-StartupRegistrationFingerprint
            Assert-StartupRegistrationFingerprintUnchanged `
                -Before $startupRegistrationBefore `
                -After $startupRegistrationAfter
        }
        catch {
            $startupRegistrationFailure = $_.Exception
        }
    }
    else {
        $startupRegistrationFailure = [System.InvalidOperationException]::new(
            'Post-run startup registration integrity could not be checked because the published process was not confirmed stopped.')
    }
}

if ($smokeDataRootCleanupRequired) {
    if ($processTreeCleanupSucceeded -and $targetProcessExited) {
        try {
            Remove-SmokeDataDirectory `
                -Path $smokeDataRoot `
                -Nonce $smokeNonce `
                -ExpectedTempRoot $tempRoot
        }
        catch {
            $cleanupFailures.Add($_.Exception)
        }
    }
    else {
        $cleanupFailures.Add(
            [System.InvalidOperationException]::new(
                "Temporary smoke data was retained because process-tree shutdown was not confirmed: '$smokeDataRoot'."))
    }
}

$failureMessages = [System.Collections.Generic.List[string]]::new()
if ($null -ne $testFailure) {
    $failureMessages.Add($testFailure.Message)
    if ($null -ne $startupFailureDetails) {
        $failureMessages.Add($startupFailureDetails)
    }
}
if ($null -ne $settingsFailure) {
    $failureMessages.Add("Post-run settings integrity check: $($settingsFailure.Message)")
}
if ($null -ne $startupRegistrationFailure) {
    $failureMessages.Add(
        "Post-run startup registration integrity check: $($startupRegistrationFailure.Message)")
}
foreach ($cleanupFailure in $cleanupFailures) {
    $failureMessages.Add("Smoke-test cleanup failed: $($cleanupFailure.Message)")
}

if ($failureMessages.Count -gt 0) {
    $innerException = if ($null -ne $testFailure) {
        $testFailure
    } elseif ($null -ne $settingsFailure) {
        $settingsFailure
    } elseif ($null -ne $startupRegistrationFailure) {
        $startupRegistrationFailure
    } else {
        $cleanupFailures[0]
    }
    throw [System.InvalidOperationException]::new(
        ($failureMessages -join "`r`n"),
        $innerException)
}

Write-Host $successMessage
