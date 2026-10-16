[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [string] $ExpectedVersion = "0.1.1",
    [string] $ExpectedCommit
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Set-NuspecProjectUrl([string] $PackagePath, [string] $ProjectUrl) {
    $archive = [IO.Compression.ZipFile]::Open($PackagePath, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.GetEntry("KeelMatrix.EfGuard.nuspec")
        if ($null -eq $entry) { throw "Package nuspec is missing." }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { [xml] $nuspec = $reader.ReadToEnd() }
        finally { $reader.Dispose() }

        $projectUrlNode = $nuspec.SelectSingleNode("//*[local-name()='projectUrl']")
        if ($null -eq $projectUrlNode) { throw "Package project URL is missing." }
        $projectUrlNode.InnerText = $ProjectUrl
        $entry.Delete()

        $updatedEntry = $archive.CreateEntry("KeelMatrix.EfGuard.nuspec")
        $stream = $updatedEntry.Open()
        try {
            $settings = [System.Xml.XmlWriterSettings]::new()
            $settings.Encoding = [System.Text.UTF8Encoding]::new($false)
            $writer = [System.Xml.XmlWriter]::Create($stream, $settings)
            try { $nuspec.Save($writer) }
            finally { $writer.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }
}

$packageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
& (Join-Path $PSScriptRoot "Test-Validate-WebsiteCatalog.ps1") -RepositoryRoot $repositoryRoot
if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "Could not determine the expected repository commit."
    }
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-package-negative-" + [Guid]::NewGuid().ToString("N"))
try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

    $negativePackage = Join-Path $temporaryRoot "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg") -Destination $negativePackage
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg") -Destination $temporaryRoot
    $archive = [IO.Compression.ZipFile]::Open($negativePackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $entry = $archive.CreateEntry("docs/benign-looking-note.txt")
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.WriteLine("Package validation negative case.") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }

    $validator = Join-Path $PSScriptRoot "Validate-Package.ps1"
    $failedAsExpected = $false
    try {
        & $validator -PackageDirectory $temporaryRoot -ExpectedVersion $ExpectedVersion -ExpectedCommit $ExpectedCommit
    }
    catch {
        $failedAsExpected = $_.Exception.Message -match "archive entry allowlist"
        if (-not $failedAsExpected) {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "The package validator accepted an unexpected benign-looking archive entry."
    }

    Write-Output "Negative package validation passed: an unexpected benign-looking archive entry was rejected."

    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $negativePackage = Join-Path $temporaryRoot "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg") -Destination $negativePackage
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg") -Destination $temporaryRoot
    $archive = [IO.Compression.ZipFile]::Open($negativePackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry("README.md").Delete()
    }
    finally { $archive.Dispose() }

    $failedAsExpected = $false
    try { & $validator -PackageDirectory $temporaryRoot -ExpectedVersion $ExpectedVersion -ExpectedCommit $ExpectedCommit }
    catch {
        $failedAsExpected = $_.Exception.Message -match "archive entry allowlist"
        if (-not $failedAsExpected) {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "The package validator accepted a package with a missing README."
    }

    Write-Output "Negative package validation passed: a missing README was rejected."

    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $negativePackage = Join-Path $temporaryRoot "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg") -Destination $negativePackage
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg") -Destination $temporaryRoot
    $archive = [IO.Compression.ZipFile]::Open($negativePackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry("README.md").Delete()
        $entry = $archive.CreateEntry("README.md")
        $rootReadmeBytes = [IO.File]::ReadAllBytes((Join-Path $PSScriptRoot "..\README.md"))
        $stream = $entry.Open()
        try { $stream.Write($rootReadmeBytes, 0, $rootReadmeBytes.Length) }
        finally { $stream.Dispose() }
    }
    finally { $archive.Dispose() }

    $failedAsExpected = $false
    try { & $validator -PackageDirectory $temporaryRoot -ExpectedVersion $ExpectedVersion -ExpectedCommit $ExpectedCommit }
    catch {
        $failedAsExpected = $_.Exception.Message -match "repository-root README"
        if (-not $failedAsExpected) {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "The package validator accepted the repository-root README as the package README."
    }

    Write-Output "Negative package validation passed: the repository-root README was rejected as the package README."

    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $negativePackage = Join-Path $temporaryRoot "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg") -Destination $negativePackage
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg") -Destination $temporaryRoot
    $archive = [IO.Compression.ZipFile]::Open($negativePackage, [IO.Compression.ZipArchiveMode]::Update)
    try {
        $archive.GetEntry("README.md").Delete()
        $entry = $archive.CreateEntry("README.md")
        $writer = [IO.StreamWriter]::new($entry.Open())
        try { $writer.WriteLine("[Unresolved documentation](docs/missing.md)") }
        finally { $writer.Dispose() }
    }
    finally { $archive.Dispose() }

    $failedAsExpected = $false
    try { & $validator -PackageDirectory $temporaryRoot -ExpectedVersion $ExpectedVersion -ExpectedCommit $ExpectedCommit }
    catch {
        $failedAsExpected = $_.Exception.Message -match "unresolved relative link"
        if (-not $failedAsExpected) {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "The package validator accepted an unresolved relative README link."
    }

    Write-Output "Negative package validation passed: an unresolved relative README link was rejected."

    Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $negativePackage = Join-Path $temporaryRoot "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg") -Destination $negativePackage
    Copy-Item -LiteralPath (Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg") -Destination $temporaryRoot
    Set-NuspecProjectUrl $negativePackage "https://github.com/KeelMatrix/EfGuard#readme"

    $failedAsExpected = $false
    try { & $validator -PackageDirectory $temporaryRoot -ExpectedVersion $ExpectedVersion -ExpectedCommit $ExpectedCommit }
    catch {
        $failedAsExpected = $_.Exception.Message -match "project URL"
        if (-not $failedAsExpected) {
            throw
        }
    }

    if (-not $failedAsExpected) {
        throw "The package validator accepted a project URL with a fragment."
    }

    Write-Output "Negative package validation passed: a project URL with a fragment was rejected."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

exit 0
