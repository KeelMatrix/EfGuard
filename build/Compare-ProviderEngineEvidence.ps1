[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Engine,

    [Parameter(Mandatory = $true)]
    [string] $RecordedEvidencePath,

    [string] $ShippedEvidencePath = ""
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
if ([string]::IsNullOrWhiteSpace($ShippedEvidencePath)) {
    $ShippedEvidencePath = Join-Path $repositoryRoot "src/KeelMatrix.EfGuard/ProviderEngineEvidence.json"
}

function Get-ClaimKeys([string] $Path, [string] $EngineName) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return @()
    }

    $document = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
    return @($document.claims |
        Where-Object { $_.engine -eq $EngineName } |
        ForEach-Object { "$($_.rule)|$($_.provider)|$($_.behavior)|$($_.verifiedBy)" } |
        Sort-Object)
}

$recorded = @(Get-ClaimKeys $RecordedEvidencePath $Engine)
if ($recorded.Count -eq 0) {
    throw "No recorded $Engine engine evidence was produced. The real-engine integration tests must run against a live $Engine engine before this gate can pass."
}

$shipped = @(Get-ClaimKeys $ShippedEvidencePath $Engine)
$difference = @(Compare-Object -ReferenceObject $shipped -DifferenceObject $recorded)
if ($difference.Count -ne 0) {
    Write-Output "Shipped claims:"
    $shipped | ForEach-Object { Write-Output "  $_" }
    Write-Output "Recorded claims:"
    $recorded | ForEach-Object { Write-Output "  $_" }
    throw "The recorded $Engine engine evidence does not match the shipped provider engine evidence manifest. Update src/KeelMatrix.EfGuard/ProviderEngineEvidence.json so it contains exactly the engine-verified claims."
}

Write-Output "Recorded $Engine engine evidence matches the shipped provider engine evidence manifest ($($recorded.Count) claims)."
exit 0
