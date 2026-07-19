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
            [string]$ExtraText
        )

        $checkMark = if ($Checked) { 'x' } else { ' ' }
        $lines = @(
            '## GUI release manual safety evidence'
            ''
            'Completed manual-test Issue: https://github.com/OWNER/REPOSITORY/issues/123'
            ''
        )
        $lines += $script:RequiredScenarios | ForEach-Object { "- [$checkMark] $_" }
        if (-not [string]::IsNullOrWhiteSpace($ExtraText)) {
            $lines += ''
            $lines += $ExtraText
        }
        return $lines -join "`n"
    }
}

Describe 'GUI release manual evidence repository policy' {
    It 'rejects a release PR to main with a missing body' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef release/0.3.0 `
                -PullRequestBody ''
        } | Should -Throw -ExpectedMessage '*requires completed GUI manual safety evidence*'
    }

    It 'rejects a missing manual-test Issue reference' {
        $body = (Get-GuiEvidenceBody) -replace '(?m)^Completed manual-test Issue:.*$', 'Completed manual-test Issue:'

        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef release/0.3.0 `
                -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must reference the completed manual-test GitHub Issue*'
    }

    It 'rejects unchecked scenario lines' {
        $body = Get-GuiEvidenceBody -Checked $false

        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef release/0.3.0 `
                -PullRequestBody $body
        } | Should -Throw -ExpectedMessage '*must check the GUI safety evidence item*'
    }

    It 'treats a malicious multiline body as data' {
        $sentinelPath = Join-Path $TestDrive 'must-not-exist.txt'
        $payload = @(
            'A multiline note remains untrusted.'
            '$(Set-Content -LiteralPath ''__SENTINEL__'' -Value ''injected'')'
        ) -join "`n"
        $payload = $payload.Replace('__SENTINEL__', $sentinelPath.Replace("'", "''"))
        $body = Get-GuiEvidenceBody -ExtraText $payload

        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef release/0.3.0 `
                -PullRequestBody $body
        } | Should -Not -Throw
        $sentinelPath | Should -Not -Exist
    }

    It 'accepts a release PR to main with complete evidence' {
        $body = Get-GuiEvidenceBody

        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef release/0.3.0 `
                -PullRequestBody $body
        } | Should -Not -Throw
    }

    It 'does not require GUI evidence for a normal develop PR' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef develop `
                -HeadRef ci/issue-39-gui-release-evidence
        } | Should -Not -Throw
    }

    It 'passes the PR body through the environment instead of the run script' {
        $workflow = Get-Content -LiteralPath $script:WorkflowPath -Raw

        $workflow | Should -Match '(?m)^\s+PR_BODY:\s+\$\{\{ github\.event\.pull_request\.body \}\}\s*$'
        ([regex]::Matches($workflow, '\$\{\{ github\.event\.pull_request\.body \}\}')).Count | Should -Be 1
        $workflow | Should -Match '(?m)^\s+-PullRequestBody \$env:PR_BODY\s*$'
    }
}
