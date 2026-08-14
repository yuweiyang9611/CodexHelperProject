$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$installerPath = Join-Path $projectRoot 'installer\CodexU.iss'
$releaseWorkflowPath = Join-Path $projectRoot '.github\workflows\release.yml'
$installer = Get-Content -LiteralPath $installerPath -Raw -Encoding utf8
$releaseWorkflow = Get-Content -LiteralPath $releaseWorkflowPath -Raw -Encoding utf8

function Assert-Contains {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Expected,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if ($Content.IndexOf($Expected, [StringComparison]::Ordinal) -lt 0) {
        throw $FailureMessage
    }
}

function Assert-Matches {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Content,
        [Parameter(Mandatory = $true)]
        [string]$Pattern,
        [Parameter(Mandatory = $true)]
        [string]$FailureMessage
    )

    if (-not [Regex]::IsMatch($Content, $Pattern, [Text.RegularExpressions.RegexOptions]::Singleline)) {
        throw $FailureMessage
    }
}

$webView2ClientId = '{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
Assert-Contains $installer "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\$webView2ClientId" `
    'Installer must inspect the Microsoft-documented 64-bit machine WebView2 Runtime key.'
Assert-Contains $installer "Software\Microsoft\EdgeUpdate\Clients\$webView2ClientId" `
    'Installer must inspect the Microsoft-documented per-user WebView2 Runtime key.'
Assert-Contains $installer "RegQueryStringValue(RootKey, SubKeyName, 'pv', Version)" `
    'Installer must inspect the WebView2 Runtime pv REG_SZ value.'
Assert-Contains $installer 'https://developer.microsoft.com/microsoft-edge/webview2/#download-section' `
    'Installer must direct users to the official Microsoft WebView2 Runtime download page.'
Assert-Matches $installer `
    'function\s+InitializeSetup\(\):\s*Boolean;.*?if\s+IsWebView2RuntimeInstalled\(\)\s+then.*?Result\s*:=\s*True;.*?Exit;.*?ShellExec\(.*?WebView2RuntimeDownloadUrl.*?Result\s*:=\s*False;' `
    'Installer must allow setup only when WebView2 is present, open the official download page when absent, and block installation.'

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
    'Release build must reject a checked-out commit whose product version differs from the inspected main version.'

Write-Host 'Release automation safeguards verified.'
