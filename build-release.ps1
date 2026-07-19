[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist')
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

    $output = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not (Test-Path -LiteralPath $output -PathType Container)) {
        New-Item -ItemType Directory -Path $output -Force | Out-Null
    }

    $packageName = "winsomnia-$Version-desktop"
    $staging = Join-Path $output $packageName
    $app = Join-Path $staging 'app'
    $archive = Join-Path $output "$packageName.zip"
    $checksum = Join-Path $output "$packageName.sha256"

    foreach ($path in @($staging, $archive, $checksum)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Recurse -Force
        }
    }
    New-Item -ItemType Directory -Path $app -Force | Out-Null

    $projects = @(
        @{ Path = 'src\Winsomnia.Engine\Winsomnia.Engine.csproj'; Name = 'engine' }
        @{ Path = 'src\Winsomnia.Desktop\Winsomnia.Desktop.csproj'; Name = 'desktop' }
    )
    foreach ($project in $projects) {
        $publish = Join-Path $output "publish-$($project.Name)"
        if (Test-Path -LiteralPath $publish) {
            Remove-Item -LiteralPath $publish -Recurse -Force
        }
        $arguments = @(
            'publish', (Join-Path $PSScriptRoot $project.Path)
            '-c', 'Release'
            '-r', 'win-x64'
            '--self-contained', 'true'
            '-p:PublishSingleFile=true'
            '-p:DebugType=None'
            "-p:Version=$Version"
            '-o', $publish
        )
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet publish failed for $($project.Path)."
        }
        Copy-Item -Path (Join-Path $publish '*') -Destination $app -Recurse -Force
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    $packageFiles = @(
        'README.md'
        'CONTRIBUTING.md'
        'RELEASE.md'
        'CHANGELOG.md'
        'LICENSE'
        'VERSION'
        'docs\EMERGENCY.md'
        'docs\ARCHITECTURE.md'
        'docs\IPC.md'
        'docs\INSTALL.md'
    )
    foreach ($relativePath in $packageFiles) {
        $source = Join-Path $PSScriptRoot $relativePath
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Release input is missing: '$source'."
        }
        $destination = Join-Path $staging $relativePath
        $parent = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        Copy-Item -LiteralPath $source -Destination $destination
    }

    $setupArguments = @(
        'publish', (Join-Path $PSScriptRoot 'src\Winsomnia.Setup\Winsomnia.Setup.csproj')
        '-c', 'Release'
        '-r', 'win-x64'
        '--self-contained', 'true'
        '-p:PublishSingleFile=true'
        '-p:DebugType=None'
        "-p:Version=$Version"
        '-o', $staging
    )
    & dotnet @setupArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed for the installer.'
    }

    Compress-Archive -LiteralPath $staging -DestinationPath $archive
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $archive)" |
        Set-Content -LiteralPath $checksum -Encoding ASCII

    Write-Output "Archive: $archive"
    Write-Output "Checksum: $checksum"
}
catch {
    Write-Error "winsomnia release build failed: $($_.Exception.Message)"
    exit 1
}
