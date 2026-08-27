#Requires -Version 7.2

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$LegacyInstallerPath,

    [Parameter(Mandatory = $true)]
    [string]$ExpectedPackageDirectory,

    [ValidateRange(30, 900)]
    [int]$TimeoutSeconds = 180,

    [string]$PackagedElectronTestPath = (Join-Path $PSScriptRoot 'Test-PackagedElectron.ps1'),

    [switch]$AllowNonCiRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-ProcessWithTimeout {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$WorkingDirectory,

        [Parameter(Mandatory = $true)]
        [int]$ProcessTimeoutSeconds,

        [hashtable]$Environment = @{}
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FilePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    foreach ($argument in $Arguments) {
        $startInfo.ArgumentList.Add($argument)
    }
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        if (-not $process.Start()) {
            throw "Process could not be started: '$FilePath'."
        }

        if (-not $process.WaitForExit($ProcessTimeoutSeconds * 1000)) {
            $processId = $process.Id
            try {
                $process.Kill($true)
                if (-not $process.WaitForExit(15000)) {
                    throw "Timed-out process tree did not terminate: '$FilePath' (PID $processId)."
                }
            }
            catch [System.InvalidOperationException] {
                # The process exited between the timeout and Kill.
            }
            throw "Process timed out after $ProcessTimeoutSeconds seconds: '$FilePath' (PID $processId)."
        }
        $process.WaitForExit()

        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            StandardOutput = ''
            StandardError = ''
        }
    }
    finally {
        try {
            $process.Refresh()
            if (-not $process.HasExited) {
                $process.Kill($true)
                if (-not $process.WaitForExit(15000)) {
                    throw "Process tree did not terminate during cleanup: '$FilePath' (PID $($process.Id))."
                }
            }
        }
        catch [System.InvalidOperationException] {
            # The process exited before cleanup.
        }
        finally {
            $process.Dispose()
        }
    }
}

function Write-ProcessEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [psobject]$Result
    )

    if (-not [string]::IsNullOrWhiteSpace([string]$Result.StandardOutput)) {
        Write-Host "[$Label stdout]"
        Write-Host ([string]$Result.StandardOutput).TrimEnd()
    }
    if (-not [string]::IsNullOrWhiteSpace([string]$Result.StandardError)) {
        Write-Host "[$Label stderr]"
        Write-Host ([string]$Result.StandardError).TrimEnd()
    }
}

function Get-Uninstaller {
    param([Parameter(Mandatory = $true)][string]$InstallDirectory)

    if (-not [System.IO.Directory]::Exists($InstallDirectory)) {
        return $null
    }

    $uninstallers = @(Get-ChildItem `
        -LiteralPath $InstallDirectory `
        -File `
        -Filter 'unins*.exe' | Sort-Object Name)
    if ($uninstallers.Count -gt 1) {
        throw "Installed payload contains multiple uninstallers: $($uninstallers.FullName -join ', ')."
    }

    return $uninstallers | Select-Object -First 1
}

function Wait-ForDirectoryRemoval {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [ValidateRange(1, 120)]
        [int]$WaitSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitSeconds)
    while ([System.IO.Directory]::Exists($Path) -and [DateTimeOffset]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 250
    }

    return -not [System.IO.Directory]::Exists($Path)
}

function Write-InnoLogEvidence {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,

        [Parameter(Mandatory = $true)]
        [string]$Path
    )

    if (-not [System.IO.File]::Exists($Path)) {
        return
    }

    Write-Host "[$Label - last 200 lines]"
    Get-Content -LiteralPath $Path -Tail 200 | ForEach-Object { Write-Host $_ }
}

function Assert-UniqueInstallerTestRoot {
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
        $leaf -notmatch '^codexu-electron-installer-test-[0-9a-f]{32}$') {
        throw "Unexpected installer-test root: '$fullPath'."
    }
}

function Remove-UniqueInstallerTestRoot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedTempRoot
    )

    Assert-UniqueInstallerTestRoot -Path $Path -ExpectedTempRoot $ExpectedTempRoot
    if ([System.IO.Directory]::Exists($Path)) {
        [System.IO.Directory]::Delete($Path, $true)
    }
}

function Test-RegistryValue {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    return $null -ne (Get-ItemProperty -LiteralPath $Path -Name $Name -ErrorAction SilentlyContinue)
}

function Set-TestStartupEntries {
    param(
        [Parameter(Mandatory = $true)][string]$RunRegistryPath,
        [Parameter(Mandatory = $true)][string]$StartupApprovedRegistryPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath
    )

    New-Item -Path $RunRegistryPath -Force | Out-Null
    $startupCommand = '"{0}"' -f [System.IO.Path]::GetFullPath($ExecutablePath)
    New-ItemProperty -Path $RunRegistryPath -Name 'codexU' -Value $startupCommand -PropertyType String -Force | Out-Null
    New-Item -Path $StartupApprovedRegistryPath -Force | Out-Null
    New-ItemProperty -Path $StartupApprovedRegistryPath -Name 'codexU' -Value ([byte[]](2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)) -PropertyType Binary -Force | Out-Null
}

function Assert-StartupEntriesPresent {
    param(
        [Parameter(Mandatory = $true)][string]$RunRegistryPath,
        [Parameter(Mandatory = $true)][string]$StartupApprovedRegistryPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $expectedCommand = '"{0}"' -f [System.IO.Path]::GetFullPath($ExecutablePath)
    $actualCommand = Get-ItemPropertyValue `
        -LiteralPath $RunRegistryPath `
        -Name 'codexU' `
        -ErrorAction SilentlyContinue
    if (-not [string]::Equals(
        [string]$actualCommand,
        $expectedCommand,
        [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-RegistryValue -Path $StartupApprovedRegistryPath -Name 'codexU')) {
        throw "$Context did not preserve the expected Electron startup registration."
    }
}

function Assert-StartupEntriesAbsent {
    param(
        [Parameter(Mandatory = $true)][string]$RunRegistryPath,
        [Parameter(Mandatory = $true)][string]$StartupApprovedRegistryPath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if ((Test-RegistryValue -Path $RunRegistryPath -Name 'codexU') -or
        (Test-RegistryValue -Path $StartupApprovedRegistryPath -Name 'codexU')) {
        throw "$Context left a codexU startup registry value behind."
    }
}

function Assert-RunEntryTargets {
    param(
        [Parameter(Mandatory = $true)][string]$RunRegistryPath,
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $expectedCommand = '"{0}"' -f [System.IO.Path]::GetFullPath($ExecutablePath)
    $actualCommand = Get-ItemPropertyValue `
        -LiteralPath $RunRegistryPath `
        -Name 'codexU' `
        -ErrorAction SilentlyContinue
    if (-not [string]::Equals(
        [string]$actualCommand,
        $expectedCommand,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context did not register the expected Run command. Expected '$expectedCommand', found '$actualCommand'."
    }
}

function Get-ShortcutTargetPath {
    param([Parameter(Mandatory = $true)][string]$ShortcutPath)

    if (-not [System.IO.File]::Exists($ShortcutPath)) {
        throw "Shortcut is missing: '$ShortcutPath'."
    }

    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $null
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $targetPath = [string]$shortcut.TargetPath
        if ([string]::IsNullOrWhiteSpace($targetPath)) {
            throw "Shortcut has no target: '$ShortcutPath'."
        }
        return [System.IO.Path]::GetFullPath($targetPath)
    }
    finally {
        if ($null -ne $shortcut -and
            [System.Runtime.InteropServices.Marshal]::IsComObject($shortcut)) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
        }
        if ([System.Runtime.InteropServices.Marshal]::IsComObject($shell)) {
            [void][System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
        }
    }
}

function Assert-ShortcutTargets {
    param(
        [Parameter(Mandatory = $true)][string]$ShortcutPath,
        [Parameter(Mandatory = $true)][string]$ExpectedTargetPath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    $actualTarget = Get-ShortcutTargetPath -ShortcutPath $ShortcutPath
    $expectedTarget = [System.IO.Path]::GetFullPath($ExpectedTargetPath)
    if (-not [string]::Equals($actualTarget, $expectedTarget, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context shortcut '$ShortcutPath' targets '$actualTarget' instead of '$expectedTarget'."
    }
}

function Get-DisplayIconExecutablePath {
    param([Parameter(Mandatory = $true)][string]$DisplayIcon)

    $iconPath = $DisplayIcon.Trim()
    $iconPath = $iconPath -replace ',\s*-?[0-9]+$', ''
    $iconPath = $iconPath.Trim().Trim('"')
    if ([string]::IsNullOrWhiteSpace($iconPath)) {
        throw 'Uninstall DisplayIcon is empty.'
    }
    return [System.IO.Path]::GetFullPath($iconPath)
}

function Assert-UninstallDisplayIcon {
    param(
        [Parameter(Mandatory = $true)][string]$UninstallRegistryPath,
        [Parameter(Mandatory = $true)][string]$ExpectedExecutablePath,
        [Parameter(Mandatory = $true)][string]$Context
    )

    if (-not (Test-Path -LiteralPath $UninstallRegistryPath)) {
        throw "$Context did not create the same-AppId uninstall registry key."
    }
    $metadata = Get-ItemProperty -LiteralPath $UninstallRegistryPath
    $actualIcon = Get-DisplayIconExecutablePath -DisplayIcon ([string]$metadata.DisplayIcon)
    $expectedIcon = [System.IO.Path]::GetFullPath($ExpectedExecutablePath)
    if (-not [string]::Equals($actualIcon, $expectedIcon, [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Context uninstall DisplayIcon '$actualIcon' does not target '$expectedIcon'."
    }
}

function Test-UsableLegacyWebView2Version {
    param([AllowNull()][object]$Value)

    $version = [string]$Value
    if ([string]::IsNullOrWhiteSpace($version)) { return $false }
    $version = $version.Trim()
    return $version -match '^[0-9.]+$' -and $version -match '[1-9]'
}

function Get-RegistryValueSnapshot64 {
    param(
        [Parameter(Mandatory = $true)][Microsoft.Win32.RegistryHive]$Hive,
        [Parameter(Mandatory = $true)][string]$SubKeyPath,
        [Parameter(Mandatory = $true)][string]$ValueName
    )

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        $Hive,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.OpenSubKey($SubKeyPath, $false)
        if ($null -eq $key) {
            return [pscustomobject]@{
                KeyExists = $false
                ValueExists = $false
                ValueKind = $null
                Value = $null
            }
        }
        try {
            $valueExists = @($key.GetValueNames()) -contains $ValueName
            return [pscustomobject]@{
                KeyExists = $true
                ValueExists = $valueExists
                ValueKind = if ($valueExists) { $key.GetValueKind($ValueName) } else { $null }
                Value = if ($valueExists) {
                    $key.GetValue(
                        $ValueName,
                        $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                } else { $null }
            }
        }
        finally {
            $key.Dispose()
        }
    }
    finally {
        $baseKey.Dispose()
    }
}

function Get-MissingRegistryKeyPaths64 {
    param(
        [Parameter(Mandatory = $true)][Microsoft.Win32.RegistryHive]$Hive,
        [Parameter(Mandatory = $true)][string]$SubKeyPath
    )

    $missingPaths = [System.Collections.Generic.List[string]]::new()
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        $Hive,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $currentPath = ''
        foreach ($segment in $SubKeyPath.Split(
            [char[]]@('\'),
            [StringSplitOptions]::RemoveEmptyEntries)) {
            $currentPath = if ($currentPath.Length -eq 0) {
                $segment
            } else {
                "$currentPath\$segment"
            }
            $key = $baseKey.OpenSubKey($currentPath, $false)
            if ($null -eq $key) {
                $missingPaths.Add($currentPath)
            }
            else {
                $key.Dispose()
            }
        }
    }
    finally {
        $baseKey.Dispose()
    }
    return $missingPaths.ToArray()
}

function Test-LegacyWebView2GateSatisfied {
    param(
        [Parameter(Mandatory = $true)][string]$MachineSubKeyPath,
        [Parameter(Mandatory = $true)][string]$UserSubKeyPath
    )

    foreach ($candidate in @(
        [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::LocalMachine; Path = $MachineSubKeyPath },
        [pscustomobject]@{ Hive = [Microsoft.Win32.RegistryHive]::CurrentUser; Path = $UserSubKeyPath }
    )) {
        $snapshot = Get-RegistryValueSnapshot64 `
            -Hive $candidate.Hive `
            -SubKeyPath $candidate.Path `
            -ValueName 'pv'
        if ($snapshot.ValueExists -and
            $snapshot.ValueKind -in @(
                [Microsoft.Win32.RegistryValueKind]::String,
                [Microsoft.Win32.RegistryValueKind]::ExpandString) -and
            (Test-UsableLegacyWebView2Version -Value $snapshot.Value)) {
            return $true
        }
    }
    return $false
}

function Enable-LegacyWebView2GateForDisposableRunner {
    param(
        [Parameter(Mandatory = $true)][bool]$DisposableRunner,
        [Parameter(Mandatory = $true)][string]$MachineSubKeyPath,
        [Parameter(Mandatory = $true)][string]$UserSubKeyPath
    )

    if (Test-LegacyWebView2GateSatisfied `
        -MachineSubKeyPath $MachineSubKeyPath `
        -UserSubKeyPath $UserSubKeyPath) {
        return $null
    }
    if (-not $DisposableRunner) {
        throw 'The v0.5.0 installer requires WebView2. Its temporary HKCU compatibility marker may only be created on a disposable GitHub-hosted runner.'
    }

    $original = Get-RegistryValueSnapshot64 `
        -Hive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
        -SubKeyPath $UserSubKeyPath `
        -ValueName 'pv'
    $missingKeyPaths = @(Get-MissingRegistryKeyPaths64 `
        -Hive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
        -SubKeyPath $UserSubKeyPath)
    $state = [pscustomobject]@{
        Original = $original
        UserSubKeyPath = $UserSubKeyPath
        OriginallyMissingKeyPaths = $missingKeyPaths
    }
    try {
        $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
            [Microsoft.Win32.RegistryHive]::CurrentUser,
            [Microsoft.Win32.RegistryView]::Registry64)
        try {
            $key = $baseKey.CreateSubKey($UserSubKeyPath)
            try {
                $key.SetValue('pv', '1.0.0.0', [Microsoft.Win32.RegistryValueKind]::String)
            }
            finally {
                $key.Dispose()
            }
        }
        finally {
            $baseKey.Dispose()
        }
        if (-not (Test-LegacyWebView2GateSatisfied `
            -MachineSubKeyPath $MachineSubKeyPath `
            -UserSubKeyPath $UserSubKeyPath)) {
            throw 'Could not establish the temporary v0.5.0 WebView2 compatibility marker.'
        }
    }
    catch {
        $enableFailure = $_.Exception
        try {
            Restore-LegacyWebView2GateState -State $state
        }
        catch {
            throw [System.InvalidOperationException]::new(
                "$($enableFailure.Message)`r`nWebView2 registry rollback failed: $($_.Exception.Message)",
                $enableFailure)
        }
        throw $enableFailure
    }
    return $state
}

function Restore-LegacyWebView2GateState {
    param([AllowNull()][psobject]$State)

    if ($null -eq $State) { return }
    $original = $State.Original
    $subKeyPath = [string]$State.UserSubKeyPath
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::CurrentUser,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $baseKey.OpenSubKey($subKeyPath, $true)
        if ($null -eq $key -and $original.KeyExists) {
            $key = $baseKey.CreateSubKey($subKeyPath)
        }
        if ($null -ne $key) {
            try {
                if ($original.ValueExists) {
                    $key.SetValue('pv', $original.Value, $original.ValueKind)
                }
                else {
                    $key.DeleteValue('pv', $false)
                }
            }
            finally {
                $key.Dispose()
            }
        }

        $missingKeyPaths = @($State.OriginallyMissingKeyPaths)
        [array]::Reverse($missingKeyPaths)
        foreach ($missingKeyPath in $missingKeyPaths) {
            $probe = $baseKey.OpenSubKey([string]$missingKeyPath, $false)
            try {
                if ($null -ne $probe -and
                    ($probe.GetValueNames().Count -ne 0 -or $probe.GetSubKeyNames().Count -ne 0)) {
                    throw "Refusing to remove temporary WebView2 key '$missingKeyPath' because it gained unrelated state."
                }
            }
            finally {
                if ($null -ne $probe) { $probe.Dispose() }
            }
            if ($null -ne $probe) {
                $baseKey.DeleteSubKey([string]$missingKeyPath, $false)
            }
        }
    }
    finally {
        $baseKey.Dispose()
    }

    $restored = Get-RegistryValueSnapshot64 `
        -Hive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
        -SubKeyPath $subKeyPath `
        -ValueName 'pv'
    $dataMatches = [System.Collections.StructuralComparisons]::StructuralEqualityComparer.Equals(
        $restored.Value,
        $original.Value)
    if ($restored.KeyExists -ne $original.KeyExists -or
        $restored.ValueExists -ne $original.ValueExists -or
        $restored.ValueKind -ne $original.ValueKind -or
        -not $dataMatches) {
        throw 'The temporary WebView2 registry state was not restored exactly.'
    }
    foreach ($missingKeyPath in @($State.OriginallyMissingKeyPaths)) {
        $missingSnapshot = Get-RegistryValueSnapshot64 `
            -Hive ([Microsoft.Win32.RegistryHive]::CurrentUser) `
            -SubKeyPath ([string]$missingKeyPath) `
            -ValueName 'pv'
        if ($missingSnapshot.KeyExists) {
            throw "Temporary WebView2 registry key '$missingKeyPath' was not removed."
        }
    }
}

function Assert-InstalledDirectoryMatchesPackage {
    param(
        [Parameter(Mandatory = $true)][string]$InstallDirectory,
        [Parameter(Mandatory = $true)][string]$ExpectedPackageDirectory,
        [Parameter(Mandatory = $true)][string]$AllowedSentinelRelativePath
    )

    $expectedFiles = [System.Collections.Generic.Dictionary[string, object]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $expectedDirectories = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in Get-ChildItem -LiteralPath $ExpectedPackageDirectory -Recurse -Force) {
        $relativePath = [System.IO.Path]::GetRelativePath($ExpectedPackageDirectory, $entry.FullName)
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Expected package contains an unsupported reparse point: '$relativePath'."
        }
        if ($entry.PSIsContainer) {
            [void]$expectedDirectories.Add($relativePath)
        }
        else {
            $expectedFiles.Add($relativePath, [pscustomobject]@{
                Length = $entry.Length
                Sha256 = (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash
            })
        }
    }
    if ($expectedFiles.Count -eq 0) {
        throw "Expected package directory contains no files: '$ExpectedPackageDirectory'."
    }

    $unexpectedEntries = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in Get-ChildItem -LiteralPath $InstallDirectory -Recurse -Force) {
        $relativePath = [System.IO.Path]::GetRelativePath($InstallDirectory, $entry.FullName)
        if (($entry.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
            $unexpectedEntries.Add("reparse-point:$relativePath")
            continue
        }
        if ($entry.PSIsContainer) {
            if (-not $expectedDirectories.Contains($relativePath)) {
                $unexpectedEntries.Add("directory:$relativePath")
            }
            continue
        }
        if ($expectedFiles.ContainsKey($relativePath)) {
            $expectedFile = $expectedFiles[$relativePath]
            if ($entry.Length -ne $expectedFile.Length) {
                $unexpectedEntries.Add("size-mismatch:$relativePath")
            }
            elseif (-not [string]::Equals(
                (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash,
                [string]$expectedFile.Sha256,
                [StringComparison]::OrdinalIgnoreCase)) {
                $unexpectedEntries.Add("hash-mismatch:$relativePath")
            }
            continue
        }
        if ([string]::Equals(
            $relativePath,
            $AllowedSentinelRelativePath,
            [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }
        if ($relativePath -match '^unins[0-9]+\.(?:exe|dat|msg)$') {
            continue
        }
        $unexpectedEntries.Add("file:$relativePath")
    }

    $missingFiles = @($expectedFiles.Keys | Where-Object {
        -not [System.IO.File]::Exists((Join-Path $InstallDirectory $_))
    })
    if (-not [System.IO.File]::Exists((Join-Path $InstallDirectory $AllowedSentinelRelativePath))) {
        $missingFiles += $AllowedSentinelRelativePath
    }
    if ($unexpectedEntries.Count -gt 0 -or $missingFiles.Count -gt 0) {
        $unexpectedSummary = @($unexpectedEntries | Select-Object -First 50) -join ', '
        $missingSummary = @($missingFiles | Select-Object -First 50) -join ', '
        throw "Upgraded directory differs from the expected Electron package. Unexpected: [$unexpectedSummary]. Missing: [$missingSummary]."
    }
}

function Get-ProcessesForExecutablePath {
    param([Parameter(Mandatory = $true)][string]$ExecutablePath)

    $fullPath = [System.IO.Path]::GetFullPath($ExecutablePath)
    $processName = [System.IO.Path]::GetFileNameWithoutExtension($fullPath)
    $matches = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
    foreach ($candidate in @(Get-Process -Name $processName -ErrorAction SilentlyContinue)) {
        try {
            if ([string]::Equals(
                [System.IO.Path]::GetFullPath($candidate.Path),
                $fullPath,
                [StringComparison]::OrdinalIgnoreCase)) {
                $matches.Add($candidate)
            }
            else {
                $candidate.Dispose()
            }
        }
        catch {
            $candidate.Dispose()
        }
    }
    return $matches.ToArray()
}

function Wait-ForExecutableProcess {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Deadline
    )

    do {
        $matches = @(Get-ProcessesForExecutablePath -ExecutablePath $ExecutablePath)
        if ($matches.Count -gt 0) { return $matches }
        Start-Sleep -Milliseconds 250
    } while ([DateTimeOffset]::UtcNow -lt $Deadline)
    return @()
}

function Assert-NoProcessesForExecutablePath {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$Context,
        [ValidateRange(1, 60)][int]$WaitSeconds = 15
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($WaitSeconds)
    do {
        $matches = @(Get-ProcessesForExecutablePath -ExecutablePath $ExecutablePath)
        if ($matches.Count -eq 0) { return }
        $processIds = @($matches | ForEach-Object { $_.Id })
        foreach ($candidate in $matches) { $candidate.Dispose() }
        if ([DateTimeOffset]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    throw "$Context left process PID(s) $($processIds -join ', ') for '$ExecutablePath'."
}

function Start-ResidentElectron {
    param(
        [Parameter(Mandatory = $true)][string]$ExecutablePath,
        [Parameter(Mandatory = $true)][string]$WorkingDirectory,
        [Parameter(Mandatory = $true)][hashtable]$Environment
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $ExecutablePath
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = $false
    $startInfo.ArgumentList.Add('--installer-smoke-resident')
    foreach ($entry in $Environment.GetEnumerator()) {
        $startInfo.Environment[[string]$entry.Key] = [string]$entry.Value
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) {
        $process.Dispose()
        throw 'Installed Electron application could not be started for resident-uninstall testing.'
    }
    return $process
}

$isDisposableGitHubRunner = `
    [string]::Equals($env:GITHUB_ACTIONS, 'true', [StringComparison]::OrdinalIgnoreCase) -and
    [string]::Equals($env:RUNNER_ENVIRONMENT, 'github-hosted', [StringComparison]::OrdinalIgnoreCase)
if (-not $isDisposableGitHubRunner -and -not $AllowNonCiRun) {
    throw 'Installer smoke testing mutates per-user installer state and is restricted to disposable GitHub-hosted runners. Self-hosted and other CI environments must explicitly use -AllowNonCiRun on an intentionally disposable account.'
}

$installer = (Resolve-Path -LiteralPath $InstallerPath).Path
if (-not [System.IO.File]::Exists($installer) -or [System.IO.FileInfo]::new($installer).Length -le 0) {
    throw "Electron installer was not found or is empty: '$InstallerPath'."
}
$legacyInstallerCandidate = [System.IO.Path]::GetFullPath($LegacyInstallerPath)
$legacyInstallerFile = [System.IO.FileInfo]::new($legacyInstallerCandidate)
if (-not [System.IO.File]::Exists($legacyInstallerCandidate) -or $legacyInstallerFile.Length -le 0) {
    throw "Legacy v0.5.0 installer was not found or is empty: '$LegacyInstallerPath'."
}
$expectedLegacyInstallerSize = 53864409
if ($legacyInstallerFile.Length -ne $expectedLegacyInstallerSize) {
    throw "Legacy installer size mismatch. Expected $expectedLegacyInstallerSize bytes, found $($legacyInstallerFile.Length)."
}
$legacyInstaller = (Resolve-Path -LiteralPath $legacyInstallerCandidate).Path
$expectedLegacyInstallerSha256 = '0f01958eeca60ac5ee57658680af5120d55e6dfaa786a8ed30bf76600bbb2b21'
$actualLegacyInstallerSha256 = (Get-FileHash -LiteralPath $legacyInstaller -Algorithm SHA256).Hash
if (-not [string]::Equals(
    $actualLegacyInstallerSha256,
    $expectedLegacyInstallerSha256,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Legacy installer SHA256 mismatch. Expected $expectedLegacyInstallerSha256, found $actualLegacyInstallerSha256."
}
$legacyVersionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($legacyInstaller)
$legacyFileVersion = ([string]$legacyVersionInfo.FileVersion).Trim()
$legacyProductVersion = ([string]$legacyVersionInfo.ProductVersion).Trim()
if (-not [string]::Equals($legacyFileVersion, '0.5.0.0', [StringComparison]::Ordinal) -or
    -not [string]::Equals($legacyProductVersion, '0.5.0.0', [StringComparison]::Ordinal)) {
    throw "Legacy installer must have file and product version 0.5.0.0; found file '$legacyFileVersion' and product '$legacyProductVersion'."
}
$expectedPackageCandidate = [System.IO.Path]::GetFullPath($ExpectedPackageDirectory)
if (-not [System.IO.Directory]::Exists($expectedPackageCandidate)) {
    throw "Expected Electron package directory was not found: '$ExpectedPackageDirectory'."
}
$expectedPackage = (Resolve-Path -LiteralPath $expectedPackageCandidate).Path
if (-not [System.IO.File]::Exists((Join-Path $expectedPackage 'CodexU.exe'))) {
    throw "Expected Electron package does not contain CodexU.exe: '$expectedPackage'."
}
$packagedTest = (Resolve-Path -LiteralPath $PackagedElectronTestPath).Path
if (-not [System.IO.File]::Exists($packagedTest)) {
    throw "Packaged Electron test script was not found: '$PackagedElectronTestPath'."
}

$runRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$startupApprovedRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{A4B05572-70A1-4A5C-A9CE-08FA966F4E8E}_is1'
$startMenuGroup = Join-Path ([Environment]::GetFolderPath('Programs')) 'codexU'
$startMenuShortcut = Join-Path $startMenuGroup 'codexU.lnk'
$startMenuUninstallShortcut = Join-Path $startMenuGroup '卸载 codexU.lnk'
$desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'codexU.lnk'
$legacyWebView2MachineSubKey = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
$legacyWebView2UserSubKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'

if ((Test-Path -LiteralPath $uninstallRegistryPath) -or
    (Test-RegistryValue -Path $runRegistryPath -Name 'codexU') -or
    (Test-RegistryValue -Path $startupApprovedRegistryPath -Name 'codexU') -or
    [System.IO.Directory]::Exists($startMenuGroup) -or
    [System.IO.File]::Exists($startMenuShortcut) -or
    [System.IO.File]::Exists($startMenuUninstallShortcut) -or
    [System.IO.File]::Exists($desktopShortcut)) {
    throw 'Refusing to run the installer test because this user already has codexU installer, startup, or shortcut state.'
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$testRoot = Join-Path $tempRoot ("codexu-electron-installer-test-{0}" -f [Guid]::NewGuid().ToString('N'))
Assert-UniqueInstallerTestRoot -Path $testRoot -ExpectedTempRoot $tempRoot
if ([System.IO.Directory]::Exists($testRoot) -or [System.IO.File]::Exists($testRoot)) {
    throw "Unique Electron installer-test path already exists: '$testRoot'."
}
[System.IO.Directory]::CreateDirectory($testRoot) | Out-Null

function Invoke-InstallerScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][ValidateSet('fresh-safety', 'legacy-v0.5.0-upgrade')][string]$Fixture,
        [Parameter(Mandatory = $true)][bool]$ExerciseResidentUninstall
    )

    $isLegacyUpgrade = $Fixture -eq 'legacy-v0.5.0-upgrade'
    $scenarioRoot = Join-Path $testRoot $Name
    $installDirectory = Join-Path $scenarioRoot 'application'
    $legacySetupLog = Join-Path $scenarioRoot 'legacy-v0.5.0-setup.log'
    $headSetupLog = Join-Path $scenarioRoot 'head-upgrade-setup.log'
    $uninstallLog = Join-Path $scenarioRoot 'uninstall.log'
    $electronStateRoot = Join-Path $scenarioRoot 'electron-user-data'
    $sidecarStateRoot = Join-Path $scenarioRoot 'sidecar-data'
    $processEnvironment = @{
        CODEXU_DATA_DIRECTORY = $sidecarStateRoot
        CODEXU_ELECTRON_USER_DATA_DIRECTORY = $electronStateRoot
    }
    $installedExecutable = Join-Path $installDirectory 'CodexU.exe'
    $sidecarExecutable = Join-Path $installDirectory 'resources\backend\CodexU.Sidecar.exe'
    $legacyExecutable = Join-Path $installDirectory 'CodexU.App.exe'
    $legacyAssembly = Join-Path $installDirectory 'CodexU.App.dll'
    [System.IO.Directory]::CreateDirectory($installDirectory) | Out-Null

    $unownedSentinel = Join-Path $installDirectory 'user-owned.keep'
    $fixturePaths = [System.Collections.Generic.List[string]]::new()
    if (-not $isLegacyUpgrade) {
        [System.IO.File]::WriteAllText($unownedSentinel, 'preserve')
        foreach ($relativePath in @(
            'System.UserOwned.dll',
            'web\user-owned.keep',
            'LICENSES\user-owned.keep'
        )) {
            $fixturePath = Join-Path $installDirectory $relativePath
            [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($fixturePath)) | Out-Null
            [System.IO.File]::WriteAllText($fixturePath, 'fresh-directory-fixture')
            $fixturePaths.Add($fixturePath)
        }
        Set-TestStartupEntries `
            -RunRegistryPath $runRegistryPath `
            -StartupApprovedRegistryPath $startupApprovedRegistryPath `
            -ExecutablePath $installedExecutable
    }

    $scenarioFailure = $null
    $cleanupFailures = [System.Collections.Generic.List[System.Exception]]::new()
    $uninstallVerified = $false
    $uninstallAttempts = 0
    $residentProcess = $null

    try {
        if ($isLegacyUpgrade) {
            $webView2State = $null
            $legacyInstallFailure = $null
            try {
                $webView2State = Enable-LegacyWebView2GateForDisposableRunner `
                    -DisposableRunner $isDisposableGitHubRunner `
                    -MachineSubKeyPath $legacyWebView2MachineSubKey `
                    -UserSubKeyPath $legacyWebView2UserSubKey
                $legacySetupResult = Invoke-ProcessWithTimeout `
                    -FilePath $legacyInstaller `
                    -Arguments @(
                        '/VERYSILENT',
                        '/SUPPRESSMSGBOXES',
                        '/NORESTART',
                        '/SP-',
                        '/CURRENTUSER',
                        "/DIR=$installDirectory",
                        '/TASKS=desktopicon,startup',
                        "/LOG=$legacySetupLog") `
                    -WorkingDirectory $scenarioRoot `
                    -ProcessTimeoutSeconds $TimeoutSeconds
                Write-ProcessEvidence -Label "$Name legacy v0.5.0 installer" -Result $legacySetupResult
                if ($legacySetupResult.ExitCode -ne 0) {
                    throw "$Name legacy v0.5.0 installer failed with exit code $($legacySetupResult.ExitCode)."
                }
            }
            catch {
                $legacyInstallFailure = $_.Exception
            }
            finally {
                try {
                    Restore-LegacyWebView2GateState -State $webView2State
                }
                catch {
                    if ($null -eq $legacyInstallFailure) {
                        $legacyInstallFailure = $_.Exception
                    }
                    else {
                        $legacyInstallFailure = [System.InvalidOperationException]::new(
                            "$($legacyInstallFailure.Message)`r`nWebView2 registry restoration failed: $($_.Exception.Message)",
                            $legacyInstallFailure)
                    }
                }
            }
            if ($null -ne $legacyInstallFailure) { throw $legacyInstallFailure }

            if (-not [System.IO.File]::Exists($legacyExecutable) -or
                -not [System.IO.File]::Exists($legacyAssembly)) {
                throw "$Name legacy installer did not create CodexU.App.exe and CodexU.App.dll."
            }
            Assert-NoProcessesForExecutablePath `
                -ExecutablePath $legacyExecutable `
                -Context "$Name legacy silent installation"
            Assert-RunEntryTargets `
                -RunRegistryPath $runRegistryPath `
                -ExecutablePath $legacyExecutable `
                -Context "$Name legacy installation"
            $legacyUninstaller = Get-Uninstaller -InstallDirectory $installDirectory
            if ($null -eq $legacyUninstaller) {
                throw "$Name legacy installation did not create exactly one uninstaller."
            }
            Assert-UninstallDisplayIcon `
                -UninstallRegistryPath $uninstallRegistryPath `
                -ExpectedExecutablePath $legacyExecutable `
                -Context "$Name legacy installation"
            Assert-ShortcutTargets `
                -ShortcutPath $startMenuShortcut `
                -ExpectedTargetPath $legacyExecutable `
                -Context "$Name legacy Start Menu"
            Assert-ShortcutTargets `
                -ShortcutPath $desktopShortcut `
                -ExpectedTargetPath $legacyExecutable `
                -Context "$Name legacy desktop"
            Assert-ShortcutTargets `
                -ShortcutPath $startMenuUninstallShortcut `
                -ExpectedTargetPath $legacyUninstaller.FullName `
                -Context "$Name legacy uninstall"
            [System.IO.File]::WriteAllText($unownedSentinel, 'preserve-across-real-upgrade')
        }

        $setupResult = Invoke-ProcessWithTimeout `
            -FilePath $installer `
            -Arguments @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                '/SP-',
                '/CURRENTUSER',
                "/DIR=$installDirectory",
                '/TASKS=desktopicon',
                "/LOG=$headSetupLog") `
            -WorkingDirectory $scenarioRoot `
            -ProcessTimeoutSeconds $TimeoutSeconds
        Write-ProcessEvidence -Label "$Name HEAD installer" -Result $setupResult
        if ($setupResult.ExitCode -ne 0) {
            throw "$Name HEAD installer failed with exit code $($setupResult.ExitCode)."
        }

        if ($isLegacyUpgrade) {
            Assert-StartupEntriesAbsent `
                -RunRegistryPath $runRegistryPath `
                -StartupApprovedRegistryPath $startupApprovedRegistryPath `
                -Context "$Name HEAD upgrade"
        }
        else {
            Assert-StartupEntriesPresent `
                -RunRegistryPath $runRegistryPath `
                -StartupApprovedRegistryPath $startupApprovedRegistryPath `
                -ExecutablePath $installedExecutable `
                -Context "$Name installation"
        }

        if (-not [System.IO.File]::Exists($installedExecutable)) {
            throw "$Name installation did not create the Electron executable."
        }
        $uninstaller = Get-Uninstaller -InstallDirectory $installDirectory
        if ($null -eq $uninstaller) {
            throw "$Name installation did not create exactly one uninstaller."
        }
        if (-not (Test-Path -LiteralPath $uninstallRegistryPath) -or
            -not [System.IO.Directory]::Exists($startMenuGroup) -or
            -not [System.IO.File]::Exists($startMenuShortcut) -or
            -not [System.IO.File]::Exists($startMenuUninstallShortcut) -or
            -not [System.IO.File]::Exists($desktopShortcut)) {
            throw "$Name installation did not create the expected uninstall metadata and shortcuts."
        }
        Assert-UninstallDisplayIcon `
            -UninstallRegistryPath $uninstallRegistryPath `
            -ExpectedExecutablePath $installedExecutable `
            -Context "$Name HEAD installation"
        Assert-ShortcutTargets `
            -ShortcutPath $startMenuShortcut `
            -ExpectedTargetPath $installedExecutable `
            -Context "$Name HEAD Start Menu"
        Assert-ShortcutTargets `
            -ShortcutPath $desktopShortcut `
            -ExpectedTargetPath $installedExecutable `
            -Context "$Name HEAD desktop"
        Assert-ShortcutTargets `
            -ShortcutPath $startMenuUninstallShortcut `
            -ExpectedTargetPath $uninstaller.FullName `
            -Context "$Name HEAD uninstall"

        if (-not [System.IO.File]::Exists($unownedSentinel)) {
            throw "$Name installation deleted an unrelated file from the selected directory."
        }
        if ($isLegacyUpgrade) {
            if ([System.IO.File]::Exists($legacyExecutable) -or
                [System.IO.File]::Exists($legacyAssembly)) {
                throw "$Name HEAD upgrade left the legacy WPF executable or assembly behind."
            }
            Assert-InstalledDirectoryMatchesPackage `
                -InstallDirectory $installDirectory `
                -ExpectedPackageDirectory $expectedPackage `
                -AllowedSentinelRelativePath 'user-owned.keep'
        }
        else {
            $missingFreshFiles = @($fixturePaths | Where-Object { -not [System.IO.File]::Exists($_) })
            if ($missingFreshFiles.Count -gt 0) {
                throw "$Name installation applied legacy cleanup to an unrecognized directory: $($missingFreshFiles -join ', ')."
            }
        }

        & $packagedTest `
            -ApplicationDirectory $installDirectory `
            -TimeoutSeconds ([Math]::Min($TimeoutSeconds, 600))

        [System.IO.File]::Delete($unownedSentinel)
        if ($Fixture -eq 'fresh-safety') {
            [System.IO.File]::Delete((Join-Path $installDirectory 'System.UserOwned.dll'))
            [System.IO.Directory]::Delete((Join-Path $installDirectory 'web'), $true)
            [System.IO.Directory]::Delete((Join-Path $installDirectory 'LICENSES'), $true)
        }

        if ($ExerciseResidentUninstall) {
            $residentProcess = Start-ResidentElectron `
                -ExecutablePath $installedExecutable `
                -WorkingDirectory $installDirectory `
                -Environment $processEnvironment
            $residentSidecars = @(Wait-ForExecutableProcess `
                -ExecutablePath $sidecarExecutable `
                -Deadline ([DateTimeOffset]::UtcNow.AddSeconds(45)))
            if ($residentSidecars.Count -eq 0) {
                throw "$Name resident Electron application did not start its Sidecar."
            }
            foreach ($candidate in $residentSidecars) { $candidate.Dispose() }
            Start-Sleep -Seconds 2
            $residentProcess.Refresh()
            if ($residentProcess.HasExited) {
                throw "$Name Electron application exited before the resident-uninstall test."
            }
        }

        Set-TestStartupEntries `
            -RunRegistryPath $runRegistryPath `
            -StartupApprovedRegistryPath $startupApprovedRegistryPath `
            -ExecutablePath $installedExecutable

        $uninstallAttempts++
        $uninstallResult = Invoke-ProcessWithTimeout `
            -FilePath $uninstaller.FullName `
            -Arguments @(
                '/VERYSILENT',
                '/SUPPRESSMSGBOXES',
                '/NORESTART',
                "/LOG=$uninstallLog") `
            -WorkingDirectory $scenarioRoot `
            -ProcessTimeoutSeconds $TimeoutSeconds `
            -Environment $processEnvironment
        Write-ProcessEvidence -Label "$Name uninstaller" -Result $uninstallResult
        if ($uninstallResult.ExitCode -ne 0) {
            throw "$Name uninstaller failed with exit code $($uninstallResult.ExitCode)."
        }

        if ($null -ne $residentProcess -and -not $residentProcess.WaitForExit(15000)) {
            throw "$Name uninstaller did not stop the resident Electron process."
        }
        Assert-NoProcessesForExecutablePath `
            -ExecutablePath $installedExecutable `
            -Context "$Name uninstallation"
        Assert-NoProcessesForExecutablePath `
            -ExecutablePath $sidecarExecutable `
            -Context "$Name uninstallation"
        if (-not (Wait-ForDirectoryRemoval -Path $installDirectory)) {
            throw "$Name uninstaller exited successfully but left the installation directory."
        }
        Assert-StartupEntriesAbsent `
            -RunRegistryPath $runRegistryPath `
            -StartupApprovedRegistryPath $startupApprovedRegistryPath `
            -Context "$Name uninstallation"
        if ((Test-Path -LiteralPath $uninstallRegistryPath) -or
            [System.IO.Directory]::Exists($startMenuGroup) -or
            [System.IO.File]::Exists($startMenuShortcut) -or
            [System.IO.File]::Exists($startMenuUninstallShortcut) -or
            [System.IO.File]::Exists($desktopShortcut)) {
            throw "$Name uninstallation left uninstall metadata or shortcuts behind."
        }
        $uninstallVerified = $true
    }
    catch {
        $scenarioFailure = $_.Exception
    }
    finally {
        if ($null -ne $residentProcess) {
            try {
                $residentProcess.Refresh()
                if (-not $residentProcess.HasExited) {
                    $residentProcess.Kill($true)
                    if (-not $residentProcess.WaitForExit(15000)) {
                        throw "$Name resident process tree did not stop during cleanup."
                    }
                }
            }
            catch [System.InvalidOperationException] {
                # The process exited between Refresh and Kill.
            }
            catch {
                $cleanupFailures.Add($_.Exception)
            }
            finally {
                $residentProcess.Dispose()
            }
        }

        if (-not $uninstallVerified -and [System.IO.Directory]::Exists($installDirectory)) {
            try {
                $uninstaller = Get-Uninstaller -InstallDirectory $installDirectory
                if ($null -ne $uninstaller -and $uninstallAttempts -lt 2) {
                    $uninstallAttempts++
                    $cleanupResult = Invoke-ProcessWithTimeout `
                        -FilePath $uninstaller.FullName `
                        -Arguments @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$uninstallLog") `
                        -WorkingDirectory $scenarioRoot `
                        -ProcessTimeoutSeconds $TimeoutSeconds `
                        -Environment $processEnvironment
                    Write-ProcessEvidence -Label "$Name cleanup uninstaller" -Result $cleanupResult
                    if ($cleanupResult.ExitCode -ne 0) {
                        throw "$Name cleanup uninstaller failed with exit code $($cleanupResult.ExitCode)."
                    }
                }
                if (-not (Wait-ForDirectoryRemoval -Path $installDirectory)) {
                    throw "$Name cleanup uninstaller left the installation directory."
                }
                $uninstallVerified = $true
            }
            catch {
                $cleanupFailures.Add($_.Exception)
            }
        }

        Remove-ItemProperty -LiteralPath $runRegistryPath -Name 'codexU' -ErrorAction SilentlyContinue
        Remove-ItemProperty -LiteralPath $startupApprovedRegistryPath -Name 'codexU' -ErrorAction SilentlyContinue
        if ($null -ne $scenarioFailure -or $cleanupFailures.Count -gt 0) {
            Write-InnoLogEvidence -Label "$Name legacy v0.5.0 Setup log" -Path $legacySetupLog
            Write-InnoLogEvidence -Label "$Name HEAD Setup log" -Path $headSetupLog
            Write-InnoLogEvidence -Label "$Name Inno uninstall log" -Path $uninstallLog
        }
    }

    $failureMessages = [System.Collections.Generic.List[string]]::new()
    if ($null -ne $scenarioFailure) { $failureMessages.Add($scenarioFailure.Message) }
    foreach ($cleanupFailure in $cleanupFailures) {
        $failureMessages.Add("$Name cleanup failed: $($cleanupFailure.Message)")
    }
    if ([System.IO.Directory]::Exists($installDirectory)) {
        $failureMessages.Add("$Name retained installer state and evidence at '$scenarioRoot'.")
    }
    if ($failureMessages.Count -gt 0) {
        $innerException = if ($null -ne $scenarioFailure) { $scenarioFailure } else { $cleanupFailures[0] }
        throw [System.InvalidOperationException]::new(($failureMessages -join "`r`n"), $innerException)
    }
    if (-not $uninstallVerified) { throw "$Name completed without verifying clean uninstallation." }
}

$testFailure = $null
try {
    Invoke-InstallerScenario `
        -Name 'fresh-directory-safety' `
        -Fixture 'fresh-safety' `
        -ExerciseResidentUninstall $false
    Invoke-InstallerScenario `
        -Name 'legacy-v0.5.0-same-appid-upgrade' `
        -Fixture 'legacy-v0.5.0-upgrade' `
        -ExerciseResidentUninstall $true
}
catch {
    $testFailure = $_.Exception
}

if ($null -eq $testFailure) {
    Remove-UniqueInstallerTestRoot -Path $testRoot -ExpectedTempRoot $tempRoot
}
else {
    throw [System.InvalidOperationException]::new(
        "$($testFailure.Message)`r`nInstaller evidence was retained at '$testRoot'.",
        $testFailure)
}

Write-Host "Electron installer verified for safe fresh install, real v0.5.0 same-AppId upgrade, exact legacy cleanup, resident shutdown, startup/shortcut cleanup, packaged smoke, and clean uninstall: '$installer'."
