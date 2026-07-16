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

    $packageName = "winsomnia-$Version"
    $archivePath = Join-Path $outputPath "$packageName.zip"
    $checksumPath = Join-Path $outputPath "$packageName.sha256"
    Remove-Item -LiteralPath $archivePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $checksumPath -Force -ErrorAction SilentlyContinue

    $releaseFiles = @(
        'README.md'
        'CONTRIBUTING.md'
        'RELEASE.md'
        'CHANGELOG.md'
        'LICENSE'
        'VERSION'
        'config.example.json'
        'docs/EMERGENCY.md'
        'winsomnia.ps1'
        'winsomnia-common.ps1'
        'winsomnia-monitor.ps1'
        'winsomnia-setup.ps1'
    )
    foreach ($relativePath in $releaseFiles) {
        $path = Join-Path $PSScriptRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            throw "Release input is missing: '$path'."
        }
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($relativePath in $releaseFiles) {
            $sourcePath = Join-Path $PSScriptRoot $relativePath
            $entryName = $relativePath.Replace('\', '/')
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $archive,
                $sourcePath,
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $archive.Dispose()
    }
    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    '{0} *{1}' -f $hash.Hash.ToLowerInvariant(), (Split-Path -Leaf $archivePath) |
        Set-Content -LiteralPath $checksumPath -Encoding ASCII

    Write-Output "Archive: $archivePath"
    Write-Output "Checksum: $checksumPath"
}
catch {
    Write-Error "winsomnia release build failed: $($_.Exception.Message)"
    exit 1
}
