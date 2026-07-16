BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    $script:CommonPath = Join-Path $script:RepoRoot 'win-somnia-common.ps1'
    $script:MonitorPath = Join-Path $script:RepoRoot 'win-somnia-monitor.ps1'
    $script:SetupPath = Join-Path $script:RepoRoot 'win-somnia-setup.ps1'
    $script:CliPath = Join-Path $script:RepoRoot 'win-somnia.ps1'
    $script:BuildPath = Join-Path $script:RepoRoot 'build-release.ps1'
    $script:WindowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    . $script:CommonPath
}

Describe 'win-somnia configuration' {
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

Describe 'win-somnia monitor safety' {
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

Describe 'win-somnia setup safety' {
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

Describe 'win-somnia integrated CLI' {
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

Describe 'win-somnia release package' {
    It 'builds a ZIP with a matching SHA-256 checksum' {
        $outputDirectory = Join-Path $TestDrive 'dist'
        $version = '0.1.0-test.1'
        $null = & $script:BuildPath -Version $version -OutputDirectory $outputDirectory

        $archivePath = Join-Path $outputDirectory "win-somnia-$version.zip"
        $checksumPath = Join-Path $outputDirectory "win-somnia-$version.sha256"
        Test-Path -LiteralPath $archivePath | Should -BeTrue
        Test-Path -LiteralPath $checksumPath | Should -BeTrue

        $expectedHash = (Get-Content -LiteralPath $checksumPath -Raw).Split(' ')[0]
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualHash | Should -Be $expectedHash

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object FullName)
            $entryNames | Should -Contain 'win-somnia.ps1'
            $entryNames | Should -Contain 'README.md'
            $entryNames | Should -Contain 'LICENSE'
            $entryNames | Should -Contain 'VERSION'
        }
        finally {
            $archive.Dispose()
        }
    }
}
