[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$hooksDirectory = Join-Path $projectRoot '.githooks'
$prePushHook = Join-Path $hooksDirectory 'pre-push'

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git is not available on PATH.'
}

$gitRoot = (& git -C $projectRoot rev-parse --show-toplevel | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($gitRoot)) {
    throw "Not a Git worktree: $projectRoot"
}

if ([IO.Path]::GetFullPath($gitRoot) -ne $projectRoot) {
    throw "The script is not running from the expected repository root: $projectRoot"
}

if (-not (Test-Path -LiteralPath $hooksDirectory -PathType Container)) {
    throw "Versioned hooks directory not found: $hooksDirectory"
}

if (-not (Test-Path -LiteralPath $prePushHook -PathType Leaf)) {
    throw "Required pre-push hook not found: $prePushHook"
}

& git -C $projectRoot config --local core.hooksPath .githooks
if ($LASTEXITCODE -ne 0) {
    throw 'Failed to set the repository-local core.hooksPath.'
}

$configuredPath = (& git -C $projectRoot config --local --get core.hooksPath | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $configuredPath -ne '.githooks') {
    throw 'Git did not retain the expected repository-local core.hooksPath value.'
}

$effectiveHook = (& git -C $projectRoot rev-parse --path-format=absolute --git-path hooks/pre-push | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($effectiveHook)) {
    throw 'Git could not resolve the effective pre-push hook path.'
}

if ([IO.Path]::GetFullPath($effectiveHook) -ne [IO.Path]::GetFullPath($prePushHook)) {
    throw "Git resolved an unexpected pre-push hook path: $effectiveHook"
}

Write-Host 'Repository hooks enabled successfully.'
Write-Host "core.hooksPath=$configuredPath"
Write-Host "pre-push=$effectiveHook"
