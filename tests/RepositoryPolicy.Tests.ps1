BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    $script:PolicyPath = Join-Path $script:RepoRoot 'scripts\Test-RepositoryPolicy.ps1'
    $script:WorkflowPath = Join-Path $script:RepoRoot '.github\workflows\ci.yml'
    $script:RequiredScenarios = @(
        'Notification warning was verified once for each of two restriction transitions.'
        'Deleting the enable marker stopped locking, including after restart.'
        'Alternate-account recovery was demonstrated or recorded as unavailable/conditional.'
        'WinRE/Safe Mode recovery was demonstrated or recorded as unavailable/conditional.'
        'The final state was safe: marker absent, Engine disarmed/paused, and task disabled.'
    )

    function Get-GuiEvidenceBody {
        param(
            [Parameter()]
            [bool]$Checked = $true,

            [Parameter()]
            [string]$IssueReference = 'https://github.com/OWNER/REPOSITORY/issues/123',

            [Parameter()]
            [string]$ExtraText
        )

        $checkMark = if ($Checked) { 'x' } else { ' ' }
        $lines = @(
            '## GUI release manual safety evidence'
            ''
            "Completed manual-test Issue: $IssueReference"
            ''
        )
        $lines += $script:RequiredScenarios | ForEach-Object { "- [$checkMark] $_" }
        if (-not [string]::IsNullOrWhiteSpace($ExtraText)) {
            $lines += ''
            $lines += $ExtraText
        }
        return $lines -join "`n"
    }

    function Get-ValidPolicyContext {
        return @{
            EventName                 = 'pull_request'
            BaseRef                  = 'main'
            HeadRef                  = 'release/0.3.0'
            RepositoryFullName       = 'OWNER/REPOSITORY'
            ManualEvidenceIssueNumber = 123
            ManualEvidenceIssueState = 'CLOSED'
            ManualEvidenceIssueLabels = @('safety', 'manual-test')
        }
    }
}

Describe 'GUI release manual evidence repository policy' {
    It 'rejects a release PR to main with a missing body' {
        $parameters = Get-ValidPolicyContext

        {
            & $script:PolicyPath @parameters -PullRequestBody ''
        } | Should -Throw -ExpectedMessage '*requires completed GUI manual safety evidence*'
    }

    It 'rejects an Issue URL for another repository' {
        $parameters = Get-ValidPolicyContext
        $body = Get-GuiEvidenceBody -IssueReference 'https://github.com/OTHER/REPOSITORY/issues/123'

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must reference a manual-test Issue in this repository*'
    }

    It 'rejects an Issue missing from the trusted lookup' {
        $parameters = Get-ValidPolicyContext
        $parameters.Remove('ManualEvidenceIssueNumber')
        $body = Get-GuiEvidenceBody

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*could not be verified by the trusted GitHub lookup*'
    }

    It 'rejects trusted metadata for a different Issue number' {
        $parameters = Get-ValidPolicyContext
        $parameters.ManualEvidenceIssueNumber = 124
        $body = Get-GuiEvidenceBody

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*trusted GitHub lookup did not match*'
    }
    It 'rejects an open Issue' {
        $parameters = Get-ValidPolicyContext
        $parameters.ManualEvidenceIssueState = 'OPEN'
        $body = Get-GuiEvidenceBody

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must exist and be CLOSED*'
    }

    It 'rejects an Issue without the manual-test label' {
        $parameters = Get-ValidPolicyContext
        $parameters.ManualEvidenceIssueLabels = @('safety')
        $body = Get-GuiEvidenceBody

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage "*must have the 'manual-test' label*"
    }

    It 'rejects unchecked scenario lines' {
        $parameters = Get-ValidPolicyContext
        $body = Get-GuiEvidenceBody -Checked $false

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must check the GUI safety evidence item*'
    }

    It 'does not count checked scenarios inside a fenced code block' {
        $parameters = Get-ValidPolicyContext
        $fencedLines = @('```markdown')
        $fencedLines += $script:RequiredScenarios | ForEach-Object { "- [x] $_" }
        $fencedLines += '```'
        $body = Get-GuiEvidenceBody -Checked $false -ExtraText ($fencedLines -join "`n")

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must check the GUI safety evidence item*'
    }

    It 'rejects evidence hidden entirely in an HTML comment' {
        $parameters = Get-ValidPolicyContext
        $body = "<!--`n$(Get-GuiEvidenceBody)`n-->"

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must reference a manual-test Issue in this repository*'
    }

    It 'rejects a hidden Issue reference with visible checklist items' {
        $parameters = Get-ValidPolicyContext
        $body = (Get-GuiEvidenceBody) -replace '(?m)^Completed manual-test Issue:.*$', 'Completed manual-test Issue: <!-- #123 -->'

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must reference a manual-test Issue in this repository*'
    }

    It 'does not count hidden checked items mixed with a visible unchecked checklist' {
        $parameters = Get-ValidPolicyContext
        $hiddenChecklist = @('<!--')
        $hiddenChecklist += $script:RequiredScenarios | ForEach-Object { "- [x] $_" }
        $hiddenChecklist += '-->'
        $body = Get-GuiEvidenceBody -Checked $false -ExtraText ($hiddenChecklist -join "`n")

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must check the GUI safety evidence item*'
    }

    It 'accepts complete CRLF evidence from a closed labeled Issue' {
        $parameters = Get-ValidPolicyContext
        $body = (Get-GuiEvidenceBody).Replace("`n", "`r`n")

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Not -Throw
    }

    It 'accepts a same-repository number reference' {
        $parameters = Get-ValidPolicyContext
        $body = Get-GuiEvidenceBody -IssueReference '#123'

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Not -Throw
    }

    It 'treats a malicious multiline body as data' {
        $parameters = Get-ValidPolicyContext
        $sentinelPath = Join-Path $TestDrive 'must-not-exist.txt'
        $payload = @(
            'A multiline note remains untrusted.'
            '$(Set-Content -LiteralPath ''__SENTINEL__'' -Value ''injected'')'
        ) -join "`r`n"
        $payload = $payload.Replace('__SENTINEL__', $sentinelPath.Replace("'", "''"))
        $body = Get-GuiEvidenceBody -ExtraText $payload

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Not -Throw
        $sentinelPath | Should -Not -Exist
    }

    It 'does not require GUI evidence for a normal develop PR' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef develop `
                -HeadRef ci/issue-39-gui-release-evidence
        } | Should -Not -Throw
    }

    It 'extracts only an integer Issue number for the trusted lookup' {
        $body = Get-GuiEvidenceBody -IssueReference '#123'

        $issueNumber = & $script:PolicyPath `
            -EventName pull_request `
            -BaseRef main `
            -HeadRef release/0.3.0 `
            -PullRequestBody $body `
            -RepositoryFullName OWNER/REPOSITORY `
            -GetManualEvidenceIssueNumber

        $issueNumber | Should -Be 123
    }

    It 'passes untrusted body via the environment and looks up trusted Issue state' {
        $workflow = Get-Content -LiteralPath $script:WorkflowPath -Raw

        $workflow | Should -Match '(?m)^\s+issues:\s+read\s*$'
        $workflow | Should -Match '(?m)^\s+PR_BODY:\s+\$\{\{ github\.event\.pull_request\.body \}\}\s*$'
        ([regex]::Matches($workflow, '\$\{\{ github\.event\.pull_request\.body \}\}')).Count | Should -Be 1
        $workflow | Should -Match '(?m)^\s+GH_TOKEN:\s+\$\{\{ github\.token \}\}\s*$'
        $workflow | Should -Match 'gh issue view \$issueNumber'
        $workflow | Should -Match '--repo \$env:REPOSITORY_FULL_NAME'
        $workflow | Should -Match 'ManualEvidenceIssueState\s+= \[string\]\$issue\.state'
        $workflow | Should -Not -Match 'gh issue view \$env:PR_BODY'
    }
}
