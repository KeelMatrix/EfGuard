[CmdletBinding()]
param(
    [string] $SolutionPath = "KeelMatrix.EfGuard.sln"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionFullPath = if ([IO.Path]::IsPathRooted($SolutionPath)) {
    [IO.Path]::GetFullPath($SolutionPath)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $SolutionPath))
}

function Fail([string] $Message) {
    throw "Dependency vulnerability audit failed: $Message"
}

if (-not (Test-Path -LiteralPath $solutionFullPath -PathType Leaf)) {
    Fail "solution '$solutionFullPath' does not exist."
}

$solutionArgument = [IO.Path]::GetRelativePath($repositoryRoot, $solutionFullPath).Replace('\', '/')
$arguments = @(
    "list",
    $solutionArgument,
    "package",
    "--vulnerable",
    "--include-transitive"
)

$outputLines = & dotnet @arguments 2>&1
$status = $LASTEXITCODE
$report = $outputLines | Out-String
$report | Write-Output

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    $report | Out-File -FilePath $env:GITHUB_STEP_SUMMARY -Encoding utf8 -Append
}

if ($status -ne 0) {
    Fail "the vulnerability command failed with exit code $status."
}

if ($report -match '(?im)has the following vulnerable packages?') {
    Fail "the dependency graph contains vulnerable packages."
}

if ($report -notmatch '(?im)has (?:no|the following) vulnerable packages?') {
    Fail "the vulnerability command returned an unrecognized result."
}

Write-Output "Dependency vulnerability audit passed with direct and transitive packages checked."
