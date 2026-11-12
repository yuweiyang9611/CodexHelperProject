#Requires -Version 7.2
[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [string]$Node = 'node')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
# The projects target net10.0, so any installed SDK from 10 onwards can build them.
$minimumSdkMajor = 10
$expectedNode = (Get-Content (Join-Path $repo 'src/CodexU.Electron/package.json') -Raw | ConvertFrom-Json).engines.node
$actualSdk = "$(& $Dotnet --version)".Trim()
if ($LASTEXITCODE -ne 0 -or $actualSdk -notmatch '^(\d+)\.\d+\.\d+' -or [int]$Matches[1] -lt $minimumSdkMajor) { throw "Expected .NET SDK $minimumSdkMajor or later; resolved '$actualSdk'. Install a newer SDK or update PATH." }
$actualNode = & $Node --version
if ($LASTEXITCODE -ne 0 -or $actualNode.TrimStart('v') -ne $expectedNode) { throw "Expected Node $expectedNode; resolved $actualNode. Update PATH before validation." }
Write-Output "Verified .NET SDK $actualSdk and Node $expectedNode."
