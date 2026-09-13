[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Validate-WorkflowCredentials.ps1"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-workflow-credentials-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Validator([string] $Root) {
    $output = & pwsh -NoLogo -NoProfile -File $validator -RepositoryRoot $Root 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

try {
    New-Item -ItemType Directory -Path (Join-Path $temporaryRoot ".github/workflows") -Force | Out-Null

    $legacyPassword = "Synthetic-Literal-" + [Guid]::NewGuid().ToString("N") + "!"
    @"
name: legacy
jobs:
  test:
    services:
      sql:
        env:
          MSSQL_SA_PASSWORD: "$legacyPassword"
    env:
      EFGUARD_SQLSERVER_CONNECTION: "Server=localhost;Password=$legacyPassword"
"@ | Set-Content -LiteralPath (Join-Path $temporaryRoot ".github/workflows/legacy.yml") -Encoding utf8

    $legacyResult = Invoke-Validator $temporaryRoot
    if ($legacyResult.ExitCode -eq 0 -or $legacyResult.Output -notmatch "literal credential assignment") {
        throw "The workflow validator accepted a legacy literal credential assignment. Output: $($legacyResult.Output)"
    }

    @'
name: runtime
jobs:
  test:
    services:
      sql:
        env:
          MSSQL_SA_PASSWORD: "EfGuard_${{ github.run_id }}!"
    env:
      EFGUARD_SQLSERVER_CONNECTION: "Server=localhost;Password=EfGuard_${{ github.run_id }}!"
      EFGUARD_POSTGRES_CONNECTION: "Host=localhost;Password=${{ secrets.RUNTIME_PASSWORD }}"
'@ | Set-Content -LiteralPath (Join-Path $temporaryRoot ".github/workflows/runtime.yml") -Encoding utf8

    Remove-Item -LiteralPath (Join-Path $temporaryRoot ".github/workflows/legacy.yml") -Force
    $runtimeResult = Invoke-Validator $temporaryRoot
    if ($runtimeResult.ExitCode -ne 0) {
        throw "The workflow validator rejected runtime credential expressions. Output: $($runtimeResult.Output)"
    }

    Write-Output "Workflow credential validation contract passed: literal assignments fail and runtime expressions pass."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
