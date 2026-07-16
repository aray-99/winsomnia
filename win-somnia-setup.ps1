[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'Medium')]
param(
    [Parameter()]
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install',

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$TaskName = 'win-somnia',

    [Parameter()]
    [string]$MonitorPath,

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
    [string]$ConfigPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'win-somnia-common.ps1')

function ConvertTo-QuotedArgument {
    param(
        [Parameter(Mandatory)]
        [string]$Value
    )

    if ($Value.Contains('"')) {
        throw 'Argument values containing a double quote are not supported.'
    }

    return '"{0}"' -f $Value
}

try {
    Import-Module ScheduledTasks -ErrorAction Stop
    $existingTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

    if ($Action -eq 'Uninstall') {
        if ($null -eq $existingTask) {
            Write-Output "Scheduled task '$TaskName' is not installed."
            exit 0
        }

        if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction Stop
            Write-Output "Scheduled task '$TaskName' was removed."
        }

        exit 0
    }

    if ([string]::IsNullOrWhiteSpace($MonitorPath)) {
        $MonitorPath = Join-Path $PSScriptRoot 'win-somnia-monitor.ps1'
    }

    if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
        $ConfigPath = Get-WinSomniaDefaultConfigPath
    }
    elseif (-not [IO.Path]::IsPathRooted($ConfigPath)) {
        $ConfigPath = [IO.Path]::GetFullPath($ConfigPath)
    }

    $config = Read-WinSomniaConfig -Path $ConfigPath -AllowMissing
    if ($PSBoundParameters.ContainsKey('StartTime')) {
        $config.startTime = $StartTime
    }
    if ($PSBoundParameters.ContainsKey('EndTime')) {
        $config.endTime = $EndTime
    }
    if ($PSBoundParameters.ContainsKey('IntervalSeconds')) {
        $config.intervalSeconds = [int]$IntervalSeconds
    }
    if ($PSBoundParameters.ContainsKey('KillSwitchPath')) {
        $config.killSwitchPath = $KillSwitchPath
    }
    if ($PSBoundParameters.ContainsKey('LogPath')) {
        $config.logPath = $LogPath
    }
    Assert-WinSomniaConfig -Config $config

    $resolvedMonitorPath = (Resolve-Path -LiteralPath $MonitorPath -ErrorAction Stop).Path
    if ([IO.Path]::GetExtension($resolvedMonitorPath) -ne '.ps1') {
        throw "MonitorPath must point to a .ps1 file: '$resolvedMonitorPath'."
    }

    $powerShellPath = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $powerShellPath -PathType Leaf)) {
        throw "Windows PowerShell was not found at '$powerShellPath'."
    }

    $taskArguments = @(
        '-NoLogo'
        '-NoProfile'
        '-NonInteractive'
        '-WindowStyle Hidden'
        '-ExecutionPolicy Bypass'
        '-File', (ConvertTo-QuotedArgument $resolvedMonitorPath)
        '-EnableLock'
        '-ConfigPath', (ConvertTo-QuotedArgument $ConfigPath)
    ) -join ' '

    $currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $taskAction = New-ScheduledTaskAction -Execute $powerShellPath -Argument $taskArguments
    $taskTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
    $taskPrincipal = New-ScheduledTaskPrincipal `
        -UserId $currentUser `
        -LogonType Interactive `
        -RunLevel Limited
    $taskSettings = New-ScheduledTaskSettingsSet `
        -AllowStartIfOnBatteries `
        -DontStopIfGoingOnBatteries `
        -ExecutionTimeLimit ([TimeSpan]::Zero) `
        -MultipleInstances IgnoreNew `
        -RestartCount 3 `
        -RestartInterval (New-TimeSpan -Minutes 1)

    $task = New-ScheduledTask `
        -Action $taskAction `
        -Trigger $taskTrigger `
        -Principal $taskPrincipal `
        -Settings $taskSettings `
        -Description 'Repeatedly locks the workstation during configured restricted hours.'

    if ($PSCmdlet.ShouldProcess($ConfigPath, 'Write win-somnia configuration')) {
        $ConfigPath = Write-WinSomniaConfig -Config $config -Path $ConfigPath
    }

    if ($PSCmdlet.ShouldProcess($TaskName, 'Register logon-triggered hidden monitor')) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force -ErrorAction Stop | Out-Null
        Write-Output "Scheduled task '$TaskName' was installed for $currentUser."
        Write-Output "Monitor: $resolvedMonitorPath"
        Write-Output "Configuration: $ConfigPath"
        Write-Output "Restricted hours: $($config.startTime)-$($config.endTime); interval: $($config.intervalSeconds) seconds"
        Write-Output "Kill switch: $($config.killSwitchPath)"
    }
}
catch {
    Write-Error "win-somnia setup failed during '$Action': $($_.Exception.Message)"
    exit 1
}
