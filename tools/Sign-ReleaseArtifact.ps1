[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path
)

$ErrorActionPreference = 'Stop'
$certificateBase64 = $env:WINDOWS_SIGNING_CERTIFICATE_BASE64
$certificatePassword = $env:WINDOWS_SIGNING_CERTIFICATE_PASSWORD
if ([string]::IsNullOrWhiteSpace($certificateBase64) -or [string]::IsNullOrWhiteSpace($certificatePassword)) {
    Write-Host "Windows signing certificate is not configured; leaving '$Path' unsigned."
    return
}

$target = (Resolve-Path -LiteralPath $Path).Path
$certificatePath = Join-Path $env:RUNNER_TEMP 'codexu-release-signing.pfx'
try {
    [IO.File]::WriteAllBytes($certificatePath, [Convert]::FromBase64String($certificateBase64))
    $signTool = Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -Recurse |
        Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if ([string]::IsNullOrWhiteSpace($signTool)) {
        throw 'signtool.exe was not found on the release runner.'
    }

    & $signTool sign /fd SHA256 /td SHA256 /tr https://timestamp.digicert.com /f $certificatePath /p $certificatePassword $target
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for '$target' with exit code $LASTEXITCODE."
    }
    & $signTool verify /pa /v $target
    if ($LASTEXITCODE -ne 0) {
        throw "Authenticode verification failed for '$target'."
    }
}
finally {
    Remove-Item -LiteralPath $certificatePath -Force -ErrorAction SilentlyContinue
}
