[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [string] $ExpectedVersion = "0.1.0",
    [string] $ExpectedCommit
)

$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.IO.Compression.FileSystem

$packageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
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
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
