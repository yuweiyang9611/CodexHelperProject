#Requires -Version 7.2
$ErrorActionPreference = 'Stop'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('codexu-verify-' + [guid]::NewGuid().ToString('N'))
$originalPath = $env:PATH
$originalFailure = $env:CODEXU_VERIFY_TEST_FAIL
$originalLog = $env:CODEXU_VERIFY_TEST_LOG
try {
    New-Item -ItemType Directory -Path "$testRoot/tools", "$testRoot/src/CodexU.Web", "$testRoot/src/CodexU.Electron", "$testRoot/bin" -Force | Out-Null
    Copy-Item "$PSScriptRoot/Verify-Project.ps1", "$PSScriptRoot/Invoke-CheckedNative.ps1" "$testRoot/tools"
    Set-Content "$testRoot/tools/Verify-Environment.ps1" 'param($Dotnet)'
    Set-Content "$testRoot/tools/Generate-ThirdPartyInventory.ps1" ''
    Set-Content "$testRoot/tools/Test-PackagedElectron.ps1" 'param($ApplicationDirectory); Add-Content $env:CODEXU_VERIFY_TEST_LOG finished'
    foreach ($name in @('dotnet', 'npm', 'node')) {
        Set-Content "$testRoot/bin/$name.cmd" @('@echo off', "echo $name %*>>`"%CODEXU_VERIFY_TEST_LOG%`"", "if `"%CODEXU_VERIFY_TEST_FAIL%`"==`"$name %*`" exit /b 17", 'exit /b 0')
    }
    $env:PATH = "$testRoot/bin;$originalPath"
    $env:CODEXU_VERIFY_TEST_LOG = "$testRoot/invocations.log"
    foreach ($failure in @('npm ci', 'dotnet test CodexU.sln --configuration Release', 'npm run build', 'npm run package')) {
        $env:CODEXU_VERIFY_TEST_FAIL = $failure
        Remove-Item -LiteralPath $env:CODEXU_VERIFY_TEST_LOG -ErrorAction SilentlyContinue
        $PSNativeCommandUseErrorActionPreference = $false
        $result = & (Join-Path $PSHOME 'pwsh.exe') -NoProfile -File "$testRoot/tools/Verify-Project.ps1" -Dotnet "$testRoot/bin/dotnet.cmd" -Install -Package 2>&1
        if ($LASTEXITCODE -eq 0) { throw "Verification succeeded after injected failure: $failure" }
        $calls = @(Get-Content $env:CODEXU_VERIFY_TEST_LOG)
        if ($calls[-1].Trim() -ne $failure) { throw "Commands continued after failure: $failure; $calls" }
        if (($result -join "`n") -notmatch 'exit 17') { throw "Missing command failure context: $result" }
    }
    Write-Output 'Verification failure propagation passed: install, test, build, package.'
} finally {
    $env:PATH = $originalPath
    $env:CODEXU_VERIFY_TEST_FAIL = $originalFailure
    $env:CODEXU_VERIFY_TEST_LOG = $originalLog
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
# The final child intentionally failed. Do not leak its exit code into the
# GitHub Actions PowerShell wrapper after all assertions have passed.
exit 0
