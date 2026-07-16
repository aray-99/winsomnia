[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

try {
    if ([string]::IsNullOrWhiteSpace($Version)) {
        $Version = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Raw).Trim()
    }

    if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
        throw "Version '$Version' is not a supported semantic version."
    }

    if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
        $OutputDirectory = Join-Path $PSScriptRoot 'dist'
    }
    $outputPath = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not (Test-Path -LiteralPath $outputPath -PathType Container)) {
        New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
    }

    $packageName = "win-somnia-$Version"
    $archivePath = Join-Path $outputPath "$packageName.zip"
    $checksumPath = Join-Path $outputPath "$packageName.sha256"
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue

    $releaseFiles = @(
        'README.md'
        'CHANGELOG.md'
        'LICENSE'
        'VERSION'
        'config.example.json'
        'win-somnia.ps1'
        'win-somnia-common.ps1'
        'win-somnia-monitor.ps1'
        'win-somnia-setup.ps1'
    ) | ForEach-Object {
        $path = Join-Path $PSScriptRoot $_
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release input is missing: '$path'."
        }
        $path
    }

    Compress-Archive -LiteralPath $releaseFiles -DestinationPath $archivePath -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $archivePath) |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Output "Archive: $archivePath"
    Write-Output "Checksum: $checksumPath"
}
catch {
    Write-Error "win-somnia release build failed: $($_.Exception.Message)"
    exit 1
}
