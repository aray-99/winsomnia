BeforeAll {
    $script:RepoRoot = Split-Path -Parent $PSScriptRoot
    $script:BuildPath = Join-Path $script:RepoRoot 'build-release.ps1'
    $script:PolicyPath = Join-Path $script:RepoRoot 'scripts\Test-RepositoryPolicy.ps1'
}

Describe 'winsomnia release package' {
    It 'builds the single desktop ZIP with a matching SHA-256 checksum' {
        $outputDirectory = Join-Path $TestDrive 'dist'
        $version = '0.3.0-test.1'
        $null = & $script:BuildPath -Version $version -OutputDirectory $outputDirectory

        $packageName = "winsomnia-$version-desktop"
        $archivePath = Join-Path $outputDirectory "$packageName.zip"
        $checksumPath = Join-Path $outputDirectory "$packageName.sha256"
        Test-Path -LiteralPath $archivePath | Should -BeTrue
        Test-Path -LiteralPath $checksumPath | Should -BeTrue
        @(Get-ChildItem -LiteralPath $outputDirectory -File -Filter '*.zip').Count | Should -Be 1
        @(Get-ChildItem -LiteralPath $outputDirectory -File -Filter '*.sha256').Count | Should -Be 1

        $expectedHash = (Get-Content -LiteralPath $checksumPath -Raw).Split(' ')[0]
        $actualHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $actualHash | Should -Be $expectedHash

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
        try {
            $entryNames = @($archive.Entries | ForEach-Object { $_.FullName -replace '\\', '/' })
            $prefix = "$packageName/"
            $entryNames | Should -Contain "${prefix}Winsomnia.Setup.exe"
            $entryNames | Should -Contain "${prefix}app/Winsomnia.Engine.exe"
            $entryNames | Should -Contain "${prefix}app/Winsomnia.Desktop.exe"
            $entryNames | Should -Contain "${prefix}README.md"
            $entryNames | Should -Contain "${prefix}CONTRIBUTING.md"
            $entryNames | Should -Contain "${prefix}RELEASE.md"
            $entryNames | Should -Contain "${prefix}docs/EMERGENCY.md"
            $entryNames | Should -Contain "${prefix}docs/INSTALL.md"
            $entryNames | Should -Contain "${prefix}LICENSE"
            $entryNames | Should -Contain "${prefix}VERSION"
            $entryNames | Should -Not -Contain "${prefix}winsomnia.ps1"
            $entryNames | Should -Not -Contain "${prefix}winsomnia-monitor.ps1"
            $entryNames | Should -Not -Contain "${prefix}winsomnia-setup.ps1"

            $readmeEntry = $archive.Entries | Where-Object {
                ($_.FullName -replace '\\', '/') -eq "${prefix}README.md"
            } | Select-Object -First 1
            $reader = [IO.StreamReader]::new($readmeEntry.Open())
            try {
                $readme = $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
            $relativeLinks = [regex]::Matches($readme, '\[[^\]]+\]\((?!https?://|#)([^)]+)\)')
            foreach ($link in $relativeLinks) {
                $entryNames | Should -Contain ($prefix + $link.Groups[1].Value.Replace('\', '/'))
            }
        }
        finally {
            $archive.Dispose()
        }
    }
}

Describe 'winsomnia repository policy' {
    It 'accepts a release-blocker fix targeting a release branch' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef release/0.3.0 `
                -HeadRef fix/release-safety-guardrails
        } | Should -Not -Throw
    }

    It 'rejects a feature branch targeting main' {
        {
            & $script:PolicyPath `
                -EventName pull_request `
                -BaseRef main `
                -HeadRef feature/unsafe-route
        } | Should -Throw -ExpectedMessage '*Branch flow is not allowed*'
    }

    It 'rejects a developer-specific Windows profile path' {
        $temporaryRepository = Join-Path $TestDrive 'privacy-repository'
        New-Item -ItemType Directory -Path $temporaryRepository | Out-Null
        $null = & git -C $temporaryRepository init
        ('Example: C:' + '\Users\' + 'example-person\project') |
            Set-Content -LiteralPath (Join-Path $temporaryRepository 'README.md')
        $null = & git -C $temporaryRepository add README.md

        {
            & $script:PolicyPath -RepositoryRoot $temporaryRepository
        } | Should -Throw -ExpectedMessage '*Developer-specific Windows profile path*'
    }
}
