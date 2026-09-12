function Invoke-CheckedNative {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Stage,
          [Parameter(Mandatory)][string]$Command,
          [string[]]$Arguments = @())
    # Explicit exit checking works in PowerShell 7.2 as well as later versions.
    $PSNativeCommandUseErrorActionPreference = $false
    & $Command @Arguments
    if ($LASTEXITCODE -ne 0) { throw "[$Stage] $Command failed (exit $LASTEXITCODE)." }
}
