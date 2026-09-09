[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [string] $Version = "0.1.0",
    [string] $WorkingDirectory = ""
)

$ErrorActionPreference = "Stop"
$packageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
$repositoryRoot = if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..")) } else { [IO.Path]::GetFullPath($WorkingDirectory) }
$validationRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-consumer-" + [Guid]::NewGuid().ToString("N"))
$oldTelemetry = $env:KEELMATRIX_NO_TELEMETRY
$oldNugetPackages = $env:NUGET_PACKAGES
$env:KEELMATRIX_NO_TELEMETRY = "1"

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

function Get-TelemetryFiles {
    $bases = @(
        [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::ApplicationData),
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile),
        [IO.Path]::GetTempPath()
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique

    foreach ($base in $bases) {
        $root = Join-Path $base "KeelMatrix/efguard"
        if (Test-Path -LiteralPath $root) {
            Get-ChildItem -LiteralPath $root -Recurse -Force -File -ErrorAction SilentlyContinue |
                Select-Object -ExpandProperty FullName
        }
    }
}

function Invoke-InstalledTool([string] $Tool, [string[]] $Arguments) {
    $stdoutPath = Join-Path $validationRoot ([Guid]::NewGuid().ToString("N") + ".stdout")
    $stderrPath = Join-Path $validationRoot ([Guid]::NewGuid().ToString("N") + ".stderr")
    Push-Location $repositoryRoot
    try {
        & $Tool @Arguments 1> $stdoutPath 2> $stderrPath
        $exitCode = $LASTEXITCODE
    }
    finally { Pop-Location }
    [pscustomobject]@{
        ExitCode = $exitCode
        StandardOutput = Get-Content -Raw $stdoutPath -ErrorAction SilentlyContinue
        StandardError = Get-Content -Raw $stderrPath -ErrorAction SilentlyContinue
    }
}

try {
    if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) { throw "package directory '$packageDirectory' does not exist." }
    New-Item -ItemType Directory -Path $validationRoot -Force | Out-Null
    $packagesRoot = Join-Path $validationRoot "nuget-packages"
    $installRoot = Join-Path $validationRoot "tool"
    $configPath = Join-Path $validationRoot "NuGet.config"
    $escapedPackageDirectory = [Security.SecurityElement]::Escape($packageDirectory)
    $config = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-efguard" value="$escapedPackageDirectory" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-efguard">
      <package pattern="KeelMatrix.EfGuard" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
"@
    [IO.File]::WriteAllText($configPath, $config)
    New-Item -ItemType Directory -Path $packagesRoot -Force | Out-Null
    $env:NUGET_PACKAGES = $packagesRoot
    $telemetryBefore = @(Get-TelemetryFiles)

    Invoke-Dotnet @("tool", "install", "KeelMatrix.EfGuard", "--tool-path", $installRoot, "--version", $Version, "--configfile", $configPath, "--no-cache", "--verbosity", "quiet")
    $tool = Get-ChildItem -LiteralPath $installRoot -File | Where-Object { $_.BaseName -eq "efguard" } | Select-Object -First 1
    if ($null -eq $tool) { throw "isolated tool install did not create the efguard command." }
    $localPackagePath = Join-Path $packageDirectory "KeelMatrix.EfGuard.$Version.nupkg"
    $installedPackage = Get-ChildItem -LiteralPath (Join-Path (Join-Path $installRoot ".store") (Join-Path "keelmatrix.efguard" $Version)) -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -ieq "KeelMatrix.EfGuard.nupkg" } |
        Select-Object -First 1
    if ($null -eq $installedPackage) { throw "isolated tool store does not contain the installed EfGuard package." }
    $localHash = (Get-FileHash -LiteralPath $localPackagePath -Algorithm SHA256).Hash
    $installedHash = (Get-FileHash -LiteralPath $installedPackage.FullName -Algorithm SHA256).Hash
    if ($localHash -ne $installedHash) { throw "installed EfGuard package does not match the freshly built local artifact." }

    $clean = Invoke-InstalledTool $tool.FullName @("check", "--project", "fixtures/Ef8Clean/Ef8CleanFixture.csproj", "--format", "json")
    if ($clean.ExitCode -ne 0) { throw "installed-package clean smoke returned exit code $($clean.ExitCode): $($clean.StandardError)" }
    $cleanReport = $clean.StandardOutput | ConvertFrom-Json
    if ($cleanReport.summary.exitCode -ne 0) { throw "installed-package clean smoke report did not have exit code 0." }

    $baseline = Invoke-InstalledTool $tool.FullName @("check", "--project", "fixtures/Ef8/Ef8Fixture.csproj", "--baseline", "HEAD", "--format", "json")
    if ($baseline.ExitCode -ne 0) { throw "installed-package baseline smoke returned exit code $($baseline.ExitCode): $($baseline.StandardError)" }
    $baselineReport = $baseline.StandardOutput | ConvertFrom-Json
    if (-not $baselineReport.baseline.available) { throw "installed-package baseline smoke did not load the requested Git reference." }

    $telemetryAfter = @(Get-TelemetryFiles)
    $newTelemetryFiles = @($telemetryAfter | Where-Object { $telemetryBefore -notcontains $_ })
    if ($newTelemetryFiles.Count -gt 0) { throw "telemetry state changed during suppressed consumer validation." }
    Write-Output "Isolated package consumer validation passed using local EfGuard source mapping and NuGet.org dependency mapping."
}
finally {
    $env:KEELMATRIX_NO_TELEMETRY = $oldTelemetry
    $env:NUGET_PACKAGES = $oldNugetPackages
    if (Test-Path -LiteralPath $validationRoot) { Remove-Item -LiteralPath $validationRoot -Recurse -Force }
}
