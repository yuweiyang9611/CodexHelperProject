#Requires -Version 7.2
[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [switch]$Install, [switch]$Desktop, [switch]$Package)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$repo = Split-Path -Parent $PSScriptRoot
Push-Location $repo
try {
    & "$PSScriptRoot/Verify-Environment.ps1" -Dotnet $Dotnet
    $Dotnet = (Get-Command $Dotnet).Source
    foreach ($project in @('CodexU.Web', 'CodexU.Electron')) {
        Push-Location "src/$project"
        try { if ($Install) { npm.cmd ci } } finally { Pop-Location }
    }
    & $Dotnet test CodexU.sln --configuration Release
    Push-Location src/CodexU.Web
    try { npm.cmd run test:unit; npm.cmd run build; npm.cmd run test:e2e } finally { Pop-Location }
    Push-Location src/CodexU.Electron
    try { npm.cmd test } finally { Pop-Location }
    if ($Desktop -or $Package) {
        & $Dotnet publish src/CodexU.Sidecar/CodexU.Sidecar.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishReadyToRun=false -p:DebugSymbols=false -p:DebugType=None --output src/CodexU.Electron/backend
        if ($Desktop) {
            Push-Location src/CodexU.Electron
            try { node scripts/test-desktop.cjs } finally { Pop-Location }
        }
        if ($Package) {
            & "$PSScriptRoot/Generate-ThirdPartyInventory.ps1"
            Push-Location src/CodexU.Electron
            try { npm.cmd run package } finally { Pop-Location }
            & "$PSScriptRoot/Test-PackagedElectron.ps1" -ApplicationDirectory src/CodexU.Electron/out/CodexU-win32-x64
        }
    }
} finally { Pop-Location }
