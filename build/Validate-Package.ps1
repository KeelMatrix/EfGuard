[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageDirectory,

    [string] $ExpectedVersion = "0.1.0",
    [string] $ExpectedCommit
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Package contract failed: $Message"
}

function Assert-Equal([object] $Expected, [object] $Actual, [string] $Name) {
    if ([string]$Expected -ne [string]$Actual) {
        Fail "$Name expected '$Expected' but was '$Actual'."
    }
}

function Assert-True([bool] $Condition, [string] $Message) {
    if (-not $Condition) { Fail $Message }
}

function Get-ArchiveEntry([System.IO.Compression.ZipArchive] $Archive, [string] $Name) {
    $entry = $Archive.GetEntry($Name)
    if ($null -eq $entry) { Fail "required archive entry '$Name' is missing from '$($Archive.Name)'." }
    return $entry
}

function Read-EntryText([System.IO.Compression.ZipArchive] $Archive, [string] $Name) {
    $entry = Get-ArchiveEntry $Archive $Name
    $reader = [System.IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}

function Read-EntryBytes([System.IO.Compression.ZipArchive] $Archive, [string] $Name) {
    $entry = Get-ArchiveEntry $Archive $Name
    $stream = $entry.Open()
    try {
        $memory = [System.IO.MemoryStream]::new()
        try {
            $stream.CopyTo($memory)
            return $memory.ToArray()
        }
        finally { $memory.Dispose() }
    }
    finally { $stream.Dispose() }
}

function Read-BigEndianUInt32([byte[]] $Bytes, [int] $Offset) {
    return ([uint32]$Bytes[$Offset] -shl 24) -bor ([uint32]$Bytes[$Offset + 1] -shl 16) -bor ([uint32]$Bytes[$Offset + 2] -shl 8) -bor $Bytes[$Offset + 3]
}

function Assert-ExactArchiveEntries([System.IO.Compression.ZipArchive] $Archive, [string[]] $Expected, [string] $PackageName) {
    $actual = @($Archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') } | Sort-Object)
    $expectedSorted = @($Expected | Sort-Object)
    $differences = @(Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $actual)
    if ($differences.Count -ne 0) {
        $actualText = if ($actual.Count -eq 0) { '(none)' } else { $actual -join ', ' }
        $expectedText = $expectedSorted -join ', '
        Fail "archive entry allowlist failed for '$PackageName'. Expected: $expectedText. Actual: $actualText."
    }
}

function Invoke-Dotnet([string[]] $Arguments) {
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { Fail "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE." }
}

$packageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if (-not (Test-Path -LiteralPath $packageDirectory -PathType Container)) {
    Fail "package directory '$packageDirectory' does not exist."
}

if ([string]::IsNullOrWhiteSpace($ExpectedCommit)) {
    $ExpectedCommit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "could not determine the expected repository commit." }
}

$expectedNames = @(
    "KeelMatrix.EfGuard.$ExpectedVersion.nupkg",
    "KeelMatrix.EfGuard.$ExpectedVersion.snupkg"
) | Sort-Object
$actualFiles = @(Get-ChildItem -LiteralPath $packageDirectory -File | Select-Object -ExpandProperty Name | Sort-Object)
if (@(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualFiles).Count -ne 0) {
    Fail "expected exactly '$($expectedNames -join ', ')' but found '$($actualFiles -join ', ')'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$nupkgPath = Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.nupkg"
$snupkgPath = Join-Path $packageDirectory "KeelMatrix.EfGuard.$ExpectedVersion.snupkg"
$nupkg = [IO.Compression.ZipFile]::OpenRead($nupkgPath)
try {
    $entryNames = @($nupkg.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    Assert-ExactArchiveEntries $nupkg @(
        "_rels/.rels",
        "[Content_Types].xml",
        "icon.png",
        "KeelMatrix.EfGuard.nuspec",
        "LICENSE",
        "package/services/metadata/core-properties/nuget.psmdcp",
        "README.md",
        "tools/net8.0/any/DotnetToolSettings.xml",
        "tools/net8.0/any/KeelMatrix.EfGuard.deps.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.dll",
        "tools/net8.0/any/KeelMatrix.EfGuard.pdb",
        "tools/net8.0/any/KeelMatrix.EfGuard.runtimeconfig.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.runtimeconfig.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.xml",
        "tools/net8.0/any/KeelMatrix.Telemetry.dll",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.runtimeconfig.json",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.runtimeconfig.json"
    ) "$(Split-Path -Leaf $nupkgPath)"

    foreach ($entryName in $entryNames | Where-Object { $_ -match '(?i)\.(?:nuspec|md|txt|json|xml|props|targets|cs|csproj)$' }) {
        $content = Read-EntryText $nupkg $entryName
        if ($content -match '(?i)(?:password|api[_-]?key|secret)\s*=|BEGIN (?:RSA|OPENSSH|EC) PRIVATE KEY|ghp_[A-Za-z0-9]+') {
            Fail "secret or prohibited text found in '$entryName'."
        }
    }

    foreach ($required in @(
        "README.md",
        "LICENSE",
        "icon.png",
        "tools/net8.0/any/DotnetToolSettings.xml",
        "tools/net8.0/any/KeelMatrix.EfGuard.dll",
        "tools/net8.0/any/KeelMatrix.EfGuard.pdb",
        "tools/net8.0/any/KeelMatrix.EfGuard.deps.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.runtimeconfig.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/KeelMatrix.EfGuard.Worker.runtimeconfig.json",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/net10.0/KeelMatrix.EfGuard.Worker.runtimeconfig.json",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.dll",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.deps.json",
        "tools/net8.0/any/net9.0/KeelMatrix.EfGuard.Worker.runtimeconfig.json"
    )) {
        Assert-True ($entryNames -contains $required) "required package asset '$required' is missing."
    }

    $iconBytes = Read-EntryBytes $nupkg "icon.png"
    Assert-Equal 111752 $iconBytes.Length "icon.png size"
    Assert-True ($iconBytes[0] -eq 137 -and $iconBytes[1] -eq 80 -and $iconBytes[2] -eq 78 -and $iconBytes[3] -eq 71) "icon.png has an invalid PNG signature."
    $iconWidth = Read-BigEndianUInt32 $iconBytes 16
    $iconHeight = Read-BigEndianUInt32 $iconBytes 20
    Assert-Equal 512 $iconWidth "icon width"
    Assert-Equal 512 $iconHeight "icon height"

    $toolSettings = Read-EntryText $nupkg "tools/net8.0/any/DotnetToolSettings.xml"
    Assert-True ($toolSettings -match 'Command Name="efguard"') "tool metadata does not declare the efguard command."

    [xml] $nuspec = Read-EntryText $nupkg "KeelMatrix.EfGuard.nuspec"
    $metadata = $nuspec.package.metadata
    if ($null -eq $metadata) { Fail "nuspec metadata is missing." }
    Assert-Equal "KeelMatrix.EfGuard" $metadata.id "package ID"
    Assert-Equal $ExpectedVersion $metadata.version "package version"
    Assert-Equal "KeelMatrix" $metadata.authors "authors"
    Assert-Equal "Detect EF Core migrations that can break rolling deployments, lose data, or block production traffic, and explain a safer rollout before merge." $metadata.description "description"
    Assert-Equal "ef-core entity-framework database-migrations zero-downtime rolling-deployment ci sql-server postgresql dotnet-tool" $metadata.tags "tags"
    Assert-Equal "MIT" $metadata.license.InnerText "license"
    Assert-Equal "README.md" $metadata.readme "README metadata"
    Assert-Equal "icon.png" $metadata.icon "icon metadata"
    Assert-Equal "https://github.com/KeelMatrix/EfGuard" $metadata.repository.url "repository URL"
    Assert-Equal $ExpectedCommit $metadata.repository.commit "repository commit"
    Assert-Equal "DotnetTool" $metadata.packageTypes.packageType.name "tool package type"

    $dependencyGroups = @($metadata.dependencies.group)
    Assert-Equal 1 $dependencyGroups.Count "dependency group count"
    Assert-Equal "net8.0" $dependencyGroups[0].targetFramework "dependency target framework"
    $dependencies = @($dependencyGroups[0].dependency)
    Assert-Equal 1 $dependencies.Count "dependency count"
    Assert-Equal "KeelMatrix.Telemetry" $dependencies[0].id "dependency ID"
    Assert-Equal "0.1.0" $dependencies[0].version "dependency version"
}
finally { $nupkg.Dispose() }

$snupkg = [IO.Compression.ZipFile]::OpenRead($snupkgPath)
try {
    $symbolNames = @($snupkg.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    Assert-ExactArchiveEntries $snupkg @(
        "_rels/.rels",
        "[Content_Types].xml",
        "KeelMatrix.EfGuard.nuspec",
        "package/services/metadata/core-properties/nuget.psmdcp",
        "tools/net8.0/any/KeelMatrix.EfGuard.pdb"
    ) "$(Split-Path -Leaf $snupkgPath)"
    Assert-True (@($symbolNames | Where-Object { $_ -match '(?i)\.pdb$' }).Count -gt 0) "symbol package does not contain PDB files."
    Assert-True (@($symbolNames | Where-Object { $_ -match '(?i)\.(?:dll|deps\.json|runtimeconfig\.json)$' }).Count -eq 0) "symbol package contains runtime assets."
}
finally { $snupkg.Dispose() }

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repeatRoot = Join-Path ([IO.Path]::GetTempPath()) ("efguard-repeat-pack-" + [Guid]::NewGuid().ToString("N"))
$repeatOne = Join-Path $repeatRoot "one"
$repeatTwo = Join-Path $repeatRoot "two"
try {
    New-Item -ItemType Directory -Path $repeatOne, $repeatTwo -Force | Out-Null
    $packArguments = @(
        "pack",
        (Join-Path $repositoryRoot "src/KeelMatrix.EfGuard/KeelMatrix.EfGuard.csproj"),
        "--configuration", "Release",
        "--no-build",
        "--no-restore",
        "--include-symbols",
        "--p:SymbolPackageFormat=snupkg"
    )
    Push-Location $repositoryRoot
    try {
        Invoke-Dotnet @($packArguments + @("--output", $repeatOne))
        Invoke-Dotnet @($packArguments + @("--output", $repeatTwo))
    }
    finally {
        Pop-Location
    }

    foreach ($packageName in @(
        "KeelMatrix.EfGuard.$ExpectedVersion.nupkg",
        "KeelMatrix.EfGuard.$ExpectedVersion.snupkg"
    )) {
        $firstPath = Join-Path $repeatOne $packageName
        $secondPath = Join-Path $repeatTwo $packageName
        Assert-True (Test-Path -LiteralPath $firstPath -PathType Leaf) "repeat-pack output '$packageName' is missing from the first pack."
        Assert-True (Test-Path -LiteralPath $secondPath -PathType Leaf) "repeat-pack output '$packageName' is missing from the second pack."
        $firstHash = (Get-FileHash -LiteralPath $firstPath -Algorithm SHA256).Hash
        $secondHash = (Get-FileHash -LiteralPath $secondPath -Algorithm SHA256).Hash
        Assert-Equal $firstHash $secondHash "repeat-pack SHA-256 for $packageName"
        Write-Output "Repeat-pack hash passed for $packageName ($firstHash)."
    }
}
finally {
    if (Test-Path -LiteralPath $repeatRoot) { Remove-Item -LiteralPath $repeatRoot -Recurse -Force }
}

Write-Output "Package contract passed for KeelMatrix.EfGuard $ExpectedVersion at commit $ExpectedCommit."
