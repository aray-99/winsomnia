[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Position = 0)]
    [ValidateSet('setup', 'status', 'pause', 'resume', 'test', 'logs', 'uninstall')]
    [string]$Command,

    [Parameter()]
    [string]$StartTime,

    [Parameter()]
    [string]$EndTime,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [Nullable[int]]$IntervalSeconds,

    [Parameter()]
    [string]$KillSwitchPath,

    [Parameter()]
    [string]$LogPath,

    [Parameter()]
    [string]$ConfigPath,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$TestDurationSeconds = 10,

    [Parameter()]
    [ValidateRange(1, 1000)]
    [int]$LogLines = 50
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$taskName = 'winsomnia'
$scriptParameters = @{}
foreach ($parameterName in $PSBoundParameters.Keys) {
    $scriptParameters[$parameterName] = $PSBoundParameters[$parameterName]
}

. (Join-Path $PSScriptRoot 'winsomnia-common.ps1')

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-WinSomniaDefaultConfigPath
}

function Get-CurrentConfig {
    return Read-WinSomniaConfig -Path $ConfigPath -AllowMissing
}

function Invoke-SetupCommand {
    param(
        [Parameter()]
        [switch]$Interactive
    )

    $arguments = @{
        Action = 'Install'
        ConfigPath = $ConfigPath
    }

    if ($Interactive) {
        $current = Get-CurrentConfig
        $newStart = Read-Host "Start time [$($current.startTime)]"
        $newEnd = Read-Host "End time [$($current.endTime)]"
        $newInterval = Read-Host "Lock interval in seconds [$($current.intervalSeconds)]"

        if (-not [string]::IsNullOrWhiteSpace($newStart)) {
            $arguments.StartTime = $newStart
        }
        if (-not [string]::IsNullOrWhiteSpace($newEnd)) {
            $arguments.EndTime = $newEnd
        }
        if (-not [string]::IsNullOrWhiteSpace($newInterval)) {
            $parsedInterval = 0
            if (-not [int]::TryParse($newInterval, [ref]$parsedInterval)) {
                throw 'The interval must be a whole number.'
            }
            $arguments.IntervalSeconds = $parsedInterval
        }
    }
    else {
        if ($scriptParameters.ContainsKey('StartTime')) {
            $arguments.StartTime = $StartTime
        }
        if ($scriptParameters.ContainsKey('EndTime')) {
            $arguments.EndTime = $EndTime
        }
        if ($scriptParameters.ContainsKey('IntervalSeconds')) {
            $arguments.IntervalSeconds = $IntervalSeconds
        }
        if ($scriptParameters.ContainsKey('KillSwitchPath')) {
            $arguments.KillSwitchPath = $KillSwitchPath
        }
        if ($scriptParameters.ContainsKey('LogPath')) {
            $arguments.LogPath = $LogPath
        }
    }

    if ($WhatIfPreference) {
        $arguments.WhatIf = $true
    }

    & (Join-Path $PSScriptRoot 'winsomnia-setup.ps1') @arguments
}

function Show-Status {
    $config = Get-CurrentConfig
    $killSwitchPresent = Test-Path -LiteralPath $config.killSwitchPath
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue

    Write-Output 'winsomnia status'
    Write-Output "  Configuration : $ConfigPath"
    Write-Output "  Restricted    : $($config.startTime)-$($config.endTime)"
    Write-Output "  Lock interval : $($config.intervalSeconds) seconds"
    Write-Output "  Kill switch   : $($config.killSwitchPath)"
    Write-Output "  Log           : $($config.logPath)"
    Write-Output "  Paused        : $killSwitchPresent"

    if ($null -eq $task) {
        Write-Output '  Task          : Not installed'
    }
    else {
        $taskInfo = Get-ScheduledTaskInfo -TaskName $taskName -ErrorAction SilentlyContinue
        Write-Output "  Task          : $($task.State)"
        if ($null -ne $taskInfo) {
            Write-Output "  Last result   : $($taskInfo.LastTaskResult)"
            Write-Output "  Last run      : $($taskInfo.LastRunTime)"
        }
    }
}

function Invoke-Pause {
    $config = Get-CurrentConfig
    $killSwitchDirectory = Split-Path -Parent $config.killSwitchPath
    if (-not (Test-Path -LiteralPath $killSwitchDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $killSwitchDirectory -Force | Out-Null
    }
    if (-not (Test-Path -LiteralPath $config.killSwitchPath)) {
        New-Item -ItemType File -Path $config.killSwitchPath -Force | Out-Null
    }
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Write-Output "winsomnia is paused. Kill switch: $($config.killSwitchPath)"
}

function Invoke-Resume {
    $config = Get-CurrentConfig
    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -eq $task) {
        throw "Scheduled task '$taskName' is not installed. Run setup first."
    }

    Remove-Item -LiteralPath $config.killSwitchPath -Force -ErrorAction SilentlyContinue
    Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    Write-Output "winsomnia is active for $($config.startTime)-$($config.endTime)."
}

function Invoke-SafeTest {
    $now = Get-Date
    $testStart = $now.AddMinutes(-1).ToString('HH:mm')
    $testEnd = $now.AddMinutes(1).ToString('HH:mm')
    $unusedKillSwitch = Join-Path ([IO.Path]::GetTempPath()) "winsomnia-dryrun-$([Guid]::NewGuid().ToString('N')).txt"

    & (Join-Path $PSScriptRoot 'winsomnia-monitor.ps1') `
        -DryRun `
        -TestDurationSeconds $TestDurationSeconds `
        -StartTime $testStart `
        -EndTime $testEnd `
        -IntervalSeconds 1 `
        -KillSwitchPath $unusedKillSwitch `
        -ConfigPath $ConfigPath
}

function Show-Log {
    $config = Get-CurrentConfig
    if (-not (Test-Path -LiteralPath $config.logPath -PathType Leaf)) {
        Write-Output "No log has been written yet: $($config.logPath)"
        return
    }

    Get-Content -LiteralPath $config.logPath -Tail $LogLines
}

function Invoke-UninstallCommand {
    $arguments = @{
        Action = 'Uninstall'
        ConfigPath = $ConfigPath
    }
    if ($WhatIfPreference) {
        $arguments.WhatIf = $true
    }
    & (Join-Path $PSScriptRoot 'winsomnia-setup.ps1') @arguments
}

function Show-InteractiveMenu {
    while ($true) {
        Write-Output ''
        Show-Status
        Write-Output ''
        Write-Output '1. Configure or install'
        Write-Output '2. Pause'
        Write-Output '3. Resume'
        Write-Output '4. Run safe test'
        Write-Output '5. Show logs'
        Write-Output '6. Uninstall'
        Write-Output '0. Exit'
        $selection = Read-Host 'Select'

        switch ($selection) {
            '1' { Invoke-SetupCommand -Interactive }
            '2' { Invoke-Pause }
            '3' {
                $confirmation = Read-Host 'Resume real locking with the current schedule? [y/N]'
                if ($confirmation -match '^(y|yes)$') {
                    Invoke-Resume
                }
            }
            '4' { Invoke-SafeTest }
            '5' { Show-Log }
            '6' {
                $confirmation = Read-Host 'Uninstall the scheduled task? [y/N]'
                if ($confirmation -match '^(y|yes)$') {
                    Invoke-UninstallCommand
                }
            }
            '0' { return }
            default { Write-Warning 'Unknown selection.' }
        }
    }
}

try {
    if ([string]::IsNullOrWhiteSpace($Command)) {
        Show-InteractiveMenu
        exit 0
    }

    switch ($Command) {
        'setup' { Invoke-SetupCommand }
        'status' { Show-Status }
        'pause' { Invoke-Pause }
        'resume' { Invoke-Resume }
        'test' { Invoke-SafeTest }
        'logs' { Show-Log }
        'uninstall' { Invoke-UninstallCommand }
    }
}
catch {
    Write-Error "winsomnia command failed: $($_.Exception.Message)"
    exit 1
}
