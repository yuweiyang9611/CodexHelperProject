$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installer = Get-Content -LiteralPath (Join-Path $projectRoot 'installer\CodexU.iss') -Raw -Encoding utf8
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $projectRoot '.github\workflows\release.yml') -Raw -Encoding utf8
$ciWorkflow = Get-Content -LiteralPath (Join-Path $projectRoot '.github\workflows\ci.yml') -Raw -Encoding utf8
$electronManifest = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\package.json') -Raw -Encoding utf8 | ConvertFrom-Json
$electronPackager = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\scripts\package.mjs') -Raw -Encoding utf8
$electronLegalVerifier = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\scripts\verify-legal-payload.mjs') -Raw -Encoding utf8
$electronReleaseIntegrity = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\scripts\release-integrity.mjs') -Raw -Encoding utf8
$electronMain = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\src\main.ts') -Raw -Encoding utf8
$electronWindowsHost = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\src\windowsHost.ts') -Raw -Encoding utf8
$electronNativeNotifications = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Electron\src\nativeNotifications.ts') -Raw -Encoding utf8
$sidecarHostRpc = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Sidecar\SidecarHostRpc.cs') -Raw -Encoding utf8
$inventoryGenerator = Get-Content -LiteralPath (Join-Path $projectRoot 'tools\Generate-ThirdPartyInventory.ps1') -Raw -Encoding utf8
$globalJson = Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw -Encoding utf8 | ConvertFrom-Json
[xml]$sidecarProject = Get-Content -LiteralPath (Join-Path $projectRoot 'src\CodexU.Sidecar\CodexU.Sidecar.csproj') -Raw -Encoding utf8

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    if ($Content.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw $FailureMessage
    }
}

function Assert-NotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Forbidden,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    if ($Content.IndexOf($Forbidden, [StringComparison]::Ordinal) -ge 0) {
        throw $FailureMessage
    }
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string]$Pattern,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    if (-not [Regex]::IsMatch($Content, $Pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw $FailureMessage
    }
}

function Assert-Ordered {
    param(
        [Parameter(Mandatory = $true)][string]$Content,
        [Parameter(Mandatory = $true)][string[]]$Expected,
        [Parameter(Mandatory = $true)][string]$FailureMessage
    )

    $cursor = 0
    foreach ($item in $Expected) {
        $next = $Content.IndexOf($item, $cursor, [StringComparison]::Ordinal)
        if ($next -lt 0) {
            throw "$FailureMessage Missing or out of order: '$item'."
        }
        $cursor = $next + $item.Length
    }
}

# Installer identity and entry points must remain compatible with the previous
# per-user Inno installation while switching the shipped host to Electron.
Assert-Contains $installer 'AppId={{A4B05572-70A1-4A5C-A9CE-08FA966F4E8E}' `
    'Installer must preserve the existing product AppId for upgrades.'
Assert-Contains $installer 'DefaultDirName={localappdata}\Programs\codexU' `
    'Installer must remain a per-user installation.'
Assert-Contains $installer 'UninstallDisplayIcon={app}\CodexU.exe' `
    'Installer uninstall metadata must point to the Electron executable.'
Assert-Matches $installer '\[Icons\].*?Filename:\s*"\{app\}\\CodexU\.exe"' `
    'Installer shortcuts must launch CodexU.exe.'
Assert-Matches $installer 'Name:\s*"\{group\}\\codexU";[^\r\n]*AppUserModelID:\s*"io\.github\.yuweiyang9611\.CodexU";[^\r\n]*AppUserModelToastActivatorCLSID:\s*"073466E0-6E09-49FC-A4D3-900BED0DBD46"' `
    'Installer Start Menu shortcut must register the stable Electron AppUserModelID and toast activator CLSID.'
Assert-Contains $electronWindowsHost "export const WINDOWS_APP_USER_MODEL_ID = 'io.github.yuweiyang9611.CodexU';" `
    'Electron and the installer must share the reviewed Windows AppUserModelID.'
Assert-Contains $electronWindowsHost "export const WINDOWS_TOAST_ACTIVATOR_CLSID = '{073466E0-6E09-49FC-A4D3-900BED0DBD46}';" `
    'Electron and the installer must share the reviewed Windows toast activator CLSID.'
Assert-Matches $electronMain 'function\s+initializeWindowsDesktopIdentity.*?configureWindowsDesktopIdentity\(process\.platform,\s*app\)' `
    'Electron must configure the packaged Windows process identity before advertising native notifications.'
Assert-Matches $electronMain 'initializePersistentLog\(\);\s*registerWindowsNotificationActivation\(\);\s*registerLifecycleHandlers\(\)' `
    'Electron must register cold notification activation only after acquiring the single-instance lock.'
Assert-Matches $electronWindowsHost 'function\s+ensureWindowsNotificationShortcut.*?readShortcutLink.*?writeShortcutLink.*?readShortcutLink' `
    'Electron must create and read back the real Start Menu AUMID/CLSID shortcut, including for ZIP releases.'
Assert-Ordered $electronMain @(
    'initializeWindowsNotificationShortcut();',
    'nativeNotifications = createNativeNotificationAdapter();',
    'await startSidecar();'
) 'Electron must verify the Windows shortcut before advertising notification capability to the Sidecar.'
Assert-Contains $electronNativeNotifications 'id: notification.id' `
    'Electron must pass the stable logical notification ID to the operating system.'
Assert-Matches $electronNativeNotifications "handle\.once\('failed'.*?this\.ownsHandle\(notification\.id,\s*delivery,\s*handle\).*?this\.rememberedIds\.delete\(notification\.id\).*?this\.scheduleRetry\(delivery\)" `
    'Owned asynchronous native notification failures must clear de-duplication and remain retryable.'
Assert-Contains $electronMain 'Notification.handleActivation' `
    'Electron must handle persisted Windows notification activations and cold starts.'
Assert-Contains $electronMain "app.on('browser-window-focus'" `
    'Electron must re-read effective Windows startup state when the app regains focus.'
Assert-Contains $sidecarHostRpc 'DefaultStartupRegistrationTimeout = TimeSpan.FromSeconds(25)' `
    'Startup reverse RPC must complete before Electron forward requests time out.'
Assert-Matches $installer '\[Registry\].*?Software\\Microsoft\\Windows\\CurrentVersion\\Run.*?ValueName:\s*"codexU";\s*Flags:\s*deletevalue\s+uninsdeletevalue' `
    'Installer must remove the legacy and Electron startup command during install and uninstall.'
Assert-Matches $installer 'StartupApproved\\Run.*?ValueName:\s*"codexU";\s*Flags:\s*deletevalue\s+uninsdeletevalue' `
    'Installer must remove Windows StartupApproved state during install and uninstall.'
Assert-NotContains $installer 'Tasks: startup' `
    'Installer must not compete with the Electron-owned transactional startup setting.'
Assert-Contains $installer 'Check: ShouldRemoveLegacyStartupRegistration' `
    'Installer must preserve valid Electron startup state during silent reinstalls.'
Assert-Matches $installer 'procedure\s+CurUninstallStepChanged.*?RegDeleteValue\(.*?CurrentVersion\\Run.*?RegDeleteValue\(.*?StartupApproved\\Run' `
    'Uninstall must remove both Electron-owned startup registry values unconditionally.'
Assert-Matches $installer '\[Run\].*?Filename:\s*"\{app\}\\CodexU\.exe"' `
    'Installer post-install action must launch CodexU.exe.'
Assert-Contains $installer 'CloseApplicationsFilter=CodexU.exe,CodexU.Sidecar.exe,CodexU.App.exe' `
    'Installer upgrades must close the Electron host, Sidecar and legacy WPF host.'
$dirsSectionMatch = [Regex]::Match($installer, '(?ms)^\[Dirs\]\s*\r?\n(?<Body>.*?)(?=^\[|\z)')
if (-not $dirsSectionMatch.Success) {
    throw 'Installer must declare the application-directory uninstall policy in [Dirs].'
}
$applicationDirectoryEntries = @($dirsSectionMatch.Groups['Body'].Value -split '\r?\n' | Where-Object {
    $_ -match '^\s*Name:\s*"\{app\}"\s*;'
})
if ($applicationDirectoryEntries.Count -ne 1 -or
    $applicationDirectoryEntries[0] -notmatch '^\s*Name:\s*"\{app\}";\s*Flags:\s*uninsalwaysuninstall\s*$') {
    throw 'Uninstall must remove an empty application directory even when it predated Setup.'
}
Assert-Contains $installer '[InstallDelete]' `
    'Installer must explicitly migrate files from the legacy WPF layout.'
$installDeleteSection = [Regex]::Match($installer, '(?s)\[InstallDelete\](.*?)(?:\r?\n\[|\z)').Groups[1].Value
$ungatedInstallDeletes = @($installDeleteSection -split '\r?\n' | Where-Object {
    $_ -match '^Type:' -and $_ -notmatch ';\s*Check:\s*ShouldRemoveLegacyWpfFiles\s*$'
})
if ($ungatedInstallDeletes.Count -gt 0) {
    throw 'Every destructive legacy migration entry must require positive WPF installation detection.'
}
Assert-Contains $installer "FileExists(ExpandConstant('{app}\CodexU.App.exe')) and" `
    'Legacy cleanup must positively identify the old WPF executable.'
Assert-Contains $installer "FileExists(ExpandConstant('{app}\CodexU.App.dll'))" `
    'Legacy cleanup must positively identify the old WPF application assembly.'
Assert-NotContains $installer 'Name: "{app}\*"' `
    'Installer must never recursively delete the whole installation directory.'
Assert-Matches $installer 'function\s+InitializeUninstall\(\):\s*Boolean;.*?--maintenance-shutdown.*?ewWaitUntilTerminated.*?ResultCode\s*=\s*0' `
    'Uninstall must fail closed while waiting for the resident Electron process to shut down.'
Assert-Contains $electronMain `
    'const maintenanceShutdownRequests = new CompletionQueue<string, MaintenanceShutdownOutcome>();' `
    'Maintenance shutdown requests must retain late markers until the shutdown outcome is known.'
Assert-Matches $electronMain `
    '(?s)function\s+completeApplicationShutdown.*?maintenanceShutdownRequests\.complete\(outcome\).*?acknowledgeMaintenanceShutdown' `
    'Every queued maintenance marker must receive the completed shutdown outcome.'
Assert-Matches $electronMain `
    '(?s)function\s+acknowledgeMaintenanceShutdown.*?else\s+writeMaintenanceShutdownFailureMarker\(maintenanceMarker\)' `
    'A failed Sidecar shutdown must explicitly fail the maintenance handshake.'
Assert-Contains $electronWindowsHost "export const WINDOWS_LOGIN_ITEM_NAME = 'codexU';" `
    'Electron startup registration must use the installer-owned stable value name.'
Assert-Contains $electronWindowsHost 'name: WINDOWS_LOGIN_ITEM_NAME' `
    'The shared Windows startup identity must explicitly write the stable value name.'
Assert-Matches $electronMain 'function\s+applyStartupRegistrationVerified.*?applyWindowsStartupRegistration\(\s*app,\s*createWindowsStartupIdentity\(process\.execPath\),\s*enabled' `
    'Electron startup registration must use the reviewed shared Windows identity adapter.'
Assert-Matches $electronWindowsHost 'state\.launchItems\.some\(.*?item\.enabled\)' `
    'Electron startup state must accept an enabled matching StartupApproved item even when disabled duplicates exist.'
Assert-Matches $electronWindowsHost 'state\.openAtLogin\s*&&\s*state\.executableWillLaunchAtLogin\s*&&\s*matchingEnabledItem' `
    'Electron startup state must include the effective Windows StartupApproved state.'
foreach ($removedWebView2Gate in @(
    'WebView2RuntimeDownloadUrl',
    'Microsoft\EdgeUpdate\Clients',
    'IsWebView2RuntimeInstalled',
    'function InitializeSetup()',
    'ShellExec('
)) {
    Assert-NotContains $installer $removedWebView2Gate `
        "Electron installer must not retain the WebView2 installation gate: '$removedWebView2Gate'."
}

# The Electron build chain is deliberately direct Packager 20: Forge 7 pins a
# vulnerable extract-zip dependency and must not return unnoticed.
if ([string]$globalJson.sdk.version -ne '10.0.400' -or [string]$globalJson.sdk.rollForward -ne 'disable') {
    throw 'Release builds must use the exact reviewed .NET SDK 10.0.400.'
}
if ([string]$sidecarProject.Project.PropertyGroup.RuntimeFrameworkVersion -ne '10.0.11') {
    throw 'The self-contained Sidecar runtime must remain explicitly pinned to 10.0.11.'
}
if ([string]$electronManifest.devDependencies.'@electron/packager' -ne '20.3.0') {
    throw 'Electron Packager must stay exactly pinned to the reviewed 20.3.0 release.'
}
if ($null -ne $electronManifest.devDependencies.'@electron-forge/cli' -or
    $null -ne $electronManifest.devDependencies.'@electron-forge/maker-zip') {
    throw 'Electron Forge must not be reintroduced into the public release build chain.'
}
if ([string]$electronManifest.engines.node -ne '22.23.2') {
    throw 'Electron must require the exact Node.js version used by CI and Release.'
}
if ([string]$electronManifest.scripts.package -ne 'npm run build && node scripts/verify-legal-payload.mjs && node scripts/package.mjs') {
    throw 'Electron package script must verify legal payload before using the reviewed direct Packager entry point.'
}
Assert-Contains $electronPackager "import { packager } from '@electron/packager';" `
    'Electron packaging must call @electron/packager directly.'
Assert-Contains $electronPackager 'electronVersion,' `
    'Electron Packager must receive the exact manifest/lock/installed runtime version.'
Assert-Contains $electronPackager 'strictlyRequireAllFuses: true' `
    'Electron packaging must fail closed when the V1 fuse wire changes.'
Assert-Contains $electronPackager "path.join(stagingRoot, 'THIRD-PARTY-INVENTORY.md')" `
    'Electron packaging must include the generated legal inventory.'
Assert-Contains $electronPackager "path.join(stagingRoot, 'LICENSES')" `
    'Electron packaging must include retained .NET and upstream licenses.'
Assert-Contains $electronLegalVerifier "'-OutputRoot', generatedRoot" `
    'Local packaging must regenerate legal payload into an isolated directory.'
Assert-Contains $electronLegalVerifier 'assertLegalPayloadIsCurrent(projectRoot, generatedRoot)' `
    'Local packaging must compare every generated legal file byte-for-byte.'
Assert-Contains $electronReleaseIntegrity "lock.packages?.['node_modules/electron']?.version" `
    'Electron version verification must include the installed lockfile entry.'
Assert-Contains $electronReleaseIntegrity 'Third-party inventory contains UNKNOWN - review required' `
    'Local packaging must reject unresolved dependency licenses.'
Assert-Contains $inventoryGenerator 'THIRD[-_. ]?PARTY' `
    'Legal inventory generation must retain nonstandard third-party notice filenames.'
Assert-Ordered $inventoryGenerator @(
    'if ($rows.Where({ $_.License -eq ''UNKNOWN - review required'' })',
    '$stagingRoot ='
) 'Legal inventory generation must reject UNKNOWN licenses before writing outputs.'

foreach ($workflow in @($ciWorkflow, $releaseWorkflow)) {
    Assert-Contains $workflow 'node-version: 22.23.2' `
        'CI and Release must use the exact Node.js version supported by the Electron workspace.'
    Assert-Contains $workflow 'src/CodexU.Web/package-lock.json' `
        'CI and Release must cache the Web lockfile.'
    Assert-Contains $workflow 'src/CodexU.Electron/package-lock.json' `
        'CI and Release must cache the Electron lockfile.'
    Assert-Matches $workflow 'working-directory:\s*src/CodexU\.Electron\s+run:\s*npm ci' `
        'CI and Release must install the locked Electron dependencies.'
    Assert-Contains $workflow 'npm audit --audit-level=high' `
        'CI and Release must audit the full Electron build and runtime graph.'
    Assert-NotContains $workflow 'npm audit --omit=dev' `
        'CI and Release must not hide Electron build-chain advisories with --omit=dev.'
    Assert-Matches $workflow 'name:\s*Audit Electron dependency graph\s+working-directory:\s*src/CodexU\.Electron\s+run:\s*npm audit --audit-level=high' `
        'CI and Release must run the Electron audit as an independently failing step.'
    Assert-Matches $workflow 'name:\s*Verify Electron dependency policy\s+working-directory:\s*src/CodexU\.Electron\s+run:\s*npm run verify:dependency-policy' `
        'CI and Release must independently reject the legacy extract-zip dependency.'
    Assert-Contains $workflow 'dotnet publish src/CodexU.Sidecar/CodexU.Sidecar.csproj' `
        'CI and Release must publish the self-contained .NET Sidecar.'
    Assert-Ordered $workflow @(
        'dotnet publish src/CodexU.Sidecar/CodexU.Sidecar.csproj',
        './tools/Generate-ThirdPartyInventory.ps1',
        'npm run package'
    ) 'CI and Release must derive legal files from the exact published Sidecar before packaging.'
    Assert-Contains $workflow 'LICENSES/dotnet-runtime-ThirdPartyNotices.txt' `
        'CI and Release must reject stale self-contained runtime notices.'
    Assert-Matches $workflow 'working-directory:\s*src/CodexU\.Electron\s+run:\s*npm run package' `
        'CI and Release must build the Electron Windows package.'
    Assert-Contains $workflow './tools/Test-PackagedElectron.ps1 -ApplicationDirectory src/CodexU.Electron/out/CodexU-win32-x64' `
        'CI and Release must use the shared packaged Electron smoke test.'
    Assert-Contains $workflow 'CODEXU_PUBLISH_DIR: ${{ github.workspace }}\src\CodexU.Electron\out\CodexU-win32-x64' `
        'CI and Release installers must consume the verified Electron package directory.'
    Assert-Matches $workflow '\./tools/Test-ElectronInstaller\.ps1.*?-InstallerPath' `
        'CI and Release must install, smoke-test and uninstall the generated setup.'
    Assert-Contains $workflow 'CodexU-0.5.0-win-x64-setup.exe' `
        'CI and Release must exercise a real same-AppId upgrade from the public v0.5.0 WPF installer.'
    Assert-Contains $workflow '0f01958eeca60ac5ee57658680af5120d55e6dfaa786a8ed30bf76600bbb2b21' `
        'CI and Release must pin the v0.5.0 migration fixture by SHA-256.'
    Assert-Contains $workflow '-LegacyInstallerPath artifacts/legacy/CodexU-0.5.0-win-x64-setup.exe' `
        'CI and Release must pass the verified v0.5.0 installer to the shared migration test.'
    Assert-Contains $workflow '-ExpectedPackageDirectory src/CodexU.Electron/out/CodexU-win32-x64' `
        'CI and Release must compare the upgraded installation with the exact packaged Electron payload.'
    Assert-Ordered $workflow @(
        'name: Download verified v0.5.0 WPF installer',
        'name: Smoke-test',
        '-LegacyInstallerPath artifacts/legacy/CodexU-0.5.0-win-x64-setup.exe'
    ) 'CI and Release must verify the legacy installer before running the real upgrade test.'
    Assert-Matches $workflow 'name:\s*Install pinned Inno Setup.*?choco install innosetup --version=6\.7\.1.*?LASTEXITCODE.*?Test-Path -LiteralPath \$compiler' `
        'CI and Release must independently install the pinned Inno Setup compiler and verify its executable exists.'
    Assert-Matches $workflow '\$compilerOutput\s*=\s*@\(& \$compiler ''installer\\CodexU\.iss'' 2>&1\).*?\$compilerExitCode\s*=\s*\$LASTEXITCODE.*?Compiler engine version: Inno Setup 6\.7\.1' `
        'CI and Release must verify the compiler-reported Inno Setup 6.7.1 engine version.'
    Assert-NotContains $workflow 'Test-PublishedApp.ps1' `
        'The public automation must not return to the legacy WPF smoke test.'
    Assert-NotContains $workflow 'artifacts/publish/CodexU.App.exe' `
        'The public automation must not package the legacy WPF executable.'
}

Assert-Matches $ciWorkflow `
    "name:\s*Resolve CI installer version.*?\[xml\]\`$props\s*=\s*Get-Content -LiteralPath 'Directory\.Build\.props'.*?\`$version\s*=\s*\(\[string\]\`$props\.Project\.PropertyGroup\.Version\)\.Trim\(\).*?\`$numericVersion\s*=\s*\`$versionMatch\.Groups\['numeric'\]\.Value" `
    'CI must derive its installer product and numeric versions from Directory.Build.props.'
Assert-Contains $ciWorkflow "if ([version]`$numericVersion -le [version]'0.5.0')" `
    'The CI installer numeric version must be newer than the real v0.5.0 migration fixture.'
Assert-Contains $ciWorkflow 'CODEXU_VERSION: ${{ steps.installer_version.outputs.version }}' `
    'The CI installer product version must use the version resolved from Directory.Build.props.'
Assert-Contains $ciWorkflow 'CODEXU_NUMERIC_VERSION: ${{ steps.installer_version.outputs.numeric_version }}' `
    'The CI installer numeric version must use the numeric version resolved from Directory.Build.props.'
Assert-Contains $ciWorkflow '$installerPath = "artifacts/installer/CodexU-$version-win-x64-setup.exe"' `
    'CI must construct the installer path from the resolved product version.'
Assert-Ordered $ciWorkflow @(
    '"installer_path=$installerPath" >> $env:GITHUB_OUTPUT',
    'Get-Item -LiteralPath ''${{ steps.installer_version.outputs.installer_path }}''',
    '-InstallerPath ''${{ steps.installer_version.outputs.installer_path }}'''
) 'CI must pass the same dynamically versioned installer from build verification to the smoke test.'
if ([Regex]::IsMatch($ciWorkflow, 'CODEXU_(?:NUMERIC_)?VERSION:\s*[0-9]')) {
    throw 'CI installer versions must be dynamically resolved rather than hard-coded.'
}

Assert-Contains $releaseWorkflow 'github.rest.repos.compareCommitsWithBasehead' `
    'Release inspection must compare an existing tag commit with the triggering main commit.'
Assert-Contains $releaseWorkflow 'basehead: `${object.sha}...${context.sha}`' `
    'Release inspection must use the existing tag as the comparison base and main as the head.'
Assert-Matches $releaseWorkflow `
    "if\s*\(!\['ahead',\s*'identical'\]\.includes\(comparison\.data\.status\)\)\s*\{.*?throw\s+new\s+Error" `
    'Release inspection must reject existing tags that are not equal to or ancestors of main.'
Assert-Contains $releaseWorkflow 'name: Verify checked-out release identity' `
    'Release build must verify the checked-out release identity before building.'
Assert-Contains $releaseWorkflow 'Release version mismatch after checkout' `
    'Release build must reject a checked-out commit whose version differs from the inspected version.'
Assert-Contains $releaseWorkflow 'Refusing to modify published release' `
    'Release inspection must fail closed instead of repairing an incomplete published release.'
Assert-Contains $releaseWorkflow 'Refusing to overwrite published release' `
    'Release publication must never overwrite an existing public binary.'
Assert-Contains $releaseWorkflow 'release.assets.length === expectedAssets.length' `
    'Release inspection must require the exact public asset count.'
Assert-Contains $releaseWorkflow 'await checksumMatches(process.env.RELEASE_ARCHIVE' `
    'Release inspection must validate public checksum content against the asset digest.'
Assert-Contains $releaseWorkflow 'unexpectedAssets.length > 0' `
    'Release publication must reject draft assets outside the expected set.'
Assert-Contains $releaseWorkflow 'actual.digest === expected.digest' `
    'Release publication must verify uploaded SHA-256 digests.'
Assert-Contains $releaseWorkflow 'Downloaded release checksum does not match' `
    'Release publication must revalidate checksum contents after downloading the cross-job artifact.'

Assert-Contains $releaseWorkflow './tools/Sign-ReleaseArtifact.ps1 -Path src/CodexU.Electron/out/CodexU-win32-x64/CodexU.exe' `
    'Release must sign the Electron executable before packaging assets.'
Assert-Contains $releaseWorkflow './tools/Sign-ReleaseArtifact.ps1 -Path src/CodexU.Electron/out/CodexU-win32-x64/resources/backend/CodexU.Sidecar.exe' `
    'Release must sign the .NET Sidecar executable before packaging assets.'
Assert-Contains $releaseWorkflow 'Compress-Archive -Path "$packageDirectory/*"' `
    'Release ZIP must be created from the verified Electron package directory.'
Assert-Ordered $releaseWorkflow @(
    'name: Sign Electron executables when configured',
    'name: Verify signed Electron package',
    'name: Build per-user Windows installer',
    'name: Sign Windows installer when configured',
    'name: Smoke-test final Windows installer',
    'name: Package release assets',
    'Get-FileHash -LiteralPath $archivePath',
    'name: Upload release payload'
) 'Release signing, smoke testing, checksums and upload must remain in the safe order.'

Write-Host 'Electron release automation safeguards verified.'
