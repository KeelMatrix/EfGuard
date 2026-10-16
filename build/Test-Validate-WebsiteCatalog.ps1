[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"
$validator = Join-Path $PSScriptRoot "Validate-WebsiteCatalog.ps1"
& $validator -RepositoryRoot $RepositoryRoot

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-website-catalog-" + [Guid]::NewGuid().ToString("N"))
$fixtureRoot = Join-Path $temporaryRoot "repository"
$projectDirectory = Join-Path $fixtureRoot "src/KeelMatrix.EfGuard"
New-Item -ItemType Directory -Path $projectDirectory -Force | Out-Null

function New-Fixture(
    [string] $PackageTags,
    [string] $ManifestPackageId = "KeelMatrix.EfGuard",
    [string] $PackageProjectUrl = "https://github.com/KeelMatrix/EfGuard",
    [string] $CommonProjectUrl = "https://github.com/KeelMatrix/EfGuard"
) {
    $packages = [ordered]@{}
    $packages[$ManifestPackageId] = [ordered]@{ visibility = "public-product"; role = "primary" }
    $manifest = [ordered]@{ schemaVersion = 1; packages = $packages } | ConvertTo-Json -Depth 4
    Set-Content -LiteralPath (Join-Path $fixtureRoot "keelmatrix.website.json") -Value $manifest -Encoding utf8

    $project = @"
<Project>
  <PropertyGroup>
    <PackageId>KeelMatrix.EfGuard</PackageId>
    <PackageProjectUrl>$PackageProjectUrl</PackageProjectUrl>
    <PackageTags>$PackageTags</PackageTags>
    <PackAsTool>true</PackAsTool>
    <ToolCommandName>efguard</ToolCommandName>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $projectDirectory "KeelMatrix.EfGuard.csproj") -Value $project -Encoding utf8

    $commonProps = @"
<Project>
  <PropertyGroup>
    <PackageProjectUrl>$CommonProjectUrl</PackageProjectUrl>
    <RepositoryUrl>https://github.com/KeelMatrix/EfGuard</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
  </PropertyGroup>
</Project>
"@
    Set-Content -LiteralPath (Join-Path $fixtureRoot "Directory.Build.props") -Value $commonProps -Encoding utf8
}

function Assert-Rejected(
    [string] $Name,
    [string] $PackageTags,
    [string] $ManifestPackageId,
    [string] $ExpectedMessage,
    [string] $PackageProjectUrl = "https://github.com/KeelMatrix/EfGuard",
    [string] $CommonProjectUrl = "https://github.com/KeelMatrix/EfGuard"
) {
    New-Fixture $PackageTags $ManifestPackageId $PackageProjectUrl $CommonProjectUrl
    $failure = $null
    try { & $validator -RepositoryRoot $fixtureRoot }
    catch { $failure = $_.Exception.Message }
    if ([string]::IsNullOrWhiteSpace($failure)) { throw "Website catalog validator accepted $Name." }
    if ($failure -notmatch [regex]::Escape($ExpectedMessage)) {
        throw "Website catalog validator rejected $Name for the wrong reason: $failure"
    }
    Write-Output "Negative website catalog validation passed: $Name was rejected."
}

function Assert-ProjectUrlRejected([string] $Name, [string] $ProjectUrl, [string] $CommonProjectUrl = "https://github.com/KeelMatrix/EfGuard") {
    $tags = "ef-core;keelmatrix-public-product;keelmatrix-primary"
    Assert-Rejected $Name $tags "KeelMatrix.EfGuard" "PackageProjectUrl" $ProjectUrl $CommonProjectUrl
}

try {
    New-Fixture "ef-core;keelmatrix-public-product;keelmatrix-primary"
    & $validator -RepositoryRoot $fixtureRoot

    Assert-ProjectUrlRejected "the legacy fragment form" "https://github.com/KeelMatrix/EfGuard#readme"
    Assert-ProjectUrlRejected "a query string" "https://github.com/KeelMatrix/EfGuard?readme=true"
    Assert-ProjectUrlRejected "a trailing slash" "https://github.com/KeelMatrix/EfGuard/"
    Assert-ProjectUrlRejected "an insecure scheme" "http://github.com/KeelMatrix/EfGuard"
    Assert-ProjectUrlRejected "a lookalike GitHub host" "https://github.com.evil/KeelMatrix/EfGuard"
    Assert-ProjectUrlRejected "a different repository owner" "https://github.com/Other/EfGuard"
    Assert-ProjectUrlRejected "a different repository name" "https://github.com/KeelMatrix/Other"
    Assert-ProjectUrlRejected "a fragment in common package metadata" "https://github.com/KeelMatrix/EfGuard" "https://github.com/KeelMatrix/EfGuard#readme"

    Assert-Rejected "a visibility mismatch" "ef-core;keelmatrix-internal-package;keelmatrix-primary" "KeelMatrix.EfGuard" "visibility sentinel"
    Assert-Rejected "a role mismatch" "ef-core;keelmatrix-public-product;keelmatrix-component" "KeelMatrix.EfGuard" "role sentinel"
    Assert-Rejected "duplicate visibility sentinels" "ef-core;keelmatrix-public-product;keelmatrix-internal-package;keelmatrix-primary" "KeelMatrix.EfGuard" "exactly one visibility sentinel"
    Assert-Rejected "duplicate role sentinels" "ef-core;keelmatrix-public-product;keelmatrix-primary;keelmatrix-component" "KeelMatrix.EfGuard" "exactly one role sentinel"
    Assert-Rejected "incorrect sentinel casing" "ef-core;KeelMatrix-public-product;keelmatrix-primary" "KeelMatrix.EfGuard" "exactly one visibility sentinel"
    Assert-Rejected "a missing package entry" "ef-core;keelmatrix-public-product;keelmatrix-primary" "KeelMatrix.Other" "exactly one package entry"
}
finally {
    $fullTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $temporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([char[]]@([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar))
    if ([IO.Path]::GetDirectoryName($fullTemporaryRoot) -ine $temporaryParent -or [IO.Path]::GetFileName($fullTemporaryRoot) -notmatch '^efguard-website-catalog-[a-f0-9]{32}$') {
        throw "Refusing to remove unexpected website catalog test path '$fullTemporaryRoot'."
    }
    if (Test-Path -LiteralPath $fullTemporaryRoot) { Remove-Item -LiteralPath $fullTemporaryRoot -Recurse -Force }
}
