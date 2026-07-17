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
    TagName        = $TagName
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

Assert-PullRequestFlow
Assert-ConventionalCommit
Assert-NoPrivateIdentifier
Assert-ReleaseTag
Write-Output 'Repository policy checks passed.'
