[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $ExpectedVersion,

    [Parameter(Mandatory = $true)]
    [string] $ExpectedCommit,

    [string] $ChangelogPath = "CHANGELOG.md",
    [string] $ExpectedPackageVersion,
    [string] $ExpectedDependencyVersion,
    [string] $RepositoryRoot,
    [string[]] $InstallExamplePath = @("src/KeelMatrix.EfGuard/README.md"),
    [string[]] $ReleaseFacingDocumentationPath = @("README.md"),
    [switch] $RequireChangelogInCommit
)

$ErrorActionPreference = "Stop"

function Fail([string] $Message) {
    throw "Changelog contract failed: $Message"
}

function Get-GitOutput([string[]] $Arguments) {
    $output = & git -C $script:RepositoryRoot @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = (@($output) | ForEach-Object { $_.ToString() }) -join " "
        Fail "git $($Arguments -join ' ') failed: $details"
    }

    return ((@($output) | ForEach-Object { $_.ToString() }) -join "`n").Trim()
}

function Resolve-RepositoryPath([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }

    return [IO.Path]::GetFullPath((Join-Path $script:RepositoryRoot $Path))
}

function Read-XmlFile([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }

    try {
        return [xml](Get-Content -LiteralPath $Path -Raw -Encoding UTF8)
    }
    catch {
        Fail "repository metadata file '$Path' is not valid XML."
    }

    return $null
}

function Get-AttributeValue([System.Xml.XmlElement] $Element, [string] $Name) {
    $attribute = $Element.Attributes[$Name]
    if ($null -eq $attribute) {
        return $null
    }

    return $attribute.Value.Trim()
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
}

$script:RepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $script:RepositoryRoot -PathType Container)) {
    Fail "repository root '$script:RepositoryRoot' does not exist."
}

if ([string]::IsNullOrWhiteSpace($ExpectedPackageVersion)) {
    $ExpectedPackageVersion = $ExpectedVersion
}

if ([string]::IsNullOrWhiteSpace($ExpectedDependencyVersion)) {
    $ExpectedDependencyVersion = $ExpectedVersion
}

$currentCommit = Get-GitOutput @("rev-parse", "--verify", "HEAD")
$expectedCommitObject = Get-GitOutput @("rev-parse", "--verify", ($ExpectedCommit + "^{commit}"))
if ($currentCommit -ne $expectedCommitObject) {
    Fail "checked-out commit '$currentCommit' does not match expected release commit '$ExpectedCommit'."
}

if ($ExpectedPackageVersion -ne $ExpectedVersion) {
    Fail "release version '$ExpectedVersion' does not match expected package version '$ExpectedPackageVersion'."
}

$changelogFullPath = Resolve-RepositoryPath $ChangelogPath
if (-not (Test-Path -LiteralPath $changelogFullPath -PathType Leaf)) {
    Fail "changelog '$changelogFullPath' does not exist."
}

if ($RequireChangelogInCommit) {
    $relativeChangelogPath = [IO.Path]::GetRelativePath($script:RepositoryRoot, $changelogFullPath).Replace('\', '/')
    if ($relativeChangelogPath -eq ".." -or $relativeChangelogPath.StartsWith("../", [StringComparison]::Ordinal)) {
        Fail "changelog '$changelogFullPath' is outside repository root '$script:RepositoryRoot'."
    }

    [void](Get-GitOutput @("ls-files", "--error-unmatch", "--", $relativeChangelogPath))
    $commitBlob = Get-GitOutput @("rev-parse", "--verify", ($expectedCommitObject + ":" + $relativeChangelogPath))
    $workingBlob = Get-GitOutput @("hash-object", ("--path=" + $relativeChangelogPath), $changelogFullPath)
    if ($commitBlob -ne $workingBlob) {
        Fail "changelog '$relativeChangelogPath' does not match the exact checked-out release commit '$currentCommit'."
    }
}

$changelogLines = @(Get-Content -LiteralPath $changelogFullPath -Encoding UTF8)
$headingRecords = @()
$headingStack = [System.Collections.Generic.List[object]]::new()
for ($index = 0; $index -lt $changelogLines.Count; $index++) {
    $headingMatch = [regex]::Match($changelogLines[$index], '^(?<hashes>#{1,6})[ \t]+(?<text>.*?)[ \t]*#*[ \t]*$')
    if ($headingMatch.Success) {
        $headingLevel = $headingMatch.Groups["hashes"].Value.Length
        while ($headingStack.Count -gt 0 -and $headingStack[$headingStack.Count - 1].Level -ge $headingLevel) {
            $headingStack.RemoveAt($headingStack.Count - 1)
        }

        $headingRecord = [pscustomobject]@{
            Level = $headingLevel
            Text = $headingMatch.Groups["text"].Value.Trim()
            Line = $index + 1
            EnclosingHeading = if ($headingStack.Count -gt 0) { $headingStack[$headingStack.Count - 1] } else { $null }
        }
        $headingRecords += $headingRecord
        $headingStack.Add($headingRecord)
    }
}

$versionToken = "\[" + [regex]::Escape($ExpectedVersion) + "\]"
$targetHeadings = @($headingRecords | Where-Object { $_.Text -match $versionToken })
if ($targetHeadings.Count -eq 0) {
    Fail "target release version '$ExpectedVersion' is absent from '$ChangelogPath'."
}
if ($targetHeadings.Count -gt 1) {
    Fail "target release version '$ExpectedVersion' has multiple release headings."
}

$targetHeading = $targetHeadings[0]
if ($targetHeading.Level -ne 2) {
    Fail "target release version '$ExpectedVersion' must be a level-two release heading."
}

$preReleaseWording = '(?i)(?<![A-Za-z])(?:planned|unreleased|unpublished|not[ \t]+(?:yet[ \t]+)?published|not[ \t]+released|to[ \t]+be[ \t]+(?:published|released)|tbd|upcoming|draft|pending|pre[ -]?release|work[ \t]+in[ \t]+progress|future[ \t]+release)(?![A-Za-z])'
$enclosingHeading = $targetHeading.EnclosingHeading
if ($null -ne $enclosingHeading -and $enclosingHeading.Text -match $preReleaseWording) {
    Fail "target release version '$ExpectedVersion' is nested inside pre-release section '$($enclosingHeading.Text)' on line $($enclosingHeading.Line)."
}

if ($targetHeading.Text -match $preReleaseWording) {
    Fail "target release heading on line $($targetHeading.Line) still contains pre-release wording."
}

$dateMatches = @([regex]::Matches($targetHeading.Text, '(?<!\d)(?<date>\d{4}-\d{2}-\d{2})(?!\d)'))
if ($dateMatches.Count -eq 0) {
    Fail "target release heading on line $($targetHeading.Line) has no ISO release date."
}
if ($dateMatches.Count -gt 1) {
    Fail "target release heading on line $($targetHeading.Line) contains multiple release dates."
}

$dateText = $dateMatches[0].Groups["date"].Value
$releaseDate = [DateTime]::MinValue
$validDate = [DateTime]::TryParseExact(
    $dateText,
    "yyyy-MM-dd",
    [Globalization.CultureInfo]::InvariantCulture,
    [Globalization.DateTimeStyles]::None,
    [ref] $releaseDate)
if (-not $validDate) {
    Fail "release date '$dateText' is invalid."
}
if ($releaseDate.Date -gt [DateTime]::UtcNow.Date) {
    Fail "release date '$dateText' is later than the current UTC date."
}

# The lowest semantic-versioned level-two entry is the initial public release.
# This narrow automated backstop applies only to that entry: it enforces the
# first-release Added-only shape and catches obvious remediation-history
# wording, while the required human editorial review remains authoritative.
$targetSectionEndLine = $changelogLines.Count + 1
$nextReleaseHeading = @($headingRecords |
    Where-Object {
        $_.Line -gt $targetHeading.Line -and
        $_.Level -le $targetHeading.Level
    } |
    Sort-Object Line |
    Select-Object -First 1)
if ($nextReleaseHeading.Count -gt 0) {
    $targetSectionEndLine = $nextReleaseHeading[0].Line
}

$versionedReleaseHeadings = @($headingRecords |
    Where-Object { $_.Level -eq 2 } |
    ForEach-Object {
        $versionMatch = [regex]::Match($_.Text, '\[(?<version>\d+\.\d+\.\d+)\]')
        if ($versionMatch.Success) {
            [pscustomobject]@{
                Heading = $_
                Version = [Version]::Parse($versionMatch.Groups["version"].Value)
            }
        }
    })
$targetVersion = [Version]::Parse($ExpectedVersion)
$earlierReleaseCount = @($versionedReleaseHeadings | Where-Object { $_.Version -lt $targetVersion }).Count
$isInitialPublicRelease = $earlierReleaseCount -eq 0

if ($isInitialPublicRelease) {
    $entryCategoryHeadings = @($headingRecords |
        Where-Object {
            $_.Line -gt $targetHeading.Line -and
            $_.Line -lt $targetSectionEndLine -and
            $_.Level -eq ($targetHeading.Level + 1)
        } |
        Sort-Object Line)
    $nonAddedCategories = @($entryCategoryHeadings | Where-Object { $_.Text -notmatch '(?i)^Added$' })
    if ($entryCategoryHeadings.Count -eq 0) {
        Fail "initial public release '$ExpectedVersion' must contain an Added section and no other release categories."
    }
    if ($entryCategoryHeadings.Count -ne 1 -or $nonAddedCategories.Count -gt 0) {
        $categories = @($entryCategoryHeadings | ForEach-Object { "'$($_.Text)' on line $($_.Line)" }) -join ', '
        Fail "initial public release '$ExpectedVersion' must contain only one Added section; found $categories."
    }

    $remediationMarkerPattern = '(?i)(?<![A-Za-z])(?:now|no[ \t]+longer|previously|formerly|used[ \t]+to|fixed|fixes|corrected|resolved|addressed|this[ \t]+removes|this[ \t]+fixes|changed[ \t]+from)(?![A-Za-z])'
    $remediationMarkers = @()
    for ($lineIndex = $targetHeading.Line - 1; $lineIndex -lt ($targetSectionEndLine - 1); $lineIndex++) {
        foreach ($markerMatch in @([regex]::Matches($changelogLines[$lineIndex], $remediationMarkerPattern))) {
            $remediationMarkers += [pscustomobject]@{
                Marker = $markerMatch.Value
                Line = $lineIndex + 1
            }
        }
    }
    if ($remediationMarkers.Count -gt 0) {
        $markerSummary = @($remediationMarkers | ForEach-Object { "'$($_.Marker)' on line $($_.Line)" } | Select-Object -Unique) -join ', '
        Fail "initial public release '$ExpectedVersion' contains remediation-history marker(s): $markerSummary."
    }
}

$versionDeclarations = @()
$versionFiles = @()
$buildPropsPath = Join-Path $script:RepositoryRoot "Directory.Build.props"
if (Test-Path -LiteralPath $buildPropsPath -PathType Leaf) {
    $versionFiles += $buildPropsPath
}
$sourceRoot = Join-Path $script:RepositoryRoot "src"
if (Test-Path -LiteralPath $sourceRoot -PathType Container) {
    $versionFiles += @(Get-ChildItem -LiteralPath $sourceRoot -Filter "*.csproj" -File -Recurse | Select-Object -ExpandProperty FullName)
}

foreach ($versionFile in @($versionFiles | Select-Object -Unique)) {
    $versionXml = Read-XmlFile $versionFile
    if ($null -eq $versionXml) {
        continue
    }

    foreach ($versionNode in @($versionXml.SelectNodes("//*[local-name()='Version' or local-name()='PackageVersion' or local-name()='VersionPrefix']"))) {
        $value = $versionNode.InnerText.Trim()
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match '\$\(|\)') {
            continue
        }
        if ($value -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
            Fail "repository package version declaration '$value' in '$versionFile' is not a concrete semantic version."
        }
        $versionDeclarations += $value
    }
}

$distinctPackageVersions = @($versionDeclarations | Select-Object -Unique)
if ($distinctPackageVersions.Count -eq 0) {
    Fail "no concrete package version declaration was found in Directory.Build.props or source project files."
}
if ($distinctPackageVersions.Count -gt 1) {
    Fail "repository package version declarations disagree: $($distinctPackageVersions -join ', ')."
}
if ($distinctPackageVersions.Count -eq 1 -and $distinctPackageVersions[0] -ne $ExpectedPackageVersion) {
    Fail "repository-declared package version '$($distinctPackageVersions[0])' does not match expected package version '$ExpectedPackageVersion'."
}

$packableProjects = @()
if (Test-Path -LiteralPath $sourceRoot -PathType Container) {
    $packableProjects = @(Get-ChildItem -LiteralPath $sourceRoot -Filter "*.csproj" -File -Recurse)
}
$centralPackagesPath = Join-Path $script:RepositoryRoot "Directory.Packages.props"
$centralPackages = Read-XmlFile $centralPackagesPath
$dependencyVersions = @{}

foreach ($projectFile in $packableProjects) {
    $projectXml = Read-XmlFile $projectFile.FullName
    if ($null -eq $projectXml) {
        continue
    }

    $packAsTool = @($projectXml.SelectNodes("//*[local-name()='PackAsTool']") | Where-Object { $_.InnerText.Trim() -eq "true" }).Count -gt 0
    $packageId = @($projectXml.SelectNodes("//*[local-name()='PackageId']") | Select-Object -First 1 | ForEach-Object { $_.InnerText.Trim() })
    if (-not $packAsTool -and $packageId.Count -eq 0) {
        continue
    }

    foreach ($reference in @($projectXml.SelectNodes("//*[local-name()='PackageReference']"))) {
        $dependencyId = Get-AttributeValue $reference "Include"
        if ([string]::IsNullOrWhiteSpace($dependencyId) -or -not $dependencyId.StartsWith("KeelMatrix.", [StringComparison]::Ordinal)) {
            continue
        }

        $dependencyVersion = Get-AttributeValue $reference "Version"
        if ([string]::IsNullOrWhiteSpace($dependencyVersion) -and $null -ne $centralPackages) {
            $centralNode = @($centralPackages.SelectNodes("//*[local-name()='PackageVersion']") | Where-Object { (Get-AttributeValue $_ "Include") -eq $dependencyId } | Select-Object -First 1)
            if ($centralNode.Count -gt 0) {
                $dependencyVersion = Get-AttributeValue $centralNode[0] "Version"
            }
        }

        if ([string]::IsNullOrWhiteSpace($dependencyVersion) -or $dependencyVersion -match '\$\(') {
            Fail "KeelMatrix dependency '$dependencyId' in '$($projectFile.FullName)' has no concrete version that can be checked."
        }
        if ($dependencyVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
            Fail "KeelMatrix dependency '$dependencyId' has invalid version '$dependencyVersion'."
        }
        $dependencyVersions[$dependencyId] = $dependencyVersion
    }
}

foreach ($dependencyId in @($dependencyVersions.Keys)) {
    if ($dependencyVersions[$dependencyId] -ne $ExpectedDependencyVersion) {
        Fail "KeelMatrix dependency '$dependencyId' version '$($dependencyVersions[$dependencyId])' does not match expected release dependency version '$ExpectedDependencyVersion'."
    }
}

$installVersionPattern = '--version(?:\s+|=)(?:"(?<version>[^"]+)"|''(?<version>[^'']+)''|(?<version>[^\s]+))'
$toolInstallPattern = '(?i)\bdotnet\s+tool\s+install\b'
$optionsWithValues = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($option in @('--add-source', '--configfile', '--framework', '--arch', '--runtime', '--tool-path', '--verbosity', '--version')) {
    [void]$optionsWithValues.Add($option)
}

function Get-DotnetToolInstallCommands([string] $Path) {
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    $commands = @()
    for ($lineIndex = 0; $lineIndex -lt $lines.Count; $lineIndex++) {
        $installText = $lines[$lineIndex]
        if ($installText -notmatch $toolInstallPattern) {
            continue
        }

        $firstLineIndex = $lineIndex
        $lastLineIndex = $lineIndex
        while ($lastLineIndex + 1 -lt $lines.Count) {
            $currentLine = $lines[$lastLineIndex].TrimEnd()
            $nextLine = $lines[$lastLineIndex + 1]
            $hasLineContinuation = $currentLine.EndsWith('\') -or $currentLine.EndsWith('`')
            $hasOptionContinuation = $nextLine -match '^\s+--(?:version|add-source|configfile|tool-path|ignore-failed-sources)\b'
            if (-not $hasLineContinuation -and -not $hasOptionContinuation) {
                break
            }

            $installText += "`n" + $nextLine
            $lastLineIndex++
        }
        $lineIndex = $lastLineIndex

        $commandMatch = [regex]::Match($installText, '(?is)\bdotnet\s+tool\s+install\b(?<arguments>.*)$')
        $packageId = $null
        $skipNextToken = $false
        if ($commandMatch.Success) {
            $argumentMatches = [regex]::Matches($commandMatch.Groups["arguments"].Value, '(?:"(?<quoted>[^"]+)"|''(?<quoted>[^'']+)''|(?<bare>\S+))')
            foreach ($argumentMatch in $argumentMatches) {
                $token = if ($argumentMatch.Groups["quoted"].Success) { $argumentMatch.Groups["quoted"].Value } else { $argumentMatch.Groups["bare"].Value }
                if ($token -in @('\', '`')) {
                    continue
                }
                if ($skipNextToken) {
                    $skipNextToken = $false
                    continue
                }
                if ($token.StartsWith('--', [StringComparison]::Ordinal)) {
                    $optionName = $token.Split('=', 2)[0]
                    if ($optionsWithValues.Contains($optionName) -and -not $token.Contains('=')) {
                        $skipNextToken = $true
                    }
                    continue
                }
                if ($token.StartsWith('-', [StringComparison]::Ordinal)) {
                    continue
                }
                if ($null -eq $packageId) {
                    $packageId = $token
                }
            }
        }

        $commands += [pscustomobject]@{
            Line = $firstLineIndex + 1
            Text = $installText
            PackageId = $packageId
            VersionMatches = @([regex]::Matches($installText, $installVersionPattern))
        }
    }

    return $commands
}

$canonicalInstallVersion = $null
foreach ($examplePath in @($InstallExamplePath)) {
    $exampleFullPath = Resolve-RepositoryPath $examplePath
    if (-not (Test-Path -LiteralPath $exampleFullPath -PathType Leaf)) {
        Fail "install-example file '$exampleFullPath' does not exist."
    }

    $installCommands = @(Get-DotnetToolInstallCommands $exampleFullPath)
    if ($installCommands.Count -eq 0) {
        Fail "canonical install-example file '$examplePath' must contain a pinned KeelMatrix.EfGuard install command."
    }

    $canonicalCommands = @($installCommands | Where-Object { $_.PackageId -eq "KeelMatrix.EfGuard" })
    $competingCommands = @($installCommands | Where-Object { $_.PackageId -ne "KeelMatrix.EfGuard" })
    if ($competingCommands.Count -gt 0) {
        $competingPackages = @($competingCommands | ForEach-Object { if ([string]::IsNullOrWhiteSpace($_.PackageId)) { '<missing package ID>' } else { $_.PackageId } } | Select-Object -Unique)
        Fail "canonical install-example file '$examplePath' contains a competing dotnet tool install command for $($competingPackages -join ', ')."
    }
    if ($canonicalCommands.Count -eq 0) {
        Fail "canonical install example '$examplePath' must install KeelMatrix.EfGuard."
    }

    foreach ($installCommand in $canonicalCommands) {
        if ($installCommand.VersionMatches.Count -eq 0) {
            Fail "canonical install example '$examplePath' must pin KeelMatrix.EfGuard with --version."
        }
        if ($installCommand.VersionMatches.Count -gt 1) {
            Fail "canonical install example '$examplePath' contains more than one --version option on line $($installCommand.Line)."
        }

        $installVersion = $installCommand.VersionMatches[0].Groups["version"].Value
        if ($installVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
            Fail "install example '$examplePath' contains unverifiable EfGuard version '$installVersion'."
        }
        if ($installVersion -ne $ExpectedVersion) {
            Fail "install example '$examplePath' version '$installVersion' does not match release version '$ExpectedVersion'."
        }
        if ($null -eq $canonicalInstallVersion) {
            $canonicalInstallVersion = $installVersion
        }
        elseif ($installVersion -ne $canonicalInstallVersion) {
            Fail "canonical install example '$examplePath' version '$installVersion' does not match canonical version '$canonicalInstallVersion'."
        }
    }
}

foreach ($releasePath in @($ReleaseFacingDocumentationPath)) {
    $releaseFullPath = Resolve-RepositoryPath $releasePath
    if (-not (Test-Path -LiteralPath $releaseFullPath -PathType Leaf)) {
        Fail "release-facing documentation file '$releaseFullPath' does not exist."
    }

    $releaseCommands = @(Get-DotnetToolInstallCommands $releaseFullPath)
    if ($releaseCommands.Count -eq 0) {
        Fail "release-facing document '$releasePath' must contain a pinned KeelMatrix.EfGuard install example."
    }

    $releaseEfGuardCommands = @($releaseCommands | Where-Object { $_.PackageId -eq "KeelMatrix.EfGuard" })
    $competingCommands = @($releaseCommands | Where-Object { $_.PackageId -ne "KeelMatrix.EfGuard" })
    if ($competingCommands.Count -gt 0) {
        $competingPackages = @($competingCommands | ForEach-Object { if ([string]::IsNullOrWhiteSpace($_.PackageId)) { '<missing package ID>' } else { $_.PackageId } } | Select-Object -Unique)
        Fail "release-facing document '$releasePath' contains a competing dotnet tool install command for $($competingPackages -join ', '); use the pinned KeelMatrix.EfGuard example."
    }
    if ($releaseEfGuardCommands.Count -eq 0) {
        Fail "release-facing document '$releasePath' must contain a KeelMatrix.EfGuard install example."
    }

    foreach ($releaseCommand in $releaseEfGuardCommands) {
        if ($releaseCommand.VersionMatches.Count -eq 0) {
            Fail "release-facing document '$releasePath' contains an unpinned KeelMatrix.EfGuard install example on line $($releaseCommand.Line)."
        }
        if ($releaseCommand.VersionMatches.Count -gt 1) {
            Fail "release-facing document '$releasePath' contains more than one --version option on line $($releaseCommand.Line)."
        }

        $releaseVersion = $releaseCommand.VersionMatches[0].Groups["version"].Value
        if ($releaseVersion -notmatch '^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$') {
            Fail "release-facing document '$releasePath' contains unverifiable EfGuard version '$releaseVersion' on line $($releaseCommand.Line)."
        }
        if ($releaseVersion -ne $canonicalInstallVersion) {
            Fail "release-facing document '$releasePath' version '$releaseVersion' does not match canonical install version '$canonicalInstallVersion'."
        }
        if ($releaseVersion -ne $ExpectedVersion) {
            Fail "release-facing document '$releasePath' version '$releaseVersion' does not match release version '$ExpectedVersion'."
        }
    }
}

Write-Output "Changelog contract passed for KeelMatrix.EfGuard $ExpectedVersion at commit $currentCommit."
exit 0
