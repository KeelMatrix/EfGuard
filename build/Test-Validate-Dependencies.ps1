[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Validate-Dependencies.ps1"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-dependency-audit-" + [Guid]::NewGuid().ToString("N"))
$previousPath = $env:PATH

function New-FakeDotnet([string] $Name, [string] $Report, [int] $ExitCode) {
    $reportPath = Join-Path $temporaryRoot "$Name.report"
    Set-Content -LiteralPath $reportPath -Value $Report -Encoding utf8
    $env:EFGUARD_AUDIT_TEST_REPORT = $reportPath
    $env:EFGUARD_AUDIT_TEST_EXIT_CODE = [string]$ExitCode
}

function Invoke-Audit([string] $Name, [string] $Report, [int] $CommandExitCode) {
    $previousReport = $env:EFGUARD_AUDIT_TEST_REPORT
    $previousExitCode = $env:EFGUARD_AUDIT_TEST_EXIT_CODE
    try {
        New-FakeDotnet $Name $Report $CommandExitCode
        $output = & pwsh -NoLogo -NoProfile -File $validator -SolutionPath "KeelMatrix.EfGuard.sln" 2>&1 | Out-String
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    }
    finally {
        $env:EFGUARD_AUDIT_TEST_REPORT = $previousReport
        $env:EFGUARD_AUDIT_TEST_EXIT_CODE = $previousExitCode
    }
}

function Assert-Fails([string] $Name, [string] $Report, [int] $CommandExitCode, [string] $ExpectedMessage) {
    $result = Invoke-Audit $Name $Report $CommandExitCode
    if ($result.ExitCode -eq 0) {
        throw "$Name unexpectedly passed. Output: $($result.Output)"
    }
    if ($result.Output -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "$Name failed for the wrong reason. Expected '$ExpectedMessage'. Output: $($result.Output)"
    }
}

try {
    if (-not (Test-Path -LiteralPath $validator -PathType Leaf)) {
        throw "Dependency audit validator is missing."
    }
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null

    $fakeDotnetDirectory = Join-Path $temporaryRoot "fake-dotnet"
    New-Item -ItemType Directory -Path $fakeDotnetDirectory -Force | Out-Null
    if ([OperatingSystem]::IsWindows()) {
        $fakeDotnetPath = Join-Path $fakeDotnetDirectory "dotnet.cmd"
        @'
@echo off
if not "%~1 %~2 %~3 %~4 %~5"=="list KeelMatrix.EfGuard.sln package --vulnerable --include-transitive" exit /b 91
type "%EFGUARD_AUDIT_TEST_REPORT%"
exit /b %EFGUARD_AUDIT_TEST_EXIT_CODE%
'@ | Set-Content -LiteralPath $fakeDotnetPath -Encoding ascii
    }
    else {
        $fakeDotnetPath = Join-Path $fakeDotnetDirectory "dotnet"
        @'
#!/usr/bin/env sh
if [ "$*" != "list KeelMatrix.EfGuard.sln package --vulnerable --include-transitive" ]; then
    exit 91
fi
cat "$EFGUARD_AUDIT_TEST_REPORT"
exit "$EFGUARD_AUDIT_TEST_EXIT_CODE"
'@ | Set-Content -LiteralPath $fakeDotnetPath -Encoding utf8
        & chmod +x $fakeDotnetPath
        if ($LASTEXITCODE -ne 0) { throw "Could not make the fake dotnet command executable." }
    }
    $env:PATH = "$fakeDotnetDirectory$([IO.Path]::PathSeparator)$previousPath"

    Assert-Fails "vulnerable-package-report" `
        "The given project 'Example' has the following vulnerable packages`n  > Example.Package 1.0.0 High" `
        0 `
        "The dependency graph contains vulnerable packages."

    $cleanResult = Invoke-Audit "clean-report" `
        "The given project 'Example' has no vulnerable packages given the sources listed." `
        0
    if ($cleanResult.ExitCode -ne 0) {
        throw "A clean dependency report failed unexpectedly. Output: $($cleanResult.Output)"
    }

    Assert-Fails "unrecognized-report" `
        "The dependency command returned an unexpected format." `
        0 `
        "The vulnerability command returned an unrecognized result."

    Assert-Fails "audit-command-failure" `
        "Unable to inspect the dependency graph." `
        7 `
        "The vulnerability command failed with exit code 7."

    Write-Output "Dependency audit fail-closed contract passed."
}
finally {
    $env:PATH = $previousPath
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
