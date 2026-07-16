function Get-WinSomniaDefaultConfigPath {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if ([string]::IsNullOrWhiteSpace($localAppData)) {
        throw 'The current user LocalAppData directory could not be resolved.'
    }

    return Join-Path $localAppData 'winsomnia\config.json'
}

function Get-WinSomniaDefaultConfig {
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    return [pscustomobject][ordered]@{
        schemaVersion      = 1
        startTime          = '23:00'
        endTime            = '06:00'
        intervalSeconds    = 5
        killSwitchPath     = 'C:\temp\win-somnia-unlock.txt'
        logPath            = (Join-Path $localAppData 'winsomnia\winsomnia.log')
    }
}

function ConvertTo-WinSomniaClockTime {
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

function Assert-WinSomniaConfig {
    param(
        [Parameter(Mandatory)]
        [psobject]$Config
    )

    foreach ($propertyName in @(
            'schemaVersion',
            'startTime',
            'endTime',
            'intervalSeconds',
            'killSwitchPath',
            'logPath')) {
        if ($null -eq $Config.PSObject.Properties[$propertyName]) {
            throw "Configuration property '$propertyName' is missing."
        }
    }

    if ([int]$Config.schemaVersion -ne 1) {
        throw "Unsupported configuration schema version '$($Config.schemaVersion)'."
    }

    $restrictionStart = ConvertTo-WinSomniaClockTime -Value ([string]$Config.startTime) -ParameterName 'StartTime'
    $restrictionEnd = ConvertTo-WinSomniaClockTime -Value ([string]$Config.endTime) -ParameterName 'EndTime'
    if ($restrictionStart -eq $restrictionEnd) {
        throw '-StartTime and -EndTime must differ.'
    }

    $interval = [int]$Config.intervalSeconds
    if ($interval -lt 1 -or $interval -gt 3600) {
        throw '-IntervalSeconds must be between 1 and 3600.'
    }

    if ([string]::IsNullOrWhiteSpace([string]$Config.killSwitchPath)) {
        throw 'killSwitchPath cannot be empty.'
    }
    if ([string]::IsNullOrWhiteSpace([string]$Config.logPath)) {
        throw 'logPath cannot be empty.'
    }
    if (-not [IO.Path]::IsPathRooted([string]$Config.killSwitchPath)) {
        throw 'killSwitchPath must be an absolute path.'
    }
    if (-not [IO.Path]::IsPathRooted([string]$Config.logPath)) {
        throw 'logPath must be an absolute path.'
    }
}

function Read-WinSomniaConfig {
    param(
        [Parameter()]
        [string]$Path = (Get-WinSomniaDefaultConfigPath),

        [Parameter()]
        [switch]$AllowMissing
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        if ($AllowMissing) {
            return Get-WinSomniaDefaultConfig
        }

        throw "Configuration file was not found: '$Path'."
    }

    try {
        $config = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Configuration file '$Path' is invalid: $($_.Exception.Message)"
    }

    Assert-WinSomniaConfig -Config $config
    return $config
}

function Write-WinSomniaConfig {
    param(
        [Parameter(Mandatory)]
        [psobject]$Config,

        [Parameter()]
        [string]$Path = (Get-WinSomniaDefaultConfigPath)
    )

    Assert-WinSomniaConfig -Config $Config
    $parent = Split-Path -Parent $Path
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force -ErrorAction Stop | Out-Null
    }

    $Config | ConvertTo-Json | Set-Content -LiteralPath $Path -Encoding UTF8 -ErrorAction Stop
    return (Resolve-Path -LiteralPath $Path).Path
}
