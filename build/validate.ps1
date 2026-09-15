[CmdletBinding()]
param(
    [string] $Configuration = "Release",
    [string] $Version = "0.1.0"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageDirectory = Join-Path $repositoryRoot "artifacts/packages"
$validationRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-validation-" + [Guid]::NewGuid().ToString("N"))
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$previousTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$env:MSBUILDDISABLENODEREUSE = "1"
$env:KEELMATRIX_NO_TELEMETRY = "1"

function Invoke-Dotnet([string[]] $Arguments) {
    $safeArguments = @($Arguments)
    if ($Arguments.Count -gt 0 -and $Arguments[0] -in @("restore", "build", "test", "pack")) {
        $safeArguments += @("-m:1", "-nodeReuse:false")
    }
    & dotnet @safeArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Reset-PackageDirectory([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
        return
    }

    $lastError = $null
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
            New-Item -ItemType Directory -Path $Path -Force | Out-Null
            return
        }
        catch {
            $lastError = $_
            if ($attempt -lt 3) { Start-Sleep -Milliseconds 250 }
        }
    }

    $remainingFiles = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -File -ErrorAction SilentlyContinue | Select-Object -ExpandProperty FullName)
    $lockedItem = if ($remainingFiles.Count -gt 0) { $remainingFiles[0] } else { $Path }
    throw "Could not clear package output '$Path' after 3 attempts. A file may be locked, including '$lockedItem'. Close the process holding it and rerun validation. Last error: $($lastError.Exception.Message)"
}

try {
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
    Reset-PackageDirectory $packageDirectory

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-Validate-Changelog.ps1")
    if ($LASTEXITCODE -ne 0) { throw "changelog contract regression coverage failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-CommitHistoryGate.ps1")
    if ($LASTEXITCODE -ne 0) { throw "commit history gate regression coverage failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Validate-WorkflowCredentials.ps1")
    if ($LASTEXITCODE -ne 0) { throw "workflow credential validation failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-Validate-WorkflowCredentials.ps1")
    if ($LASTEXITCODE -ne 0) { throw "workflow credential validation contract coverage failed." }

    Invoke-Dotnet @("restore", "KeelMatrix.EfGuard.sln", "--configfile", "NuGet.config")

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Validate-Dependencies.ps1") -SolutionPath "KeelMatrix.EfGuard.sln"
    if ($LASTEXITCODE -ne 0) { throw "dependency vulnerability audit failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-Validate-Dependencies.ps1")
    if ($LASTEXITCODE -ne 0) { throw "dependency vulnerability audit contract coverage failed." }

    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot "Test-Compare-ProviderEngineEvidence.ps1")
    if ($LASTEXITCODE -ne 0) { throw "provider engine evidence validation contract coverage failed." }

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
    if ($null -eq $previousNodeReuse) {
        Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    }
    $env:KEELMATRIX_NO_TELEMETRY = $previousTelemetry
    if (Test-Path -LiteralPath $validationRoot) { Remove-Item -LiteralPath $validationRoot -Recurse -Force }
}
