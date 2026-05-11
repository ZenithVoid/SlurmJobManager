param(
    [Parameter(Mandatory = $true)]
    [string]$PublishDirectory,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [string]$RuntimeIdentifier = "win-x64",
    [string]$Notes = "",
    [string]$PublishedAtUtc,
    [string]$PackageNamePrefix = "SlurmJobManager",
    [string]$AppProjectName = "SlurmJobManager.App",
    [switch]$GenerateLegacyVersionJson
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Resolve-VersionFromPublishOutput {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishDir,
        [Parameter(Mandatory = $true)]
        [string]$ProjectName
    )

    $depsFile = Join-Path $PublishDir "$ProjectName.deps.json"
    if (Test-Path -LiteralPath $depsFile) {
        $deps = Get-Content -LiteralPath $depsFile -Raw | ConvertFrom-Json -Depth 20
        if ($deps.PSObject.Properties.Name -contains "libraries") {
            foreach ($library in $deps.libraries.PSObject.Properties.Name) {
                if ($library -like "$ProjectName/*") {
                    return $library.Substring($ProjectName.Length + 1)
                }
            }
        }
    }

    throw "Failed to resolve version from publish output. Expected '$ProjectName.deps.json' with '$ProjectName/<version>' entry."
}

function Normalize-VersionForManifest {
    param([Parameter(Mandatory = $true)][string]$VersionText)

    $trimmed = $VersionText.Trim()
    if ($trimmed.StartsWith("v", [System.StringComparison]::OrdinalIgnoreCase)) {
        $trimmed = $trimmed.Substring(1)
    }

    if ($trimmed -match '^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?') {
        if ($matches[4]) {
            return "$($matches[1]).$($matches[2]).$($matches[3]).$($matches[4])"
        }

        return "$($matches[1]).$($matches[2]).$($matches[3])"
    }

    throw "Resolved version '$VersionText' is not parseable."
}

function Resolve-PublishedAtUtc {
    param([string]$PublishedAtRaw)

    if ([string]::IsNullOrWhiteSpace($PublishedAtRaw)) {
        return [DateTimeOffset]::UtcNow.ToString("o")
    }

    $parsed = [DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse($PublishedAtRaw, [ref]$parsed)) {
        throw "Invalid PublishedAtUtc value '$PublishedAtRaw'. Please use ISO-8601 format."
    }

    return $parsed.ToUniversalTime().ToString("o")
}

function Build-LatestManifestObject {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$PackageFileName,
        [Parameter(Mandatory = $true)][long]$PackageSize,
        [Parameter(Mandatory = $true)][string]$Sha256,
        [Parameter(Mandatory = $true)][string]$PublishedAt,
        [Parameter(Mandatory = $true)][string]$NotesText,
        [Parameter(Mandatory = $true)][string]$PackageType
    )

    $package = [ordered]@{
        packageType  = $PackageType
        fileName     = $PackageFileName
        relativePath = $PackageFileName
        sha256       = $Sha256
        size         = $PackageSize
    }

    return [ordered]@{
        version      = $Version
        title        = "SlurmJobManager $Version"
        packageType  = $PackageType
        fileName     = $PackageFileName
        relativePath = $PackageFileName
        package      = $PackageFileName
        publishedAt  = $PublishedAt
        notes        = $NotesText
        sha256       = $Sha256
        size         = $PackageSize
        packages     = @($package)
    }
}

function Build-LegacyVersionManifestObject {
    param(
        [Parameter(Mandatory = $true)][string]$Version,
        [Parameter(Mandatory = $true)][string]$PackageFileName,
        [Parameter(Mandatory = $true)][string]$PublishedAt,
        [Parameter(Mandatory = $true)][string]$NotesText
    )

    return [ordered]@{
        version     = $Version
        title       = "SlurmJobManager $Version"
        package     = $PackageFileName
        publishedAt = $PublishedAt
        notes       = $NotesText
    }
}

$publishDirPath = [System.IO.Path]::GetFullPath($PublishDirectory)
$outputDirPath = [System.IO.Path]::GetFullPath($OutputDirectory)

if (-not (Test-Path -LiteralPath $publishDirPath)) {
    throw "Publish directory does not exist: $publishDirPath"
}

if (-not (Test-Path -LiteralPath $outputDirPath)) {
    New-Item -ItemType Directory -Path $outputDirPath -Force | Out-Null
}

if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    throw "RuntimeIdentifier cannot be empty."
}

$resolvedVersionText = Resolve-VersionFromPublishOutput -PublishDir $publishDirPath -ProjectName $AppProjectName
$normalizedVersion = Normalize-VersionForManifest -VersionText $resolvedVersionText
$publishedAt = Resolve-PublishedAtUtc -PublishedAtRaw $PublishedAtUtc
$zipName = "$PackageNamePrefix-$normalizedVersion-$RuntimeIdentifier.zip"
$zipPath = Join-Path $outputDirPath $zipName
$latestManifestPath = Join-Path $outputDirPath "latest.json"
$legacyManifestPath = Join-Path $outputDirPath "version.json"

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

$tempStageRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("SlurmJobManagerReleaseArtifacts_" + [System.Guid]::NewGuid().ToString("N"))
$tempPublishCopy = Join-Path $tempStageRoot "publish"

try {
    New-Item -ItemType Directory -Path $tempPublishCopy -Force | Out-Null

    Copy-Item -Path (Join-Path $publishDirPath "*") -Destination $tempPublishCopy -Recurse -Force

    Get-ChildItem -LiteralPath $tempPublishCopy -Recurse -File |
        Where-Object {
            $_.Extension -in @(".tmp", ".log")
        } |
        Remove-Item -Force

    $staleDirs = @(
        (Join-Path $tempPublishCopy ".updater-backup")
    )
    foreach ($dir in $staleDirs) {
        if (Test-Path -LiteralPath $dir) {
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }

    Get-ChildItem -LiteralPath $tempPublishCopy -Recurse -Directory |
        Where-Object { $_.Name -ieq "logs" } |
        ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force
        }

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $tempPublishCopy,
        $zipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)

    if (-not (Test-Path -LiteralPath $zipPath)) {
        throw "Zip package was not created: $zipPath"
    }

    $fileHash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    $zipInfo = Get-Item -LiteralPath $zipPath
    $manifest = Build-LatestManifestObject `
        -Version $normalizedVersion `
        -PackageFileName $zipName `
        -PackageSize $zipInfo.Length `
        -Sha256 $fileHash.Hash.ToLowerInvariant() `
        -PublishedAt $publishedAt `
        -NotesText $Notes `
        -PackageType "zip"

    $manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $latestManifestPath -Encoding UTF8

    if ($GenerateLegacyVersionJson.IsPresent) {
        $legacyManifest = Build-LegacyVersionManifestObject `
            -Version $normalizedVersion `
            -PackageFileName $zipName `
            -PublishedAt $publishedAt `
            -NotesText $Notes
        $legacyManifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $legacyManifestPath -Encoding UTF8
    }
}
finally {
    if (Test-Path -LiteralPath $tempStageRoot) {
        Remove-Item -LiteralPath $tempStageRoot -Recurse -Force
    }
}

Write-Host "Release artifacts generated successfully."
Write-Host "Version       : $normalizedVersion"
Write-Host "Zip           : $zipPath"
Write-Host "latest.json   : $latestManifestPath"
if ($GenerateLegacyVersionJson.IsPresent) {
    Write-Host "version.json  : $legacyManifestPath"
}
