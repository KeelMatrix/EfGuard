[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Validate-Dependencies.ps1"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-dependency-audit-" + [Guid]::NewGuid().ToString("N"))
$previousPath = $env:PATH

function New-FakeDotnet([string] $Name, [string] $Report, [int] $ExitCode) {
    $reportPath = Join-Path $temporaryRoot "$Name.report"
    [IO.File]::WriteAllBytes($reportPath, [Text.Encoding]::UTF8.GetBytes($Report))
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

function Normalize-LineEndings([string] $Value) {
    if ($null -eq $Value) { return $null }
    return $Value.Replace("`r`n", "`n").Replace("`r", "`n")
}

function Normalize-AssertionWhitespace([string] $Value) {
    if ($null -eq $Value) { return $null }
    return (($Value -replace "\|", " " -replace "\s+", " ").Trim())
}

function Assert-FailedResult([string] $Name, [psobject] $Result, [string] $ExpectedMessage) {
    if ($Result.ExitCode -eq 0) {
        throw "$Name unexpectedly passed. Output: $($Result.Output)"
    }
    $normalizedOutput = Normalize-AssertionWhitespace (Normalize-LineEndings $Result.Output)
    $normalizedExpectedMessage = Normalize-AssertionWhitespace (Normalize-LineEndings $ExpectedMessage)
    if ($normalizedOutput -notmatch [regex]::Escape($normalizedExpectedMessage)) {
        throw "$Name failed for the wrong reason. Expected '$ExpectedMessage'. Output: $($Result.Output)"
    }
}

function Assert-Fails([string] $Name, [string] $Report, [int] $CommandExitCode, [string] $ExpectedMessage) {
    $result = Invoke-Audit $Name $Report $CommandExitCode
    Assert-FailedResult $Name $result $ExpectedMessage
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

    $multilineVulnerableReportLf = "The given project 'Example' has the following vulnerable packages`n  > Example.Package 1.0.0 High"
    $multilineVulnerableReportCrLf = $multilineVulnerableReportLf.Replace("`n", "`r`n")
    $lfResult = Invoke-Audit "vulnerable-package-report-lf" $multilineVulnerableReportLf 0
    $crlfResult = Invoke-Audit "vulnerable-package-report-crlf" $multilineVulnerableReportCrLf 0
    Assert-FailedResult "vulnerable-package-report-lf" $lfResult $multilineVulnerableReportLf
    Assert-FailedResult "vulnerable-package-report-crlf" $crlfResult $multilineVulnerableReportCrLf
    if ($lfResult.ExitCode -ne $crlfResult.ExitCode) {
        throw "LF and CRLF vulnerable reports produced different validator exit codes."
    }
    if ((Normalize-LineEndings $lfResult.Output) -ne (Normalize-LineEndings $crlfResult.Output)) {
        throw "LF and CRLF vulnerable reports produced different validator handling."
    }

    Assert-Fails "vulnerable-package-report" `
        "The given project 'Example' has the following vulnerable packages" `
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

    $wrongMessageRejected = $false
    try {
        Assert-Fails "wrong-message" `
            "The given project 'Example' has the following vulnerable packages`r`n  > Example.Package 1.0.0 High" `
            0 `
            "The dependency graph reported a different result."
    }
    catch {
        if ($_.Exception.Message -notmatch "failed for the wrong reason") {
            throw
        }
        $wrongMessageRejected = $true
    }
    if (-not $wrongMessageRejected) {
        throw "The dependency audit contract accepted a wrong expected message."
    }

    Write-Output "Dependency audit fail-closed contract passed."
}
finally {
    $env:PATH = $previousPath
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

exit 0
