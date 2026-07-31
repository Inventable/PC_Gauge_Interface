param(
    [Parameter(Mandatory = $true)]
    [string]$FirmwareHex,
    [string]$FirmwareVersion = '2.0',
    [uint32[]]$DeviceTypes = @(100160, 100196, 100230),
    [uint32[]]$SupportedPcbs = @(100161, 100184, 100228),
    [string]$MinimumBootloader = '1.2',
    [string]$Repository = 'Inventable/IT_Releases',
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9a-fA-F]{40}$')]
    [string]$FirmwareSourceCommit,
    [string]$ReleaseNotes = 'Initial public firmware release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$resolvedFirmware = (Resolve-Path -LiteralPath $FirmwareHex).Path
$appExecutable = Join-Path $repoRoot 'dist\publish\win-x64\Gauge.Interface.App.exe'
if (-not (Test-Path -LiteralPath $appExecutable)) {
    throw 'Publish the application before preparing the download repository.'
}

$appVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($appExecutable).ProductVersion.Split('+')[0]
$appSourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $appSourceCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'Could not determine the application source commit.'
}
$installer = Join-Path $repoRoot "dist\Northstar-Gauge-Interface-Setup-$appVersion.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Build the installer before preparing the download repository: $installer"
}

$stagingRoot = Join-Path $repoRoot 'dist\download-repository'
$resolvedStagingRoot = [System.IO.Path]::GetFullPath($stagingRoot)
$resolvedDistRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'dist')).TrimEnd('\') + '\'
if (-not $resolvedStagingRoot.StartsWith($resolvedDistRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging directory must remain inside dist: $resolvedStagingRoot"
}

if (Test-Path -LiteralPath $resolvedStagingRoot) {
    Remove-Item -LiteralPath $resolvedStagingRoot -Recurse -Force
}

$assetDirectory = Join-Path $resolvedStagingRoot 'release-assets'
$channelDirectory = Join-Path $resolvedStagingRoot 'channels'
$schemaDirectory = Join-Path $resolvedStagingRoot 'schemas'
New-Item -ItemType Directory -Force -Path $assetDirectory, $channelDirectory, $schemaDirectory | Out-Null

$installerName = Split-Path -Leaf $installer
Copy-Item -LiteralPath $installer -Destination (Join-Path $assetDirectory $installerName)
$firmwareName = "Northstar-Memory-Gauge-Universal-Offset-$FirmwareVersion.hex"
$firmwareDestination = Join-Path $assetDirectory $firmwareName
Copy-Item -LiteralPath $resolvedFirmware -Destination $firmwareDestination

$firmwareHash = (Get-FileHash -LiteralPath $firmwareDestination -Algorithm SHA256).Hash
$manifest = [ordered]@{
    schemaVersion = 1
    channel = 'stable'
    suiteVersion = $appVersion
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('o')
    application = [ordered]@{
        version = $appVersion
        sourceRepository = 'Inventable/PC_Gauge_Interface'
        sourceCommit = $appSourceCommit
        url = "https://github.com/$Repository/releases/download/suite-v$appVersion/$installerName"
        sha256 = (Get-FileHash -LiteralPath (Join-Path $assetDirectory $installerName) -Algorithm SHA256).Hash
    }
    firmware = @(
        $DeviceTypes | ForEach-Object {
        [ordered]@{
            deviceType = $_
            supportedPcbs = $SupportedPcbs
            version = $FirmwareVersion
            imageType = 'offset-production'
            processor = 'PIC18F26K80'
            minimumBootloader = $MinimumBootloader
            sourceRepository = 'Inventable/PIC_Memory_Gauge'
            sourceCommit = $FirmwareSourceCommit
            url = "https://github.com/$Repository/releases/download/suite-v$appVersion/$firmwareName"
            sha256 = $firmwareHash
            releaseNotes = $ReleaseNotes
        }
        }
    )
}
$manifestJson = $manifest | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText(
    (Join-Path $channelDirectory 'stable.json'),
    $manifestJson + [Environment]::NewLine,
    [System.Text.UTF8Encoding]::new($false))

$checksums = @(
    Get-FileHash -LiteralPath (Join-Path $assetDirectory $installerName) -Algorithm SHA256
    Get-FileHash -LiteralPath $firmwareDestination -Algorithm SHA256
) | ForEach-Object {
    $relativePath = $_.Path.Substring($resolvedStagingRoot.Length).TrimStart('\').Replace('\', '/')
    "$($_.Hash)  $relativePath"
}
$checksums | Set-Content -LiteralPath (Join-Path $resolvedStagingRoot 'SHA256SUMS.txt') -Encoding ascii

Copy-Item -LiteralPath (Join-Path $repoRoot 'release-repository-template\README.md') -Destination (Join-Path $resolvedStagingRoot 'README.md')
Copy-Item -LiteralPath (Join-Path $repoRoot 'release-repository-template\.gitignore') -Destination (Join-Path $resolvedStagingRoot '.gitignore')
Copy-Item -LiteralPath (Join-Path $repoRoot 'release-repository-template\schemas\release-manifest.schema.json') -Destination (Join-Path $schemaDirectory 'release-manifest.schema.json')
Write-Host "Download repository staging: $resolvedStagingRoot"
Write-Host "Upload files from release-assets to GitHub release suite-v$appVersion; commit the other files."
