[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageDirectory = Join-Path $repositoryRoot "artifacts/packages"
$validationRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-validation-" + [Guid]::NewGuid().ToString("N"))
$previousTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$env:KEELMATRIX_NO_TELEMETRY = "1"

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

try {
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
    if (Test-Path -LiteralPath $packageDirectory) { Remove-Item -LiteralPath $packageDirectory -Recurse -Force }
    New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null

    Invoke-Dotnet @("restore", "KeelMatrix.EfGuard.sln", "--configfile", "NuGet.config")
    Invoke-Dotnet @("build", "KeelMatrix.EfGuard.sln", "--configuration", $Configuration, "--no-restore")
    Invoke-Dotnet @("test", "KeelMatrix.EfGuard.sln", "--configuration", $Configuration, "--no-build", "--no-restore")
    Invoke-Dotnet @("format", "KeelMatrix.EfGuard.sln", "--verify-no-changes", "--no-restore")
    Invoke-Dotnet @("pack", "src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj", "--configuration", $Configuration, "--no-build", "--no-restore", "--include-symbols", "--p:SymbolPackageFormat=snupkg", "--output", $packageDirectory)

    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw "git rev-parse HEAD failed." }
    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Validate-Package.ps1") -PackageDirectory $packageDirectory -ExpectedVersion $Version -ExpectedCommit $commit
    if ($LASTEXITCODE -ne 0) { throw "package contract validation failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-Validate-Package.ps1") -PackageDirectory $packageDirectory -ExpectedVersion $Version -ExpectedCommit $commit
    if ($LASTEXITCODE -ne 0) { throw "negative package contract validation failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Validate-PackageConsumer.ps1") -PackageDirectory $packageDirectory -Version $Version -WorkingDirectory $repositoryRoot
    if ($LASTEXITCODE -ne 0) { throw "isolated package consumer validation failed." }

    Write-Output "Local validation passed with telemetry disabled, package contract validation, and isolated package consumer smoke."
}
finally {
    $env:KEELMATRIX_NO_TELEMETRY = $previousTelemetry
    if (Test-Path -LiteralPath $validationRoot) { Remove-Item -LiteralPath $validationRoot -Recurse -Force }
}
