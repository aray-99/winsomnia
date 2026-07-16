[CmdletBinding()]
param(
    [Parameter()]
    [string]$StartTime = '23:00',

    [Parameter()]
    [string]$EndTime = '06:00',

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$IntervalSeconds = 5,

    [Parameter()]
    [ValidateNotNullOrEmpty()]
    [string]$KillSwitchPath = 'C:\temp\win-somnia-unlock.txt',

    # Real locking is opt-in. The setup script supplies this switch to the task.
    [Parameter()]
    [switch]$EnableLock,

    # DryRun never locks and exits automatically (60 seconds by default).
    [Parameter()]
    [switch]$DryRun,

    [Parameter()]
    [ValidateRange(1, 3600)]
    [int]$TestDurationSeconds = 60,

    [Parameter()]
    [string]$ConfigPath,

    [Parameter()]
    [string]$LogPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$explicitParameters = @{}
foreach ($parameterName in $PSBoundParameters.Keys) {
    $explicitParameters[$parameterName] = $true
}

. (Join-Path $PSScriptRoot 'win-somnia-common.ps1')

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = Get-WinSomniaDefaultConfigPath
}

$config = Read-WinSomniaConfig -Path $ConfigPath -AllowMissing
if (-not $explicitParameters.ContainsKey('StartTime')) {
    $StartTime = [string]$config.startTime
}
if (-not $explicitParameters.ContainsKey('EndTime')) {
    $EndTime = [string]$config.endTime
}
if (-not $explicitParameters.ContainsKey('IntervalSeconds')) {
    $IntervalSeconds = [int]$config.intervalSeconds
}
if (-not $explicitParameters.ContainsKey('KillSwitchPath')) {
    $KillSwitchPath = [string]$config.killSwitchPath
}
if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = [string]$config.logPath
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
            [ref]$parsed)) {
        throw "-$ParameterName must use 24-hour HH:mm format (for example, 23:00)."
    }

    if ($parsed.TotalHours -ge 24) {
        throw "-$ParameterName must be between 00:00 and 23:59."
    }

    return $parsed
}

function Test-IsRestrictedTime {
    param(
        [Parameter(Mandatory)]
        [TimeSpan]$CurrentTime,

        [Parameter(Mandatory)]
        [TimeSpan]$RestrictionStart,

        [Parameter(Mandatory)]
        [TimeSpan]$RestrictionEnd
    )

    if ($RestrictionStart -lt $RestrictionEnd) {
        return $CurrentTime -ge $RestrictionStart -and $CurrentTime -lt $RestrictionEnd
    }

    # A range such as 23:00-06:00 crosses midnight.
    return $CurrentTime -ge $RestrictionStart -or $CurrentTime -lt $RestrictionEnd
}

function Test-KillSwitch {
    # Any filesystem object at the path is treated as a stop request.
    return Test-Path -LiteralPath $KillSwitchPath
}

function Wait-Safely {
    param(
        [Parameter(Mandatory)]
        [int]$Seconds
    )

    # Poll once per second so a long interval never delays the kill switch much.
    for ($elapsed = 0; $elapsed -lt $Seconds; $elapsed++) {
        if (Test-KillSwitch) {
            return $false
        }

        Start-Sleep -Seconds 1
    }

    return $true
}

function Write-MonitorLog {
    param(
        [Parameter(Mandatory)]
        [string]$Message,

        [Parameter()]
        [ValidateSet('INFO', 'WARN', 'ERROR')]
        [string]$Level = 'INFO'
    )

    try {
        $logDirectory = Split-Path -Parent $LogPath
        if (-not (Test-Path -LiteralPath $logDirectory -PathType Container)) {
            New-Item -ItemType Directory -Path $logDirectory -Force -ErrorAction Stop | Out-Null
        }

        if ((Test-Path -LiteralPath $LogPath -PathType Leaf) -and
            (Get-Item -LiteralPath $LogPath).Length -gt 1MB) {
            Move-Item -LiteralPath $LogPath -Destination "$LogPath.previous" -Force
        }

        $line = '{0} [{1}] {2}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Level, $Message
        Add-Content -LiteralPath $LogPath -Value $line -Encoding UTF8 -ErrorAction Stop
    }
    catch {
        Write-Warning "Monitor log could not be written: $($_.Exception.Message)"
    }
}

function Invoke-WorkstationLock {
    $rundll32Path = Join-Path $env:SystemRoot 'System32\rundll32.exe'
    $process = Start-Process `
        -FilePath $rundll32Path `
        -ArgumentList 'user32.dll,LockWorkStation' `
        -WindowStyle Hidden `
        -Wait `
        -PassThru `
        -ErrorAction Stop

    if ($process.ExitCode -ne 0) {
        throw "LockWorkStation failed with exit code $($process.ExitCode)."
    }
}

try {
    $restrictionStart = ConvertTo-ClockTime -Value $StartTime -ParameterName 'StartTime'
    $restrictionEnd = ConvertTo-ClockTime -Value $EndTime -ParameterName 'EndTime'

    if ($restrictionStart -eq $restrictionEnd) {
        throw '-StartTime and -EndTime must differ.'
    }

    if ($DryRun -and $EnableLock) {
        throw '-DryRun and -EnableLock cannot be used together.'
    }

    if (Test-KillSwitch) {
        Write-MonitorLog -Message "Kill switch found at '$KillSwitchPath'; monitor did not start."
        Write-Output "Kill switch found at '$KillSwitchPath'. Exiting without locking."
        exit 0
    }

    Write-MonitorLog -Message "Monitor started; restricted hours $StartTime-$EndTime, interval $IntervalSeconds seconds, real lock enabled: $([bool]$EnableLock)."

    $stopAtUtc = if ($DryRun) {
        [DateTime]::UtcNow.AddSeconds($TestDurationSeconds)
    }
    else {
        $null
    }

    while ($true) {
        if (Test-KillSwitch) {
            Write-MonitorLog -Message "Kill switch found at '$KillSwitchPath'; monitor is stopping."
            Write-Output "Kill switch found at '$KillSwitchPath'. Exiting."
            break
        }

        if ($DryRun -and [DateTime]::UtcNow -ge $stopAtUtc) {
            Write-MonitorLog -Message "Dry run completed after $TestDurationSeconds seconds."
            Write-Output "Dry run completed after $TestDurationSeconds seconds."
            break
        }

        $now = Get-Date
        $isRestricted = Test-IsRestrictedTime `
            -CurrentTime $now.TimeOfDay `
            -RestrictionStart $restrictionStart `
            -RestrictionEnd $restrictionEnd

        if ($isRestricted) {
            if ($DryRun -or -not $EnableLock) {
                Write-Output "[$($now.ToString('yyyy-MM-dd HH:mm:ss'))] Lock would be requested."
            }
            else {
                # Check immediately before the irreversible user-visible action.
                if (Test-KillSwitch) {
                    Write-Output "Kill switch found at '$KillSwitchPath'. Exiting."
                    break
                }

                Write-MonitorLog -Message 'Requesting workstation lock.'
                Invoke-WorkstationLock
            }
        }

        if (-not (Wait-Safely -Seconds $IntervalSeconds)) {
            Write-Output "Kill switch found at '$KillSwitchPath'. Exiting."
            break
        }
    }
}
catch {
    Write-MonitorLog -Message "Monitor stopped with an error: $($_.Exception.Message)" -Level ERROR
    Write-Error "win-somnia monitor stopped: $($_.Exception.Message)"
    exit 1
}
