#Requires -Version 7.2
[CmdletBinding()]
param([string]$Dotnet = 'dotnet', [string]$Node = 'node')
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$expectedSdk = (Get-Content (Join-Path $repo 'global.json') -Raw | ConvertFrom-Json).sdk.version
$expectedNode = (Get-Content (Join-Path $repo 'src/CodexU.Electron/package.json') -Raw | ConvertFrom-Json).engines.node
$actualSdk = & $Dotnet --version
if ($LASTEXITCODE -ne 0 -or $actualSdk.Trim() -ne $expectedSdk) { throw "Expected .NET SDK $expectedSdk; resolved $actualSdk. Use an isolated SDK or update PATH." }
$actualNode = & $Node --version
if ($LASTEXITCODE -ne 0 -or $actualNode.TrimStart('v') -ne $expectedNode) { throw "Expected Node $expectedNode; resolved $actualNode. Update PATH before validation." }
Write-Output "Verified .NET $expectedSdk and Node $expectedNode."
