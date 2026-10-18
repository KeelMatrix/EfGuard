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

function Invoke-HistoryHook([string] $WorkingDirectory, [string] $Start, [string] $End) {
        $historyHook = Join-Path $repositoryRoot ".githooks/check-history"
        & $bashPath --login -c 'cd "$1" && sh "$2" "$3" "$4"' efguard-history-gate $WorkingDirectory $historyHook $Start $End | Out-Null
        $status = $LASTEXITCODE
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

    $historyRepository = Join-Path $temporaryRoot "history-repository"
    New-Item -ItemType Directory -Path $historyRepository -Force | Out-Null
    & git -C $historyRepository init --quiet
    if ($LASTEXITCODE -ne 0) { throw "Could not initialize the synthetic history repository." }
    & git -C $historyRepository config user.name "KeelMatrix"
    & git -C $historyRepository config user.email "keelmatrix@gmail.com"
    "legacy baseline" | Set-Content -LiteralPath (Join-Path $historyRepository "baseline.txt")
    & git -C $historyRepository add baseline.txt
    & git -C $historyRepository commit --quiet -m "legacy baseline" -m "Signed-off-by: dependabot[bot] <support@github.com>"
    if ($LASTEXITCODE -ne 0) { throw "Could not create the synthetic legacy baseline." }
    $base = (& git -C $historyRepository rev-parse HEAD).Trim()
    "clean change" | Set-Content -LiteralPath (Join-Path $historyRepository "clean.txt")
    & git -C $historyRepository add clean.txt
    & git -C $historyRepository commit --quiet -m "fix: add clean change"
    if ($LASTEXITCODE -ne 0) { throw "Could not create the synthetic clean commit." }
    $cleanHead = (& git -C $historyRepository rev-parse HEAD).Trim()
    if ((Invoke-HistoryHook $historyRepository $base $cleanHead) -ne 0) {
        throw "The history gate rejected clean introduced history after an older baseline commit."
    }
    "polluted change" | Set-Content -LiteralPath (Join-Path $historyRepository "polluted.txt")
    & git -C $historyRepository add polluted.txt
    & git -C $historyRepository commit --quiet -m "fix: add polluted change" -m "Co-authored-by: unauthorized identity"
    if ($LASTEXITCODE -ne 0) { throw "Could not create the synthetic polluted commit." }
    $pollutedHead = (& git -C $historyRepository rev-parse HEAD).Trim()
    if ((Invoke-HistoryHook $historyRepository $cleanHead $pollutedHead) -eq 0) {
        throw "The history gate accepted an introduced commit with a prohibited identity trailer."
    }

    Write-Output "Commit-message and introduced-history gate regression coverage passed."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
