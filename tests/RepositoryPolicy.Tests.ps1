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

Describe 'Emergency recovery documentation policy' {
    BeforeAll {
        $script:EmergencyPath = Join-Path $script:RepoRoot 'docs\EMERGENCY.md'
        $script:Emergency = Get-Content -LiteralPath $script:EmergencyPath -Raw
    }

    It 'keeps the exact signed-in v0.3 marker deletion and verification commands' {
        $expected = @"
Remove-Item -LiteralPath 'C:\temp\winsomnia-lock-enabled.json' -Force -ErrorAction SilentlyContinue
Test-Path -LiteralPath 'C:\temp\winsomnia-lock-enabled.json'
"@.Trim()

        $script:Emergency | Should -Match ([regex]::Escape($expected))
        $script:Emergency | Should -Match '(?s)期待結果:\s*```text\s*False\s*```'
    }

    It 'distinguishes v0.3 deletion from v0.2 legacy-switch creation' {
        $script:Emergency | Should -Match '### v0\.3'
        $script:Emergency | Should -Match '### v0\.2\.x'
        $script:Emergency | Should -Match "New-Item -ItemType File -LiteralPath 'C:\\temp\\win-somnia-unlock\.txt' -Force"
        $script:Emergency | Should -Match ([regex]::Escape("Stop-ScheduledTask -TaskName 'winsomnia' -ErrorAction SilentlyContinue"))
        $script:Emergency | Should -Match ([regex]::Escape("Disable-ScheduledTask -TaskName 'winsomnia'"))
        $script:Emergency | Should -Match ([regex]::Escape("Get-ScheduledTask -TaskName 'winsomnia' | Select-Object TaskName, State"))
        $script:Emergency | Should -Match '(?s)### v0\.2\.x.*?\$taskNames = @\(''winsomnia'',''win-somnia''\).*?### バージョンが分からない'
    }

    It 'documents durable v0.3 disarm before process shutdown and containment fallback' {
        $script:Emergency | Should -Match 'marker が `False` で Engine がまだ動作している間に、Desktop の `Pause / 一時停止`'
        $script:Emergency | Should -Match '`Armed: No`、認可状態が `Disarmed`'
        $script:Emergency | Should -Match 'Desktop または IPC が利用できない場合は、marker 削除とタスク停止・無効化までを緊急 containment'
        $script:Emergency | Should -Match 'Setup の safety barrier'
        $script:Emergency | Should -Match '内部 Engine CLI は復旧手順として公開・使用しません'
        $script:Emergency | Should -Match ([regex]::Escape("Where-Object ProcessName -EQ 'Winsomnia.Engine'"))
        $script:Emergency | Should -Match 'Desktop tray は残る設計です'
        $script:Emergency | Should -Match 'tray の終了はロック安全性の必須条件ではありません'
    }

    It 'reports unknown-version task and process outcomes explicitly' {
        $script:Emergency | Should -Match ([regex]::Escape('$taskNames = @(''winsomnia'',''win-somnia'')'))
        $script:Emergency | Should -Match "State = 'MISSING'"
        $script:Emergency | Should -Match ([regex]::Escape('$task | Stop-ScheduledTask'))
        $script:Emergency | Should -Match ([regex]::Escape('$task | Disable-ScheduledTask | Out-Null'))
        $script:Emergency | Should -Match 'V03Processes'
        $script:Emergency | Should -Match 'LegacyProcesses'
    }

    It 'keeps the exact WinRE marker absence check and volume discovery' {
        $script:Emergency | Should -Match '(?s)diskpart\s*list volume\s*exit'
        $script:Emergency | Should -Match ([regex]::Escape('del /f /q "%OSVOL%\temp\winsomnia-lock-enabled.json" 2>nul'))
        $script:Emergency | Should -Match ([regex]::Escape('if not exist "%OSVOL%\temp\winsomnia-lock-enabled.json" echo MARKER_ABSENT'))
    }

    It 'does not overstate what marker deletion accomplishes' {
        $script:Emergency | Should -Match 'Armed=false'
        $script:Emergency | Should -Match '現在表示中のロック画面を解除しません'
        $script:Emergency | Should -Match 'すでに発行された非同期の `LockWorkStation` 要求を取り消しません'
        $script:Emergency | Should -Match '制御実機試験が完了するまでは、到達可能または実証済みとは扱いません'
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

    It 'rejects four-space indented Issue and checklist evidence' {
        $parameters = Get-ValidPolicyContext
        $fullyIndentedBody = ((Get-GuiEvidenceBody) -split "`n" | ForEach-Object { "    $_" }) -join "`n"

        {
            & $script:PolicyPath @parameters -PullRequestBody $fullyIndentedBody
        } | Should -Throw -ExpectedMessage '*must reference a manual-test Issue in this repository*'

        $tabIndentedBody = ((Get-GuiEvidenceBody) -split "`n" | ForEach-Object { "`t$_" }) -join "`n"
        {
            & $script:PolicyPath @parameters -PullRequestBody $tabIndentedBody
        } | Should -Throw -ExpectedMessage '*must reference a manual-test Issue in this repository*'

        $visibleIssueWithIndentedChecklist = Get-GuiEvidenceBody
        foreach ($scenario in $script:RequiredScenarios) {
            $visibleIssueWithIndentedChecklist = $visibleIssueWithIndentedChecklist.Replace("- [x] $scenario", "    - [x] $scenario")
        }

        {
            & $script:PolicyPath @parameters -PullRequestBody $visibleIssueWithIndentedChecklist
        } | Should -Throw -ExpectedMessage '*must check the GUI safety evidence item*'
    }

    It 'keeps visible evidence after an unterminated HTML comment inside a fence' {
        $parameters = Get-ValidPolicyContext
        $body = @(
            '```text'
            '<!-- literal example without a terminator'
            '```'
            (Get-GuiEvidenceBody)
        ) -join "`n"

        {
            & $script:PolicyPath @parameters -PullRequestBody $body
        } | Should -Not -Throw
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
