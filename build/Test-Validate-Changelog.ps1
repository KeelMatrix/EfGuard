[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$validator = Join-Path $PSScriptRoot "Validate-Changelog.ps1"
$currentCommit = (& git -C $repositoryRoot rev-parse --verify HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw "Could not determine the current repository commit."
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-changelog-contract-" + [Guid]::NewGuid().ToString("N"))
$fixtureRoot = Join-Path $temporaryRoot "fixtures"
$exactCommitRoot = Join-Path $temporaryRoot "exact-commit"
$releaseDate = [DateTime]::UtcNow.ToString("yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)

function Write-Fixture([string] $Path, [string] $Content) {
    $parent = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

function Invoke-Contract([hashtable] $Arguments, [bool] $ShouldPass, [string] $CaseName) {
    $output = & pwsh -NoLogo -NoProfile -File $validator @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    if ($ShouldPass -and $exitCode -ne 0) {
        throw "$CaseName unexpectedly failed with exit code ${exitCode}: $($output -join "`n")"
    }
    if (-not $ShouldPass -and $exitCode -eq 0) {
        throw "$CaseName unexpectedly passed."
    }
    if ($ShouldPass) {
        Write-Output "Changelog contract case passed: $CaseName."
    }
    else {
        $details = (@($output) | ForEach-Object { $_.ToString().Trim() } | Where-Object { $_ }) -join " "
        Write-Output "Changelog contract case failed closed as expected: $CaseName. $details"
    }
}

try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null

    $plannedPath = Join-Path $fixtureRoot "planned.md"
    Write-Fixture $plannedPath @"
# Changelog

## [Unreleased]

## [0.1.0] (Unreleased)

### Added

- Planned release fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $plannedPath
        RepositoryRoot = $repositoryRoot
    } $false "planned/unreleased target is rejected"

    $levelOneNestedPath = Join-Path $fixtureRoot "level-one-nested-under-unreleased.md"
    Write-Fixture $levelOneNestedPath @"
# [Unreleased]

## [0.1.0] - $releaseDate

### Added

- Nested release fixture under a level-one Unreleased section.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $levelOneNestedPath
        RepositoryRoot = $repositoryRoot
    } $false "target under a level-one Unreleased section is rejected"

    $nestedPath = Join-Path $fixtureRoot "nested-under-unreleased.md"
    Write-Fixture $nestedPath @"
# Changelog

## [Unreleased]

### [0.1.0] - $releaseDate

### Added

- Nested release fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $nestedPath
        RepositoryRoot = $repositoryRoot
    } $false "target nested under Unreleased is rejected"

    $plannedHeadingPath = Join-Path $fixtureRoot "planned-heading.md"
    Write-Fixture $plannedHeadingPath @"
# Changelog

## [0.1.0] - $releaseDate (Planned)

### Added

- Planned release fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $plannedHeadingPath
        RepositoryRoot = $repositoryRoot
    } $false "planned release heading is rejected"

    $missingDatePath = Join-Path $fixtureRoot "missing-date.md"
    Write-Fixture $missingDatePath @"
# Changelog

## [Unreleased]

## [0.1.0]

### Added

- Missing release date fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $missingDatePath
        RepositoryRoot = $repositoryRoot
    } $false "release without a date is rejected"

    $futureDatePath = Join-Path $fixtureRoot "future-date.md"
    $futureDate = [DateTime]::UtcNow.AddDays(1).ToString("yyyy-MM-dd", [Globalization.CultureInfo]::InvariantCulture)
    Write-Fixture $futureDatePath @"
# Changelog

## [Unreleased]

## [0.1.0] - $futureDate

### Added

- Future release date fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $futureDatePath
        RepositoryRoot = $repositoryRoot
    } $false "future release date is rejected"

    $finalizedPath = Join-Path $fixtureRoot "finalized.md"
    Write-Fixture $finalizedPath @"
# Changelog

## [Unreleased]

Future changes go here.

## [0.1.0] - $releaseDate

### Added

- Finalized release fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $finalizedPath
        RepositoryRoot = $repositoryRoot
    } $true "finalized target with consistent metadata passes"

    $mismatchPath = Join-Path $fixtureRoot "mismatch.md"
    Write-Fixture $mismatchPath @"
# Changelog

## [Unreleased]

## [0.1.0] - $releaseDate

### Added

- Version mismatch fixture.
"@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.2.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        RepositoryRoot = $repositoryRoot
    } $false "package version mismatch is rejected"

    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedDependencyVersion = "0.2.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        RepositoryRoot = $repositoryRoot
    } $false "dependency version mismatch is rejected"

    Invoke-Contract @{
        ExpectedVersion = "0.2.0"
        ExpectedPackageVersion = "0.2.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        RepositoryRoot = $repositoryRoot
    } $false "changelog and tag version mismatch is rejected"

    $installMismatchPath = Join-Path $fixtureRoot "install-mismatch.md"
    Write-Fixture $installMismatchPath "dotnet tool install --global KeelMatrix.EfGuard --version 0.2.0`n"
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($installMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "install example version mismatch is rejected"

    $equalsInstallMismatchPath = Join-Path $fixtureRoot "equals-install-mismatch.md"
    Write-Fixture $equalsInstallMismatchPath "dotnet tool install --global KeelMatrix.EfGuard --version=0.2.0`n"
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($equalsInstallMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "equals-form install example version mismatch is rejected"

    $multilineInstallMismatchPath = Join-Path $fixtureRoot "multiline-install-mismatch.md"
    Write-Fixture $multilineInstallMismatchPath @'
dotnet tool install --global KeelMatrix.EfGuard
  --version 0.2.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($multilineInstallMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "multiline install example version mismatch is rejected"

    $backtickInstallMismatchPath = Join-Path $fixtureRoot "backtick-install-mismatch.md"
    Write-Fixture $backtickInstallMismatchPath @'
dotnet tool install --global KeelMatrix.EfGuard `
  --version 0.2.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($backtickInstallMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "backtick continuation install example version mismatch is rejected"

    $backslashInstallMismatchPath = Join-Path $fixtureRoot "backslash-install-mismatch.md"
    Write-Fixture $backslashInstallMismatchPath @'
dotnet tool install --global KeelMatrix.EfGuard \
  --version 0.2.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($backslashInstallMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "backslash continuation install example version mismatch is rejected"

    $equalsMultilineInstallMismatchPath = Join-Path $fixtureRoot "equals-multiline-install-mismatch.md"
    Write-Fixture $equalsMultilineInstallMismatchPath @'
dotnet tool install --global KeelMatrix.EfGuard \
  --version=0.2.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $mismatchPath
        InstallExamplePath = @($equalsMultilineInstallMismatchPath)
        RepositoryRoot = $repositoryRoot
    } $false "equals-form continuation install example version mismatch is rejected"

    $multilineInstallConsistentPath = Join-Path $fixtureRoot "multiline-install-consistent.md"
    Write-Fixture $multilineInstallConsistentPath @'
dotnet tool install --global KeelMatrix.EfGuard `
  --version 0.1.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $finalizedPath
        InstallExamplePath = @($multilineInstallConsistentPath)
        RepositoryRoot = $repositoryRoot
    } $true "finalized changelog with a consistent multiline install example passes"

    $equalsMultilineInstallConsistentPath = Join-Path $fixtureRoot "equals-multiline-install-consistent.md"
    Write-Fixture $equalsMultilineInstallConsistentPath @'
dotnet tool install --global KeelMatrix.EfGuard `
  --version=0.1.0
'@
    Invoke-Contract @{
        ExpectedVersion = "0.1.0"
        ExpectedPackageVersion = "0.1.0"
        ExpectedCommit = $currentCommit
        ChangelogPath = $finalizedPath
        InstallExamplePath = @($equalsMultilineInstallConsistentPath)
        RepositoryRoot = $repositoryRoot
    } $true "finalized changelog with an equals-form install example passes"

    $exactCommitChangelog = @"
# Changelog

## [Unreleased]

## [1.2.3] - $releaseDate

### Added

- Exact commit fixture.
"@
    Write-Fixture (Join-Path $exactCommitRoot "CHANGELOG.md") $exactCommitChangelog
    Write-Fixture (Join-Path $exactCommitRoot "README.md") "dotnet tool install --global KeelMatrix.EfGuard --version 1.2.3`n"
    Write-Fixture (Join-Path $exactCommitRoot "Directory.Build.props") "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>`n"
    & git -C $exactCommitRoot init -b main | Out-Null
    & git -C $exactCommitRoot config user.name "KeelMatrix" | Out-Null
    & git -C $exactCommitRoot config user.email "engineering@keelmatrix.dev" | Out-Null
    & git -C $exactCommitRoot add CHANGELOG.md README.md Directory.Build.props | Out-Null
    & git -C $exactCommitRoot commit -m "test exact changelog commit" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not commit the exact-commit fixture." }
    $exactCommit = (& git -C $exactCommitRoot rev-parse --verify HEAD).Trim()
    Invoke-Contract @{
        ExpectedVersion = "1.2.3"
        ExpectedPackageVersion = "1.2.3"
        ExpectedCommit = $exactCommit
        ChangelogPath = "CHANGELOG.md"
        RepositoryRoot = $exactCommitRoot
        RequireChangelogInCommit = $true
    } $true "finalized changelog is bound to its exact commit"

    Write-Fixture (Join-Path $exactCommitRoot "CHANGELOG.md") ($exactCommitChangelog.Replace($releaseDate, "2026-01-01"))
    Invoke-Contract @{
        ExpectedVersion = "1.2.3"
        ExpectedPackageVersion = "1.2.3"
        ExpectedCommit = $exactCommit
        ChangelogPath = "CHANGELOG.md"
        RepositoryRoot = $exactCommitRoot
        RequireChangelogInCommit = $true
    } $false "changed changelog cannot pass for an older commit"

    Write-Fixture (Join-Path $exactCommitRoot "Directory.Build.props") "<Project><PropertyGroup><Version>1.2.4</Version></PropertyGroup></Project>`n"
    Invoke-Contract @{
        ExpectedVersion = "1.2.3"
        ExpectedPackageVersion = "1.2.3"
        ExpectedCommit = $exactCommit
        ChangelogPath = "CHANGELOG.md"
        RepositoryRoot = $exactCommitRoot
    } $false "package metadata mismatch is rejected"
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "Changelog contract coverage passed with synthetic fixtures."
exit 0
