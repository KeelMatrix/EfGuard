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
    & git -C $temporaryRoot init --quiet
    if ($LASTEXITCODE -ne 0) { throw "could not initialize synthetic repository" }
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
	Get-ChildItem -LiteralPath $temporaryRoot -Force |
		Where-Object Name -notin @(".git", "legacy-validator.ps1") |
		Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
	New-Item -ItemType Directory -Path $workflowDirectory, $scriptDirectory -Force | Out-Null
	$Contents | Set-Content -LiteralPath (Join-Path $workflowDirectory "$Name.yml") -Encoding utf8
}

function Write-Files([hashtable] $Files) {
	foreach ($file in $Files.GetEnumerator()) {
		$path = Join-Path $temporaryRoot $file.Key
		$parent = Split-Path -Parent $path
		New-Item -ItemType Directory -Path $parent -Force | Out-Null
		$file.Value | Set-Content -LiteralPath $path -Encoding utf8
	}
}

function Sync-TrackedFiles {
	& git -C $temporaryRoot add -A 2>$null | Out-Null
	if ($LASTEXITCODE -ne 0) { throw "could not update synthetic repository index" }
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
            Name = "block-script-assignment-split"
            LegacyPasses = $true
            Script = "`$databasePassword =`n  `"$secret`"`n"
            Contents = @"
name: block-script-assignment-split
jobs:
  test:
    steps:
      - run: ./build/credential-script.ps1
"@
        },
        @{
            Name = "nested-script-2"
            LegacyPasses = $true
            Files = @{
                "build/outer.ps1" = '& "$PSScriptRoot/inner.ps1"'
                "build/inner.ps1" = "`$databasePassword = `"$secret`""
            }
            Contents = @"
name: nested-script-2
jobs:
  test:
    steps:
      - run: ./build/outer.ps1
"@
        },
        @{
            Name = "nested-script-3"
            LegacyPasses = $true
            Files = @{
                "build/outer.ps1" = '& "$PSScriptRoot/middle.ps1"'
                "build/middle.ps1" = '& "$PSScriptRoot/inner.ps1"'
                "build/inner.ps1" = "`$databasePassword = `"$secret`""
            }
            Contents = @"
name: nested-script-3
jobs:
  test:
    steps:
      - run: ./build/outer.ps1
"@
        },
        @{
            Name = "ci-wired-json"
            LegacyPasses = $true
            Files = @{
                "build/read-ci-json.ps1" = 'Get-Content ./fixtures/ci-credentials.json | ConvertFrom-Json'
                "fixtures/ci-credentials.json" = "{`"password`":`"$secret`"}"
            }
            Contents = @"
name: ci-wired-json
jobs:
  test:
    steps:
      - run: ./build/read-ci-json.ps1
"@
        },
        @{
            Name = "reachable-script-data-file"
            LegacyPasses = $true
            Files = @{
                "build/read-data.ps1" = 'Get-Content ./fixtures/credentials.ini'
                "fixtures/credentials.ini" = "password=$secret"
            }
            Contents = @"
name: reachable-script-data-file
jobs:
  test:
    steps:
      - run: ./build/read-data.ps1
"@
        },
        @{
            Name = "reachable-xml-credential"
            LegacyPasses = $true
            Files = @{
                "build/read-xml.ps1" = 'Get-Content ./fixtures/credentials.xml'
                "fixtures/credentials.xml" = "<settings><password>$secret</password></settings>"
            }
            Contents = @"
name: reachable-xml-credential
jobs:
  test:
    steps:
      - run: ./build/read-xml.ps1
"@
        },
        @{
            Name = "reachable-csv-credential"
            LegacyPasses = $true
            Files = @{
                "build/read-csv.ps1" = 'Get-Content ./fixtures/credentials.csv'
                "fixtures/credentials.csv" = "username,password`nci-user,$secret"
            }
            Contents = @"
name: reachable-csv-credential
jobs:
  test:
    steps:
      - run: ./build/read-csv.ps1
"@
        },
        @{
            Name = "reachable-multiline-connection"
            LegacyPasses = $true
            Files = @{
                "build/connection.ps1" = (@'
$connection = @"
Server=localhost;
__CREDENTIAL_KEY__="__SECRET__";
TrustServerCertificate=True
"@
'@).Replace("__CREDENTIAL_KEY__", "Password").Replace("__SECRET__", $secret)
            }
            Contents = @"
name: reachable-multiline-connection
jobs:
  test:
    steps:
      - run: ./build/connection.ps1
"@
        },
        @{
            Name = "password-command-argument"
            LegacyPasses = $true
            Contents = @"
name: password-command-argument
jobs:
  test:
    steps:
      - run: dotnet tool run fixture --password "$secret"
"@
        },
        @{
            Name = "password-command-argument-split"
            LegacyPasses = $true
            Contents = @"
name: password-command-argument-split
jobs:
  test:
    steps:
      - run: |
          dotnet tool run fixture --password
            "$secret"
"@
        },
        @{
            Name = "password-command-argument-case-insensitive"
            LegacyPasses = $true
            Files = @{
                "build/password-command.ps1" = "tool.exe -Password `"$secret`""
            }
            Contents = @"
name: password-command-argument-case-insensitive
jobs:
  test:
    steps:
      - run: ./build/password-command.ps1
"@
        },
        @{
            Name = "short-password-command-argument"
            LegacyPasses = $true
            Files = @{
                "build/short-password-command.ps1" = "tool.exe -p `"$secret`""
            }
            Contents = @"
name: short-password-command-argument
jobs:
  test:
    steps:
      - run: ./build/short-password-command.ps1
"@
        },
        @{
            Name = "attached-short-password-command-argument"
            LegacyPasses = $true
            Contents = @"
name: attached-short-password-command-argument
jobs:
  test:
    steps:
      - run: tool.exe -p`"$secret`"
"@
        },
        @{
            Name = "colon-short-password-command-argument"
            LegacyPasses = $true
            Files = @{
                "build/colon-short-password-command.ps1" = "tool.exe -p:`"$secret`""
            }
            Contents = @"
name: colon-short-password-command-argument
jobs:
  test:
    steps:
      - run: ./build/colon-short-password-command.ps1
"@
        },
        @{
            Name = "colon-long-password-command-argument"
            LegacyPasses = $true
            Contents = @"
name: colon-long-password-command-argument
jobs:
  test:
    steps:
      - run: tool.exe --password:`"$secret`"
"@
        },
        @{
            Name = "colon-pascal-password-command-argument"
            LegacyPasses = $true
            Files = @{
                "build/colon-pascal-password-command.ps1" = "tool.exe -Password:`"$secret`""
            }
            Contents = @"
name: colon-pascal-password-command-argument
jobs:
  test:
    steps:
      - run: ./build/colon-pascal-password-command.ps1
"@
        },
        @{
            Name = "attached-short-password-equals-command-argument"
            LegacyPasses = $true
            Files = @{
                "build/attached-short-password-equals-command.ps1" = "tool.exe -p=`"$secret`""
            }
            Contents = @"
name: attached-short-password-equals-command-argument
jobs:
  test:
    steps:
      - run: ./build/attached-short-password-equals-command.ps1
"@
        },
        @{
            Name = "unallowlisted-attached-short-option"
            LegacyPasses = $true
            Contents = @"
name: unallowlisted-attached-short-option
jobs:
  test:
    steps:
      - run: tool.exe -pathological`"$secret`"
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
		Write-Workflow $case.Name $case.Contents
        if ($case.ContainsKey("Script")) {
            $case.Script | Set-Content -LiteralPath (Join-Path $scriptDirectory "credential-script.ps1") -Encoding utf8
        }
		if ($case.ContainsKey("Files")) {
			Write-Files $case.Files
		}
		Sync-TrackedFiles

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

    $passingCases = @(
        @{
            Name = "allowlisted-attached-short-options"
            Contents = @'
name: allowlisted-attached-short-options
jobs:
  test:
    steps:
      - run: >-
          tool.exe -path:src -project:app -properties:props -platform:x64
          -publish:feed -preview:latest -parallel:4 -port:5432 -provider:sql
'@
        },
        @{
            Name = "attached-runtime-command-arguments"
            Contents = @'
name: attached-runtime-command-arguments
jobs:
  test:
    steps:
      - run: >-
          tool.exe -p"$env:EFGUARD_PASSWORD"
          --password:"${{ secrets.RUNTIME_PASSWORD }}"
'@
        }
    )

    foreach ($case in $passingCases) {
        Write-Workflow $case.Name $case.Contents
        Sync-TrackedFiles

        $result = Invoke-Validator $temporaryRoot
        if ($result.ExitCode -ne 0) {
            throw "Structural validator rejected positive case '$($case.Name)'. Output: $($result.Output)"
        }
    }

    Write-Workflow "depth-cap" @'
name: depth-cap
jobs:
  test:
    steps:
      - run: ./build/depth-0.ps1
'@
    $depthFiles = @{}
    for ($index = 0; $index -lt 5; $index++) {
        $next = if ($index -lt 4) {
            '& "$PSScriptRoot/depth-{0}.ps1"' -f ($index + 1)
        }
        else {
            ('$password = "__SECRET__"').Replace("__SECRET__", $secret)
        }
        $depthFiles["build/depth-$index.ps1"] = $next
    }
    Write-Files $depthFiles
    Sync-TrackedFiles
    $depthResult = Invoke-Validator $temporaryRoot
    if ($depthResult.ExitCode -eq 0) {
        throw "The structural validator accepted a reachable reference beyond its depth cap."
    }
    $depthLegacyResult = Invoke-LegacyValidator $temporaryRoot
    if ($depthLegacyResult.ExitCode -ne 0) {
        throw "Pre-fix validator did not accept the depth-cap bypass case."
    }

    Write-Workflow "file-cap" @'
name: file-cap
jobs:
  test:
    steps:
      - run: ./build/file-cap.ps1
'@
    $capFiles = @{
        "build/file-cap.ps1" = (1..65 | ForEach-Object { "Write-Output ./fixtures/reachable-$_.txt" }) -join "`n"
    }
    for ($index = 1; $index -le 65; $index++) {
        $capFiles["fixtures/reachable-$index.txt"] = "safe-$index"
    }
    Write-Files $capFiles
    Sync-TrackedFiles
    $capResult = Invoke-Validator $temporaryRoot
    if ($capResult.ExitCode -eq 0) {
        throw "The structural validator accepted a reachable closure beyond its file cap."
    }
    $capLegacyResult = Invoke-LegacyValidator $temporaryRoot
    if ($capLegacyResult.ExitCode -ne 0) {
        throw "Pre-fix validator did not accept the file-cap bypass case."
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
