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
    [int]$TestDurationSeconds = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

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
    return Test-Path -LiteralPath $KillSwitchPath -PathType Leaf
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
        Write-Output "Kill switch found at '$KillSwitchPath'. Exiting without locking."
        exit 0
    }

    $stopAtUtc = if ($DryRun) {
        [DateTime]::UtcNow.AddSeconds($TestDurationSeconds)
    }
    else {
        $null
    }

    while ($true) {
        if (Test-KillSwitch) {
            Write-Output "Kill switch found at '$KillSwitchPath'. Exiting."
            break
        }

        if ($DryRun -and [DateTime]::UtcNow -ge $stopAtUtc) {
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

                & "$env:SystemRoot\System32\rundll32.exe" user32.dll,LockWorkStation
                if ($LASTEXITCODE -ne 0) {
                    throw "LockWorkStation failed with exit code $LASTEXITCODE."
                }
            }
        }

        if (-not (Wait-Safely -Seconds $IntervalSeconds)) {
            Write-Output "Kill switch found at '$KillSwitchPath'. Exiting."
            break
        }
    }
}
catch {
    Write-Error "win-somnia monitor stopped: $($_.Exception.Message)"
    exit 1
}
