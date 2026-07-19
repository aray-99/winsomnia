[CmdletBinding()]
param(
    [Parameter()]
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),

    [Parameter()]
    [string]$EventName,

    [Parameter()]
    [string]$BaseRef,

    [Parameter()]
    [string]$HeadRef,

    [Parameter()]
    [string]$BaseSha,

    [Parameter()]
    [string]$HeadSha,

    [Parameter()]
    [AllowEmptyString()]
    [string]$PullRequestBody,

    [Parameter()]
    [string]$RepositoryFullName,

    [Parameter()]
    [Nullable[int]]$ManualEvidenceIssueNumber,

    [Parameter()]
    [string]$ManualEvidenceIssueState,

    [Parameter()]
    [string[]]$ManualEvidenceIssueLabels,

    [Parameter()]
    [switch]$GetManualEvidenceIssueNumber,

    [Parameter()]
    [string]$TagName
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$policyContext = [pscustomobject]@{
    RepositoryRoot = $RepositoryRoot
    EventName      = $EventName
    BaseRef        = $BaseRef
    HeadRef        = $HeadRef
    BaseSha        = $BaseSha
    HeadSha        = $HeadSha
    PullRequestBody          = $PullRequestBody
    RepositoryFullName       = $RepositoryFullName
    ManualEvidenceIssueNumber = $ManualEvidenceIssueNumber
    ManualEvidenceIssueState = $ManualEvidenceIssueState
    ManualEvidenceIssueLabels = $ManualEvidenceIssueLabels
    TagName                  = $TagName
}

function Invoke-GitText {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments
    )

    $output = & git -C $policyContext.RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed: $($output -join [Environment]::NewLine)"
    }
    return @($output)
}

function Assert-PullRequestFlow {
    if ($policyContext.EventName -ne 'pull_request') {
        return
    }
    if ([string]::IsNullOrWhiteSpace($policyContext.BaseRef) -or [string]::IsNullOrWhiteSpace($policyContext.HeadRef)) {
        throw 'Pull request policy requires BaseRef and HeadRef.'
    }

    $allowed = switch -Regex ($policyContext.BaseRef) {
        '^main$' { $policyContext.HeadRef -match '^(release|hotfix)/[^/]+$'; break }
        '^develop$' { $policyContext.HeadRef -match '^(feature|fix|docs|chore|refactor|ci|test)/[^/]+$' -or $policyContext.HeadRef -match '^(main|release/[^/]+|hotfix/[^/]+)$'; break }
        '^release/[^/]+$' { $policyContext.HeadRef -match '^(fix|docs|chore|ci|test)/[^/]+$'; break }
        default { $false }
    }

    if (-not $allowed) {
        throw "Branch flow is not allowed: '$($policyContext.HeadRef)' -> '$($policyContext.BaseRef)'."
    }
}

function Test-IsGuiReleasePullRequest {
    return $policyContext.EventName -eq 'pull_request' -and
        $policyContext.BaseRef -eq 'main' -and
        $policyContext.HeadRef -match '^release/[^/]+$'
}

function ConvertTo-NormalizedMarkdown {
    param(
        [Parameter()]
        [AllowEmptyString()]
        [string]$Markdown
    )

    return ($Markdown.Replace("`r`n", "`n")).Replace("`r", "`n")
}

function ConvertTo-MarkdownWithoutFencedCodeBlock {
    param(
        [Parameter()]
        [AllowEmptyString()]
        [string]$Markdown
    )

    $visibleLines = [Collections.Generic.List[string]]::new()
    $fenceCharacter = $null
    $fenceLength = 0
    foreach ($line in (ConvertTo-NormalizedMarkdown -Markdown $Markdown) -split "`n") {
        if ($null -eq $fenceCharacter) {
            if ($line -match '^[ \t]{0,3}(?<fence>`{3,}|~{3,})') {
                $fenceCharacter = $Matches.fence.Substring(0, 1)
                $fenceLength = $Matches.fence.Length
                continue
            }

            $visibleLines.Add($line)
            continue
        }

        $closingPattern = '^[ \t]{0,3}' + [regex]::Escape($fenceCharacter) +
            "{$fenceLength,}[ \t]*$"
        if ($line -match $closingPattern) {
            $fenceCharacter = $null
            $fenceLength = 0
        }
    }

    return $visibleLines -join "`n"
}

function Get-ManualEvidenceIssueNumberFromBody {
    if ([string]::IsNullOrWhiteSpace($policyContext.PullRequestBody)) {
        throw 'A release PR to main requires completed GUI manual safety evidence in the PR body.'
    }
    if ($policyContext.RepositoryFullName -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') {
        throw 'GUI release evidence validation requires the trusted repository full name.'
    }

    $normalizedBody = ConvertTo-MarkdownWithoutFencedCodeBlock -Markdown $policyContext.PullRequestBody
    $escapedRepository = [regex]::Escape($policyContext.RepositoryFullName)
    $issueReferencePattern = '(?im)^[ \t]*Completed manual-test Issue:[ \t]*(?:' +
        "https://github\.com/$escapedRepository/issues/(?<urlNumber>[1-9][0-9]*)|#(?<shortNumber>[1-9][0-9]*))[ \t]*$"
    $issueReference = [regex]::Match($normalizedBody, $issueReferencePattern)
    if (-not $issueReference.Success) {
        throw 'A release PR to main must reference a manual-test Issue in this repository.'
    }

    $numberText = if ($issueReference.Groups['urlNumber'].Success) {
        $issueReference.Groups['urlNumber'].Value
    }
    else {
        $issueReference.Groups['shortNumber'].Value
    }
    $issueNumber = 0
    if (-not [int]::TryParse($numberText, [ref]$issueNumber) -or $issueNumber -le 0) {
        throw 'The completed manual-test Issue number is invalid.'
    }

    return $issueNumber
}

function Assert-GuiReleaseEvidence {
    if (-not (Test-IsGuiReleasePullRequest)) {
        return
    }

    $referencedIssueNumber = Get-ManualEvidenceIssueNumberFromBody
    if ($null -eq $policyContext.ManualEvidenceIssueNumber) {
        throw 'The referenced manual-test Issue could not be verified by the trusted GitHub lookup.'
    }
    if ([int]$policyContext.ManualEvidenceIssueNumber -ne $referencedIssueNumber) {
        throw 'The trusted GitHub lookup did not match the referenced manual-test Issue.'
    }
    if ($policyContext.ManualEvidenceIssueState -ne 'CLOSED') {
        throw 'The referenced manual-test Issue must exist and be CLOSED.'
    }
    if (@($policyContext.ManualEvidenceIssueLabels) -notcontains 'manual-test') {
        throw "The referenced Issue must have the 'manual-test' label."
    }

    $bodyWithoutFencedCode = ConvertTo-MarkdownWithoutFencedCodeBlock -Markdown $policyContext.PullRequestBody
    $requiredScenarios = @(
        'Notification warning was verified once for each of two restriction transitions.'
        'Deleting the enable marker stopped locking, including after restart.'
        'Alternate-account recovery was demonstrated or recorded as unavailable/conditional.'
        'WinRE/Safe Mode recovery was demonstrated or recorded as unavailable/conditional.'
        'The final state was safe: marker absent, Engine disarmed/paused, and task disabled.'
    )

    foreach ($scenario in $requiredScenarios) {
        $escapedScenario = [regex]::Escape($scenario)
        if ($bodyWithoutFencedCode -notmatch "(?im)^[ \t]*-[ \t]*\[x\][ \t]*$escapedScenario[ \t]*$") {
            throw "A release PR to main must check the GUI safety evidence item: '$scenario'"
        }
    }
}

function Assert-ConventionalCommit {
    if ([string]::IsNullOrWhiteSpace($policyContext.BaseSha) -or [string]::IsNullOrWhiteSpace($policyContext.HeadSha)) {
        return
    }

    $subjects = Invoke-GitText -Arguments @('log', '--format=%s', "$($policyContext.BaseSha)..$($policyContext.HeadSha)")
    foreach ($subject in $subjects) {
        if ($subject -notmatch '^(feat|fix|docs|refactor|test|chore|ci|build|perf|revert)(\([a-z0-9._-]+\))?!?: .+' -and
            $subject -notmatch '^Merge .+') {
            throw "Commit subject does not follow Conventional Commits: '$subject'."
        }
    }
}

function Assert-NoPrivateIdentifier {
    $extensions = @('.md', '.ps1', '.json', '.yml', '.yaml', '.txt', '.xml', '.cs', '.csproj', '.xaml', '.resx', '.slnx')
    $blockedValues = @()
    if (-not [string]::IsNullOrWhiteSpace($env:WINSOMNIA_PRIVATE_IDENTIFIERS)) {
        $blockedValues = @($env:WINSOMNIA_PRIVATE_IDENTIFIERS -split '[;\r\n]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }

    $trackedFiles = Invoke-GitText -Arguments @('ls-files')
    foreach ($relativePath in $trackedFiles) {
        $extension = [IO.Path]::GetExtension($relativePath)
        if ($extensions -notcontains $extension -and [IO.Path]::GetFileName($relativePath) -notin @('LICENSE', 'VERSION')) {
            continue
        }

        $path = Join-Path $policyContext.RepositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            continue
        }
        $content = Get-Content -LiteralPath $path -Raw

        foreach ($match in [regex]::Matches($content, '(?i)C:\\Users\\([^\\/:*?"<>|\r\n]+)')) {
            $profileName = $match.Groups[1].Value
            if ($profileName -ne 'USER') {
                throw "Developer-specific Windows profile path found in '$relativePath'."
            }
        }

        foreach ($blockedValue in $blockedValues) {
            if ($content.IndexOf($blockedValue, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "A private identifier from WINSOMNIA_PRIVATE_IDENTIFIERS was found in '$relativePath'."
            }
        }
    }
}

function Assert-ReleaseTag {
    if ([string]::IsNullOrWhiteSpace($policyContext.TagName)) {
        return
    }
    if ($policyContext.TagName -notmatch '^v(.+)$') {
        throw "Release tag must start with 'v': '$($policyContext.TagName)'."
    }

    $tagVersion = $Matches[1]
    $version = (Get-Content -LiteralPath (Join-Path $policyContext.RepositoryRoot 'VERSION') -Raw).Trim()
    if ($tagVersion -ne $version) {
        throw "Tag version '$tagVersion' does not match VERSION '$version'."
    }

    $changeLog = Get-Content -LiteralPath (Join-Path $policyContext.RepositoryRoot 'CHANGELOG.md') -Raw
    if ($changeLog.IndexOf("[$version]", [StringComparison]::Ordinal) -lt 0) {
        throw "CHANGELOG.md does not contain an entry for '$version'."
    }

    $null = Invoke-GitText -Arguments @('merge-base', '--is-ancestor', $policyContext.TagName, 'origin/main')
}

if ($GetManualEvidenceIssueNumber) {
    if (-not (Test-IsGuiReleasePullRequest)) {
        throw 'Manual evidence Issue extraction is only valid for a release PR to main.'
    }
    Write-Output (Get-ManualEvidenceIssueNumberFromBody)
    return
}

Assert-PullRequestFlow
Assert-GuiReleaseEvidence
Assert-ConventionalCommit
Assert-NoPrivateIdentifier
Assert-ReleaseTag
Write-Output 'Repository policy checks passed.'
