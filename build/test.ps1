[CmdletBinding()]
param(
    [string] $Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$solutionPath = Join-Path $repositoryRoot "KeelMatrix.EfGuard.sln"
$previousNodeReuse = $env:MSBUILDDISABLENODEREUSE
$previousTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$env:MSBUILDDISABLENODEREUSE = "1"
$env:KEELMATRIX_NO_TELEMETRY = "1"

function Invoke-Dotnet([string[]] $Arguments) {
    $safeArguments = @($Arguments + @("-m:1", "-nodeReuse:false"))
    & dotnet @safeArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

Push-Location $repositoryRoot
try {
    Invoke-Dotnet @("restore", "KeelMatrix.EfGuard.sln", "--configfile", "NuGet.config")
    Invoke-Dotnet @("build", "KeelMatrix.EfGuard.sln", "--configuration", $Configuration, "--no-restore")
    Invoke-Dotnet @("test", "KeelMatrix.EfGuard.sln", "--configuration", $Configuration, "--no-build", "--no-restore")

    Write-Output "EfGuard build and test passed with MSBuild node reuse disabled and one build node."
}
finally {
    Pop-Location
    if ($null -eq $previousNodeReuse) {
        Remove-Item Env:MSBUILDDISABLENODEREUSE -ErrorAction SilentlyContinue
    }
    else {
        $env:MSBUILDDISABLENODEREUSE = $previousNodeReuse
    }

    if ($null -eq $previousTelemetry) {
        Remove-Item Env:KEELMATRIX_NO_TELEMETRY -ErrorAction SilentlyContinue
    }
    else {
        $env:KEELMATRIX_NO_TELEMETRY = $previousTelemetry
    }
}
