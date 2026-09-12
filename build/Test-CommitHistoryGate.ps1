[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-history-gate-" + [Guid]::NewGuid().ToString("N"))
$messageEncoding = [Text.UTF8Encoding]::new($false)
$programFiles = [Environment]::GetEnvironmentVariable("ProgramFiles")
$gitBash = if ([string]::IsNullOrWhiteSpace($programFiles)) { $null } else { Join-Path $programFiles "Git\bin\bash.exe" }
$bashPath = if ($gitBash -and (Test-Path -LiteralPath $gitBash)) { $gitBash } else { (Get-Command bash -ErrorAction SilentlyContinue).Source }
if ([string]::IsNullOrWhiteSpace($bashPath)) {
    throw "Git Bash is required to run the repository hooks."
}

function Write-Message([string] $Path, [string] $Message) {
    [IO.File]::WriteAllText($Path, $Message, $messageEncoding)
}

function Invoke-CommitMessageHook([string] $Path, [string] $Message) {
    Write-Message $Path $Message
    Push-Location $repositoryRoot
    try {
        & $bashPath --login -c 'sh "$1" "$2"' efguard-history-gate .githooks/commit-msg $Path | Out-Null
        $status = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    return [int]$status
}

function Invoke-HistoryHook([string] $Head) {
    Push-Location $repositoryRoot
    try {
        & $bashPath --login -c 'sh "$1" "$2" "$3"' efguard-history-gate .githooks/check-history "0000000000000000000000000000000000000000" $Head | Out-Null
        $status = $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
    return [int]$status
}

try {
    New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
    $originalPath = Join-Path $temporaryRoot "original.txt"
    $rewrittenPath = Join-Path $temporaryRoot "rewritten.txt"
    $conflictPath = Join-Path $temporaryRoot "conflict.txt"
    $markerPath = Join-Path $temporaryRoot "marker.txt"

    $ticketPrefix = [string]::Concat([char]0x4B, [char]0x45, [char]0x45)
    $conflictsLabel = [string]::Concat([char]0x43, [char]0x6F, [char]0x6E, [char]0x66, [char]0x6C, [char]0x69, [char]0x63, [char]0x74, [char]0x73)
    $markerLeft = [string]::new([char]0x3C, 7)
    $markerMiddle = [string]::new([char]0x3D, 7)
    $markerRight = [string]::new([char]0x3E, 7)
    $originalMessage = @"
fix: fail closed on partial metadata

Treat partial loads in selected EF metadata assemblies as fatal while preserving bounded diagnostics for peripheral assemblies.

Refs ${ticketPrefix}-258

# ${conflictsLabel}:
#	src/KeelMatrix.EfGuard.Worker/Program.cs
#	tests/KeelMatrix.EfGuard.Tests/CliContractTests.cs
#	tests/KeelMatrix.EfGuard.Tests/ExtractionFixtureTests.cs
"@
    $rewrittenMessage = @"
fix: fail closed on partial metadata

    Treat partial loads in selected EF metadata assemblies as fatal while preserving bounded diagnostics for peripheral assemblies.
"@
    $conflictMessage = @"
fix: preserve useful metadata diagnostics

# ${conflictsLabel}:
#	src/KeelMatrix.EfGuard.Worker/Program.cs
"@
    $markerMessage = "review merge metadata`n`n${markerLeft} local`nkeep one side`n${markerMiddle}`n${markerRight} remote`n"

    $originalStatus = Invoke-CommitMessageHook $originalPath $originalMessage
    if ($originalStatus -eq 0) {
        throw "The commit-message gate accepted the original polluted message (exit $originalStatus)."
    }
    $rewrittenStatus = Invoke-CommitMessageHook $rewrittenPath $rewrittenMessage
    if ($rewrittenStatus -ne 0) {
        throw "The commit-message gate rejected the rewritten developer-facing message (exit $rewrittenStatus)."
    }
    $conflictStatus = Invoke-CommitMessageHook $conflictPath $conflictMessage
    if ($conflictStatus -eq 0) {
        throw "The commit-message gate accepted a conflict-label line (exit $conflictStatus)."
    }
    $markerStatus = Invoke-CommitMessageHook $markerPath $markerMessage
    if ($markerStatus -eq 0) {
        throw "The commit-message gate accepted conflict-marker lines (exit $markerStatus)."
    }

    $head = (& git -C $repositoryRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($head)) {
        throw "Could not determine the current repository commit."
    }
    if ((Invoke-HistoryHook $head) -ne 0) {
        throw "The history gate rejected the current reachable branch history."
    }

    Write-Output "Commit-message and reachable-history gate regression coverage passed."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
