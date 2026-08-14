[CmdletBinding()]
param(
    [string]$EventPath = $env:GITHUB_EVENT_PATH,
    [switch]$RequireSafeLocalConfig,
    [switch]$ScanAllLocalBranches
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$emailPattern = '[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
$emailRegex = [regex]::new($emailPattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant)

function Test-NoreplyAddress {
    param([string]$Address)

    return $Address -match '(?i)@users\.noreply\.github\.com$' -or
        $Address -match '(?i)^noreply@github\.com$'
}

function Assert-NoEmailText {
    param(
        [AllowNull()]
        [string]$Text,
        [string]$Location
    )

    if (-not [string]::IsNullOrEmpty($Text) -and $emailRegex.IsMatch($Text)) {
        throw "Privacy check failed: an email address is present in $Location. The address is intentionally not printed."
    }
}

function Invoke-GitLines {
    param([string[]]$Arguments)

    $output = @(& git @Arguments)
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
    return $output
}

if ($RequireSafeLocalConfig) {
    $configuredEmail = (& git config --get user.email | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-NoreplyAddress $configuredEmail)) {
        throw 'Privacy check failed: this repository is not configured with a GitHub noreply email.'
    }

    $useConfigOnly = (& git config --get user.useConfigOnly | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $useConfigOnly -ne 'true') {
        throw 'Privacy check failed: user.useConfigOnly must be true for this repository.'
    }
}

$revision = if ($ScanAllLocalBranches) { '--branches' } else { 'HEAD' }
$metadataLines = @(Invoke-GitLines @('log', $revision, '--format=%H%x09%ae%x09%ce'))
foreach ($line in $metadataLines) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $fields = $line -split "`t", 3
    if ($fields.Count -ne 3) {
        throw 'Privacy check failed: malformed Git metadata output.'
    }

    $shortCommit = $fields[0].Substring(0, [Math]::Min(12, $fields[0].Length))
    if (-not (Test-NoreplyAddress $fields[1])) {
        throw "Privacy check failed: commit $shortCommit has a non-noreply author address. The address is intentionally not printed."
    }
    if (-not (Test-NoreplyAddress $fields[2])) {
        throw "Privacy check failed: commit $shortCommit has a non-noreply committer address. The address is intentionally not printed."
    }
}

$commits = @(Invoke-GitLines @('rev-list', $revision))
foreach ($commit in $commits) {
    if ([string]::IsNullOrWhiteSpace($commit)) { continue }
    $shortCommit = $commit.Substring(0, [Math]::Min(12, $commit.Length))
    $message = (Invoke-GitLines @('show', '-s', '--format=%B', $commit)) -join "`n"
    Assert-NoEmailText $message "the message of commit $shortCommit"

    $matchedFiles = @(& git grep -I -l -E $emailPattern $commit -- .)
    if ($LASTEXITCODE -notin @(0, 1)) {
        throw "git grep failed for commit $shortCommit with exit code $LASTEXITCODE."
    }

    foreach ($matchedFile in $matchedFiles) {
        $revisionPrefix = "${commit}:"
        $file = if ($matchedFile.StartsWith($revisionPrefix, [StringComparison]::Ordinal)) {
            $matchedFile.Substring($revisionPrefix.Length)
        } else { $matchedFile }

        if ($file -eq 'THIRD-PARTY-LICENSES.txt' -or $file.StartsWith('LICENSES/', [StringComparison]::Ordinal)) {
            continue
        }

        $content = (Invoke-GitLines @('show', "${commit}:$file")) -join "`n"
        $matches = @($emailRegex.Matches($content))
        $nonNoreplyMatches = @($matches | Where-Object { -not (Test-NoreplyAddress $_.Value) })
        if ($nonNoreplyMatches.Count -eq 0) { continue }

        $onlyReservedTestAddresses = $file.StartsWith('tests/', [StringComparison]::Ordinal) -and
            @($nonNoreplyMatches | Where-Object { $_.Value -notmatch '(?i)@(example\.com|example\.test)$' }).Count -eq 0
        if (-not $onlyReservedTestAddresses) {
            throw "Privacy check failed: $file in commit $shortCommit contains an email address. The address is intentionally not printed."
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($EventPath) -and (Test-Path -LiteralPath $EventPath -PathType Leaf)) {
    $event = Get-Content -LiteralPath $EventPath -Raw -Encoding utf8 | ConvertFrom-Json
    $eventTexts = New-Object System.Collections.Generic.List[object]

    if ($event.PSObject.Properties['pull_request']) {
        $eventTexts.Add([pscustomobject]@{ Location = 'the pull request title'; Text = [string]$event.pull_request.title })
        $eventTexts.Add([pscustomobject]@{ Location = 'the pull request body'; Text = [string]$event.pull_request.body })
    }
    if ($event.PSObject.Properties['issue']) {
        $eventTexts.Add([pscustomobject]@{ Location = 'the issue title'; Text = [string]$event.issue.title })
        $eventTexts.Add([pscustomobject]@{ Location = 'the issue body'; Text = [string]$event.issue.body })
    }
    if ($event.PSObject.Properties['comment']) {
        $eventTexts.Add([pscustomobject]@{ Location = 'the comment body'; Text = [string]$event.comment.body })
    }
    if ($event.PSObject.Properties['review']) {
        $eventTexts.Add([pscustomobject]@{ Location = 'the review body'; Text = [string]$event.review.body })
    }

    foreach ($item in $eventTexts) {
        Assert-NoEmailText $item.Text $item.Location
    }
}

Write-Host "Privacy check passed for $($commits.Count) commit(s); no private email was printed."
