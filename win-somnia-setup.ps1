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
    [string]$StartTime = '23:00',

    [Parameter()]
    [string]$EndTime = '06:00',

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 5,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$KillSwitchPath = 'C:\temp\win-somnia-unlock.txt'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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

function ConvertTo-ClockTime {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$ParameterName
    )

    $parsed = [TimeSpan]::Zero
    if (-not [TimeSpan]::TryParseExact(
            $Value,
            'hh\:mm',
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$parsed) -or $parsed.TotalHours -ge 24) {
        throw "-$ParameterName must use 24-hour HH:mm format between 00:00 and 23:59."
    }

    return $parsed
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

    $resolvedMonitorPath = (Resolve-Path -LiteralPath $MonitorPath -ErrorAction Stop).Path
    if ([IO.Path]::GetExtension($resolvedMonitorPath) -ne '.ps1') {
        throw "MonitorPath must point to a .ps1 file: '$resolvedMonitorPath'."
    }

    $restrictionStart = ConvertTo-ClockTime -Value $StartTime -ParameterName 'StartTime'
    $restrictionEnd = ConvertTo-ClockTime -Value $EndTime -ParameterName 'EndTime'
    if ($restrictionStart -eq $restrictionEnd) {
        throw '-StartTime and -EndTime must differ.'
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
        '-StartTime', (ConvertTo-QuotedArgument $StartTime)
        '-EndTime', (ConvertTo-QuotedArgument $EndTime)
        '-IntervalSeconds', $IntervalSeconds
        '-KillSwitchPath', (ConvertTo-QuotedArgument $KillSwitchPath)
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

    if ($PSCmdlet.ShouldProcess($TaskName, 'Register logon-triggered hidden monitor')) {
        Register-ScheduledTask -TaskName $TaskName -InputObject $task -Force -ErrorAction Stop | Out-Null
        Write-Output "Scheduled task '$TaskName' was installed for $currentUser."
        Write-Output "Monitor: $resolvedMonitorPath"
        Write-Output "Restricted hours: $StartTime-$EndTime; interval: $IntervalSeconds seconds"
        Write-Output "Kill switch: $KillSwitchPath"
    }
}
catch {
    Write-Error "win-somnia setup failed during '$Action': $($_.Exception.Message)"
    exit 1
}
