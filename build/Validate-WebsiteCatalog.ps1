[CmdletBinding()]
param(
    [string] $RepositoryRoot = (Join-Path $PSScriptRoot "..")
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Website catalog contract failed: $Message"
}

function Get-ExactlyOneValue([xml] $Document, [string] $Name, [string] $Source) {
    $nodes = @($Document.SelectNodes("//*[local-name()='$Name']"))
    if ($nodes.Count -ne 1) { Fail "$Source must define exactly one $Name element." }
    return $nodes[0].InnerText.Trim()
}

function Assert-Equal([string] $Expected, [string] $Actual, [string] $Name) {
    if (-not [StringComparer]::Ordinal.Equals($Expected, $Actual)) {
        Fail "$Name expected '$Expected' but was '$Actual'."
    }
}

$repositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
$manifestPath = Join-Path $repositoryRoot "keelmatrix.website.json"
$projectPath = Join-Path $repositoryRoot "src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj"
$commonPropsPath = Join-Path $repositoryRoot "Directory.Build.props"
foreach ($path in @($manifestPath, $projectPath, $commonPropsPath)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { Fail "required file '$path' is missing." }
}

try {
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -ErrorAction Stop
}
catch {
    Fail "manifest is not valid JSON: $($_.Exception.Message)"
}

$manifestFields = @($manifest.PSObject.Properties | ForEach-Object { $_.Name })
if ($manifestFields.Count -ne 2 -or @($manifestFields | Where-Object { $_ -cnotin @("schemaVersion", "packages") }).Count -ne 0) {
    Fail "manifest must contain only schemaVersion and packages."
}
if ($manifest.schemaVersion -ne 1) { Fail "manifest schemaVersion must be 1." }

$packageEntries = @($manifest.packages.PSObject.Properties)
if ($packageEntries.Count -ne 1 -or $packageEntries[0].Name -cne "KeelMatrix.EfGuard") {
    Fail "manifest must contain exactly one package entry named 'KeelMatrix.EfGuard'."
}
$entry = $packageEntries[0].Value
$entryFields = @($entry.PSObject.Properties | ForEach-Object { $_.Name })
if ($entryFields.Count -ne 2 -or @($entryFields | Where-Object { $_ -cnotin @("visibility", "role") }).Count -ne 0) {
    Fail "package manifest entries may contain only visibility and role."
}
if ($entry.visibility -cne "public-product") { Fail "KeelMatrix.EfGuard visibility must be 'public-product'." }
if ($entry.role -cne "primary") { Fail "KeelMatrix.EfGuard role must be 'primary'." }

try {
    [xml] $project = Get-Content -LiteralPath $projectPath -Raw -ErrorAction Stop
    [xml] $commonProps = Get-Content -LiteralPath $commonPropsPath -Raw -ErrorAction Stop
}
catch {
    Fail "package project metadata is not valid XML: $($_.Exception.Message)"
}

Assert-Equal "KeelMatrix.EfGuard" (Get-ExactlyOneValue $project "PackageId" "package project") "PackageId"
Assert-Equal "https://github.com/KeelMatrix/EfGuard#readme" (Get-ExactlyOneValue $project "PackageProjectUrl" "package project") "PackageProjectUrl"
Assert-Equal "true" ((Get-ExactlyOneValue $project "PackAsTool" "package project").ToLowerInvariant()) "PackAsTool"
Assert-Equal "efguard" (Get-ExactlyOneValue $project "ToolCommandName" "package project") "ToolCommandName"
Assert-Equal "https://github.com/KeelMatrix/EfGuard" (Get-ExactlyOneValue $commonProps "RepositoryUrl" "common package metadata") "RepositoryUrl"
Assert-Equal "git" (Get-ExactlyOneValue $commonProps "RepositoryType" "common package metadata") "RepositoryType"
Assert-Equal "true" ((Get-ExactlyOneValue $commonProps "PublishRepositoryUrl" "common package metadata").ToLowerInvariant()) "PublishRepositoryUrl"

$tagText = Get-ExactlyOneValue $project "PackageTags" "package project"
$tags = @($tagText -split '[;,\s]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
$visibilityNames = @("keelmatrix-public-product", "keelmatrix-internal-package")
$roleNames = @("keelmatrix-primary", "keelmatrix-component")
$visibilityTags = @($tags | Where-Object { $visibilityNames -ccontains $_ })
$roleTags = @($tags | Where-Object { $roleNames -ccontains $_ })
if ($visibilityTags.Count -ne 1) { Fail "PackageTags must contain exactly one visibility sentinel." }
if ($roleTags.Count -ne 1) { Fail "PackageTags must contain exactly one role sentinel." }

$expectedVisibilityTag = if ($entry.visibility -ceq "public-product") { "keelmatrix-public-product" } else { "keelmatrix-internal-package" }
$expectedRoleTag = if ($entry.role -ceq "primary") { "keelmatrix-primary" } else { "keelmatrix-component" }
Assert-Equal $expectedVisibilityTag $visibilityTags[0] "PackageTags visibility sentinel"
Assert-Equal $expectedRoleTag $roleTags[0] "PackageTags role sentinel"

Write-Output "Website catalog contract passed for KeelMatrix.EfGuard."
