[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$compareScript = Join-Path $PSScriptRoot "Compare-ProviderEngineEvidence.ps1"
$shippedPath = Join-Path $repositoryRoot "src/KeelMatrix.EfGuard/ProviderEngineEvidence.json"
$validationRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-engine-evidence-" + [Guid]::NewGuid().ToString("N"))

function Invoke-Compare([string] $Path) {
    $output = & pwsh -NoLogo -NoProfile -File $compareScript -Engine "sqlserver" -RecordedEvidencePath $Path -ShippedEvidencePath $shippedPath 2>&1
    return $LASTEXITCODE
}

try {
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null

    $missingPath = Join-Path $validationRoot "missing.json"
    if ((Invoke-Compare $missingPath) -eq 0) {
        throw "Provider engine evidence validation accepted a missing recorded evidence file."
    }

    $shipped = Get-Content -Raw -LiteralPath $shippedPath | ConvertFrom-Json
    $sqlServerClaims = @($shipped.claims | Where-Object { $_.engine -eq "sqlserver" })
    if ($sqlServerClaims.Count -eq 0) {
        throw "The shipped provider engine evidence has no sqlserver claims to exercise."
    }

    $matchedPath = Join-Path $validationRoot "matched.json"
    [IO.File]::WriteAllText($matchedPath, (@{ version = 1; claims = $sqlServerClaims } | ConvertTo-Json -Depth 6))
    if ((Invoke-Compare $matchedPath) -ne 0) {
        throw "Provider engine evidence validation rejected the shipped evidence."
    }

    $tamperedPath = Join-Path $validationRoot "tampered.json"
    $tamperedClaims = @($sqlServerClaims | Select-Object -Skip 1)
    [IO.File]::WriteAllText($tamperedPath, (@{ version = 1; claims = $tamperedClaims } | ConvertTo-Json -Depth 6))
    if ((Invoke-Compare $tamperedPath) -eq 0) {
        throw "Provider engine evidence validation accepted recorded evidence that does not match the shipped manifest."
    }

    $emptyPath = Join-Path $validationRoot "empty.json"
    [IO.File]::WriteAllText($emptyPath, '{"version":1,"claims":[]}')
    if ((Invoke-Compare $emptyPath) -eq 0) {
        throw "Provider engine evidence validation accepted an empty recorded evidence file."
    }

    Write-Output "Provider engine evidence validation contract coverage passed."
}
finally {
    if (Test-Path -LiteralPath $validationRoot) {
        Remove-Item -LiteralPath $validationRoot -Recurse -Force
    }
}

exit 0
