[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Validate-WorkflowCredentials.ps1"
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-workflow-credentials-" + [Guid]::NewGuid().ToString("N"))
$workflowDirectory = Join-Path $temporaryRoot ".github/workflows"
$scriptDirectory = Join-Path $temporaryRoot "build"

function Invoke-Validator([string] $Root) {
    $output = & pwsh -NoLogo -NoProfile -File $validator -RepositoryRoot $Root 2>&1 | Out-String
    [pscustomobject]@{
        ExitCode = $LASTEXITCODE
        Output = $output
    }
}

# This is the round-14 line-only implementation. Each newly covered bypass
# must be accepted here and rejected by the structural validator above.
$legacyValidator = @'
[CmdletBinding()]
param([string] $RepositoryRoot)
$ErrorActionPreference = "Stop"
function Test-RuntimeValue([string] $Value) {
    $trimmed = $Value.Trim().Trim('"', "'")
    if ([string]::IsNullOrWhiteSpace($trimmed) -or $trimmed -in @("null", "~")) { return $true }
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
        if ($match.Success -and -not (Test-RuntimeValue $match.Groups["value"].Value)) { return $true }
    }
    return $false
}
$workflowRoot = Join-Path $RepositoryRoot ".github/workflows"
if (-not (Test-Path -LiteralPath $workflowRoot -PathType Container)) { throw "workflow directory missing" }
foreach ($workflow in @(Get-ChildItem -LiteralPath $workflowRoot -Recurse -File | Where-Object Extension -in @(".yml", ".yaml"))) {
    foreach ($line in Get-Content -LiteralPath $workflow.FullName) {
        if ($line.TrimStart().StartsWith("#", [StringComparison]::Ordinal)) { continue }
        if ($line -match '^\s*id-token\s*:\s*write\s*$') { continue }
        if (Find-LiteralCredentialAssignment $line) { throw "literal credential assignment" }
    }
}
'@

try {
    New-Item -ItemType Directory -Path $workflowDirectory, $scriptDirectory -Force | Out-Null
    $legacyValidatorPath = Join-Path $temporaryRoot "legacy-validator.ps1"
    $legacyValidator | Set-Content -LiteralPath $legacyValidatorPath -Encoding utf8

    function Invoke-LegacyValidator([string] $Root) {
        $output = & pwsh -NoLogo -NoProfile -File $legacyValidatorPath -RepositoryRoot $Root 2>&1 | Out-String
        [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output
        }
    }

    function Write-Workflow([string] $Name, [string] $Contents) {
        Get-ChildItem -LiteralPath $workflowDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
        Get-ChildItem -LiteralPath $scriptDirectory -File -ErrorAction SilentlyContinue | Remove-Item -Force
        $Contents | Set-Content -LiteralPath (Join-Path $workflowDirectory "$Name.yml") -Encoding utf8
    }

    $secret = "Synthetic-Literal-" + [Guid]::NewGuid().ToString("N") + "!"
    $cases = @(
        @{
            Name = "plain-yaml"
            LegacyPasses = $false
            Contents = @"
name: plain-yaml
jobs:
  test:
    env:
      MSSQL_SA_PASSWORD: $secret
"@
        },
        @{
            Name = "single-quoted-yaml"
            LegacyPasses = $false
            Contents = @"
name: single-quoted-yaml
jobs:
  test:
    env:
      MSSQL_SA_PASSWORD: '$secret'
"@
        },
        @{
            Name = "quoted-key"
            LegacyPasses = $true
            Contents = @"
name: quoted-key
jobs:
  test:
    env:
      "MSSQL_SA_PASSWORD": "$secret"
"@
        },
        @{
            Name = "flow-mapping"
            LegacyPasses = $true
            Contents = @"
name: flow-mapping
jobs:
  test:
    env: { "MSSQL_SA_PASSWORD": "$secret" }
"@
        },
        @{
            Name = "connection-double-quoted-password"
            LegacyPasses = $true
            Contents = @"
name: connection-double-quoted-password
jobs:
  test:
    env:
      EFGUARD_SQLSERVER_CONNECTION: 'Server=localhost;Password="$secret";TrustServerCertificate=True'
"@
        },
        @{
            Name = "connection-single-quoted-password"
            LegacyPasses = $true
            Contents = @"
name: connection-single-quoted-password
jobs:
  test:
    env:
      EFGUARD_SQLSERVER_CONNECTION: "Server=localhost;Password='$secret';TrustServerCertificate=True"
"@
        },
        @{
            Name = "block-connection"
            LegacyPasses = $true
            Contents = @"
name: block-connection
jobs:
  test:
    run: |
      connection='Server=localhost;Password="$secret";TrustServerCertificate=True'
"@
        },
        @{
            Name = "folded-connection"
            LegacyPasses = $true
            Contents = @"
name: folded-connection
jobs:
  test:
    run: >
      connection="Server=localhost;
      Password='$secret';TrustServerCertificate=True"
"@
        },
        @{
            Name = "multiline-connection"
            LegacyPasses = $true
            Contents = @"
name: multiline-connection
jobs:
  test:
    env:
      EFGUARD_SQLSERVER_CONNECTION: |
        Server=localhost;
        Password="$secret";
        TrustServerCertificate=True
"@
        },
        @{
            Name = "script-assignment"
            LegacyPasses = $true
            Script = "`$databasePassword = `"$secret`"`n"
            Contents = @"
name: script-assignment
jobs:
  test:
    steps:
      - run: ./build/credential-script.ps1
"@
        },
        @{
            Name = "env-indirection"
            LegacyPasses = $true
            Script = @"
`$DB_PASSWORD = "$secret"
`$connection = "Server=localhost;Password=`$env:DB_PASSWORD"
"@
            Contents = @"
name: env-indirection
jobs:
  test:
    steps:
      - run: ./build/credential-script.ps1
"@
        },
        @{
            Name = "github-expression-fallback"
            LegacyPasses = $true
            Contents = (@"
name: github-expression-fallback
jobs:
  test:
    env:
      MSSQL_SA_PASSKEY: `${{ secrets.MISSING || '$secret' }}
"@).Replace("PASSKEY", "PASSWORD")
        },
        @{
            Name = "anchor"
            LegacyPasses = $false
            Contents = (@"
name: anchor
jobs:
  test:
    env:
      base: &credential "$secret"
      passKEY: *credential
"@).Replace("passKEY", "password")
        },
        @{
            Name = "alias"
            LegacyPasses = $false
            Contents = @"
name: alias
jobs:
  test:
    env:
      password: &credential "$secret"
      copied: *credential
"@
        },
        @{
            Name = "shell-assignment"
            LegacyPasses = $false
            Contents = @"
name: shell-assignment
jobs:
  test:
    run: |
      PASSWORD=$secret
"@
        }
    )

    foreach ($case in $cases) {
        if ($case.ContainsKey("Script")) {
            $case.Script | Set-Content -LiteralPath (Join-Path $scriptDirectory "credential-script.ps1") -Encoding utf8
        }
        Write-Workflow $case.Name $case.Contents

        $result = Invoke-Validator $temporaryRoot
        if ($result.ExitCode -eq 0) {
            throw "Structural validator accepted negative case '$($case.Name)'."
        }

        $legacyResult = Invoke-LegacyValidator $temporaryRoot
        if ($case.LegacyPasses -and $legacyResult.ExitCode -ne 0) {
            throw "Pre-fix validator did not accept bypass case '$($case.Name)'."
        }
        if (-not $case.LegacyPasses -and $legacyResult.ExitCode -eq 0) {
            throw "Pre-fix validator unexpectedly accepted existing case '$($case.Name)'."
        }
    }

    Write-Workflow "malformed" "name: ["
    $malformedResult = Invoke-Validator $temporaryRoot
    if ($malformedResult.ExitCode -eq 0) {
        throw "The structural validator accepted malformed workflow YAML."
    }

    Write-Workflow "missing-script" @'
name: missing-script
jobs:
  test:
    steps:
      - run: ./build/missing-script.ps1
'@
    $missingScriptResult = Invoke-Validator $temporaryRoot
    if ($missingScriptResult.ExitCode -eq 0) {
        throw "The structural validator accepted an uninspectable workflow-invoked script."
    }

    Write-Workflow "runtime" @'
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
      NUGET_API_KEY: ${{ steps.nuget-login.outputs.NUGET_API_KEY }}
      EFGUARD_INDIRECT: "Server=localhost;Password=$env:EFGUARD_PASSWORD"
'@
    $runtimeResult = Invoke-Validator $temporaryRoot
    if ($runtimeResult.ExitCode -ne 0) {
        throw "The structural validator rejected runtime credential expressions. Output: $($runtimeResult.Output)"
    }

    $repositoryResult = Invoke-Validator (Join-Path $PSScriptRoot "..")
    if ($repositoryResult.ExitCode -ne 0) {
        throw "The structural validator rejected the repository workflows or its own pattern strings. Output: $($repositoryResult.Output)"
    }

    Write-Output "Workflow credential validation contract passed: structural bypasses fail, pre-fix bypasses are proven, and runtime expressions pass."
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
