[CmdletBinding()]
param(
    [Parameter()]
    [string]$Version,

    [Parameter()]
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'dist\desktop')
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
    $staging = Join-Path $output "winsomnia-$Version-desktop"
    $app = Join-Path $staging 'app'
    if (Test-Path -LiteralPath $staging) {
        Remove-Item -LiteralPath $staging -Recurse -Force
    }
    New-Item -ItemType Directory -Path $app -Force | Out-Null

    $projects = @(
        @{ Path = 'src\Winsomnia.Engine\Winsomnia.Engine.csproj'; Name = 'engine' }
        @{ Path = 'src\Winsomnia.Desktop\Winsomnia.Desktop.csproj'; Name = 'desktop' }
    )
    foreach ($project in $projects) {
        $publish = Join-Path $output $project.Name
        $arguments = @('publish', (Join-Path $PSScriptRoot $project.Path), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', $publish)
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $($project.Path)." }
        Copy-Item -Path (Join-Path $publish '*') -Destination $app -Recurse -Force
    }

    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\EMERGENCY.md') -Destination $app
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\IPC.md') -Destination $app
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\INSTALL.md') -Destination $app
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'VERSION') -Destination $app

    $setupArguments = @('publish', (Join-Path $PSScriptRoot 'src\Winsomnia.Setup\Winsomnia.Setup.csproj'), '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:DebugType=None', '-o', $staging)
    & dotnet @setupArguments
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed for the installer.' }

    $archive = "$staging.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -LiteralPath $staging -DestinationPath $archive
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant()) *$(Split-Path -Leaf $archive)" |
        Set-Content -LiteralPath "$archive.sha256" -Encoding ASCII
    Write-Output "Desktop archive: $archive"
}
catch {
    Write-Error "winsomnia desktop build failed: $($_.Exception.Message)"
    exit 1
}
