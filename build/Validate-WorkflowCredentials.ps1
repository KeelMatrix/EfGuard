[CmdletBinding()]
param(
    [string] $RepositoryRoot
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Join-Path $PSScriptRoot ".."
}
$RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)

function Fail([string] $Message) {
    throw "Workflow credential validation failed: $Message"
}

function Test-RuntimeValue([string] $Value) {
    $trimmed = $Value.Trim().Trim('"', "'")
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed -in @("null", "~")) {
        return $true
    }

    return $trimmed -match '\$\{\{' -or
        $trimmed -match '^\$env:' -or
        $trimmed -match '^\$[A-Za-z_][A-Za-z0-9_]*' -or
        $trimmed -match '^\$\(' -or
        $trimmed -match '^(?:secrets|vars|github|inputs|env)\.'
}

function Find-LiteralCredentialAssignment([string] $Line) {
    $patterns = @(
        '(?i)\b(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)\s*=\s*(?<value>[^;,\s"'']+)',
        '(?i)^\s*[\w.-]*(?:password|passwd|pwd|api[_-]?key|secret|token|private[_-]?key)[\w.-]*\s*:\s*(?<value>.+?)\s*(?:#.*)?$'
    )

    foreach ($pattern in $patterns) {
        $match = [regex]::Match($Line, $pattern)
        if ($match.Success -and -not (Test-RuntimeValue $match.Groups["value"].Value)) {
            return $true
        }
    }

    return $false
}

if (-not (Test-Path -LiteralPath $RepositoryRoot -PathType Container)) {
    Fail "repository root '$RepositoryRoot' does not exist."
}

$workflowRoot = Join-Path $RepositoryRoot ".github/workflows"
if (-not (Test-Path -LiteralPath $workflowRoot -PathType Container)) {
    Fail "workflow directory '$workflowRoot' does not exist."
}

$violations = [System.Collections.Generic.List[string]]::new()
foreach ($workflow in @(Get-ChildItem -LiteralPath $workflowRoot -Recurse -File | Where-Object Extension -in @(".yml", ".yaml"))) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $workflow.FullName) {
        $lineNumber++
        if ($line.TrimStart().StartsWith("#", [StringComparison]::Ordinal)) {
            continue
        }
        if ($line -match '^\s*id-token\s*:\s*write\s*$') {
            continue
        }
        if (Find-LiteralCredentialAssignment $line) {
            $relativePath = [IO.Path]::GetRelativePath($RepositoryRoot, $workflow.FullName).Replace('\', '/')
            $violations.Add("${relativePath}:$lineNumber")
        }
    }
}

if ($violations.Count -gt 0) {
    Fail "literal credential assignment(s) found at $($violations -join ', '). Use a runtime expression or secure CI mechanism."
}

Write-Output "Workflow credential validation passed: no literal credential assignments found."
