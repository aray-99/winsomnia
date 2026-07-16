BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    $script:CommonPath = Join-Path $script:RepoRoot 'winsomnia-common.ps1'
    $script:MonitorPath = Join-Path $script:RepoRoot 'winsomnia-monitor.ps1'
    $script:SetupPath = Join-Path $script:RepoRoot 'winsomnia-setup.ps1'
    $script:CliPath = Join-Path $script:RepoRoot 'winsomnia.ps1'
    $script:BuildPath = Join-Path $script:RepoRoot 'build-release.ps1'
    $script:PolicyPath = Join-Path $script:RepoRoot 'scripts\Test-RepositoryPolicy.ps1'
    $script:WindowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    . $script:CommonPath
}

Describe 'winsomnia configuration' {
    It 'accepts the default overnight configuration' {
        $config = Get-WinSomniaDefaultConfig
        { Assert-WinSomniaConfig -Config $config } | Should -Not -Throw
    }

    It 'rejects equal start and end times' {
        $config = Get-WinSomniaDefaultConfig
        $config.startTime = '06:00'
        $config.endTime = '06:00'
        { Assert-WinSomniaConfig -Config $config } | Should -Throw
    }

    It 'rejects relative safety paths' {
        $config = Get-WinSomniaDefaultConfig
        $config.killSwitchPath = '.\unlock.txt'
        { Assert-WinSomniaConfig -Config $config } | Should -Throw
    }

    It 'round-trips a configuration file' {
        $configPath = Join-Path $TestDrive 'config.json'
        $config = Get-WinSomniaDefaultConfig
        $config.startTime = '22:30'
        $config.endTime = '05:45'
        Write-WinSomniaConfig -Config $config -Path $configPath | Out-Null

        $loaded = Read-WinSomniaConfig -Path $configPath
        $loaded.startTime | Should -Be '22:30'
        $loaded.endTime | Should -Be '05:45'
    }
}

Describe 'winsomnia monitor safety' {
    It 'waits for the lock helper process and checks its explicit exit code' {
        $monitorSource = Get-Content -LiteralPath $script:MonitorPath -Raw
        $monitorSource | Should -Match 'Start-Process'
        $monitorSource | Should -Match '\$process\.ExitCode'
        $monitorSource | Should -Not -Match '\$LASTEXITCODE'
    }

    It 'completes a bounded dry run without locking' {
        $logPath = Join-Path $TestDrive 'dry-run.log'
        $missingKillSwitch = Join-Path $TestDrive 'missing-unlock.txt'
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:MonitorPath
            '-DryRun', '-TestDurationSeconds', '2'
            '-StartTime', '00:00', '-EndTime', '23:59'
            '-IntervalSeconds', '1'
            '-KillSwitchPath', $missingKillSwitch
            '-LogPath', $logPath
        )

        $output = & $script:WindowsPowerShell @arguments 2>&1
        $LASTEXITCODE | Should -Be 0
        $output -join "`n" | Should -Match 'Dry run completed after 2 seconds'
        Test-Path -LiteralPath $logPath | Should -BeTrue
    }

    It 'exits before an explicitly enabled lock when the kill switch exists' {
        $killSwitch = Join-Path $TestDrive 'unlock.txt'
        New-Item -ItemType File -Path $killSwitch -Force | Out-Null
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:MonitorPath
            '-EnableLock', '-StartTime', '00:00', '-EndTime', '23:59'
            '-KillSwitchPath', $killSwitch
            '-LogPath', (Join-Path $TestDrive 'kill-switch.log')
        )

        $output = & $script:WindowsPowerShell @arguments 2>&1
        $LASTEXITCODE | Should -Be 0
        $output -join "`n" | Should -Match 'Exiting without locking'
    }

    It 'treats a directory at the kill-switch path as a stop request' {
        $killSwitch = Join-Path $TestDrive 'unlock-directory'
        New-Item -ItemType Directory -Path $killSwitch | Out-Null
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:MonitorPath
            '-EnableLock', '-StartTime', '00:00', '-EndTime', '23:59'
            '-KillSwitchPath', $killSwitch
            '-LogPath', (Join-Path $TestDrive 'directory-kill-switch.log')
        )

        $output = & $script:WindowsPowerShell @arguments 2>&1
        $LASTEXITCODE | Should -Be 0
        $output -join "`n" | Should -Match 'Exiting without locking'
    }

    It 'fails closed for an invalid time' {
        $standardOutput = Join-Path $TestDrive 'invalid.stdout.txt'
        $standardError = Join-Path $TestDrive 'invalid.stderr.txt'
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:MonitorPath
            '-DryRun', '-StartTime', '25:00', '-EndTime', '06:00'
            '-LogPath', (Join-Path $TestDrive 'invalid.log')
        )

        $process = Start-Process `
            -FilePath $script:WindowsPowerShell `
            -ArgumentList $arguments `
            -WindowStyle Hidden `
            -Wait `
            -PassThru `
            -RedirectStandardOutput $standardOutput `
            -RedirectStandardError $standardError

        $process.ExitCode | Should -Be 1
        Get-Content -LiteralPath $standardError -Raw | Should -Match 'StartTime'
    }
}

Describe 'winsomnia setup safety' {
    It 'does not write configuration or register during WhatIf' {
        $configPath = Join-Path $TestDrive 'whatif-config.json'
        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:SetupPath
            '-Action', 'Install', '-ConfigPath', $configPath
            '-StartTime', '23:00', '-EndTime', '06:00'
            '-KillSwitchPath', (Join-Path $TestDrive 'unlock.txt')
            '-LogPath', (Join-Path $TestDrive 'monitor.log')
            '-WhatIf'
        )

        $null = & $script:WindowsPowerShell @arguments 2>&1
        $LASTEXITCODE | Should -Be 0
        Test-Path -LiteralPath $configPath | Should -BeFalse
    }
}

Describe 'winsomnia integrated CLI' {
    It 'shows status from the selected configuration' {
        $configPath = Join-Path $TestDrive 'cli-config.json'
        $config = Get-WinSomniaDefaultConfig
        $config.startTime = '22:15'
        $config.endTime = '05:30'
        Write-WinSomniaConfig -Config $config -Path $configPath | Out-Null

        $arguments = @(
            '-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass'
            '-File', $script:CliPath, 'status', '-ConfigPath', $configPath
        )
        $output = & $script:WindowsPowerShell @arguments 2>&1

        $LASTEXITCODE | Should -Be 0
        $output -join "`n" | Should -Match '22:15-05:30'
    }
}

Describe 'winsomnia release package' {
    It 'builds a ZIP with a matching SHA-256 checksum' {
        $outputDirectory = Join-Path $TestDrive 'dist'
        $version = '0.1.0-test.1'
        $null = & $script:BuildPath -Version $version -OutputDirectory $outputDirectory

        $archivePath = Join-Path $outputDirectory "winsomnia-$version.zip"
        $checksumPath = Join-Path $outputDirectory "winsomnia-$version.sha256"
        Test-Path -LiteralPath $archivePath | Should -BeTrue
        Test-Path -LiteralPath $checksumPath | Should -BeTrue

        $expectedHash = (Get-Content -LiteralPath $checksumPath -Raw).Split(' ')[0]
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualHash | Should -Be $expectedHash

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object FullName)
            $entryNames | Should -Contain 'winsomnia.ps1'
            $entryNames | Should -Contain 'README.md'
            $entryNames | Should -Contain 'CONTRIBUTING.md'
            $entryNames | Should -Contain 'RELEASE.md'
            $entryNames | Should -Contain 'docs/EMERGENCY.md'
            $entryNames | Should -Contain 'LICENSE'
            $entryNames | Should -Contain 'VERSION'

            $readmeEntry = $archive.GetEntry('README.md')
            $reader = [IO.StreamReader]::new($readmeEntry.Open())
            try {
                $readme = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
            $relativeLinks = [regex]::Matches($readme, '\[[^\]]+\]\((?!https?://|#)([^)]+)\)')
            foreach ($link in $relativeLinks) {
                $entryNames | Should -Contain $link.Groups[1].Value
            }
        }
        finally {
            $archive.Dispose()
        }
    }
}

Describe 'winsomnia repository policy' {
    It 'accepts a release-blocker fix targeting a release branch' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef release/0.1.0 `
                -HeadRef fix/release-safety-guardrails
        } | Should -Not -Throw
    }

    It 'rejects a feature branch targeting main' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef feature/unsafe-route
        } | Should -Throw -ExpectedMessage '*Branch flow is not allowed*'
    }

    It 'rejects a developer-specific Windows profile path' {
        $temporaryRepository = Join-Path $TestDrive 'privacy-repository'
        New-Item -ItemType Directory -Path $temporaryRepository | Out-Null
        $null = & git -C $temporaryRepository init
        ('Example: C:' + '\Users\' + 'example-person\project') |
            Set-Content -LiteralPath (Join-Path $temporaryRepository 'README.md')
        $null = & git -C $temporaryRepository add README.md

        {
            & $script:PolicyPath -RepositoryRoot $temporaryRepository
        } | Should -Throw -ExpectedMessage '*Developer-specific Windows profile path*'
    }
}
