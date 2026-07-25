param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipArchive,
    [switch]$KeepRunning
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $KeepRunning) {
    & (Join-Path $PSScriptRoot 'stop-gauge-app.ps1')
}

$env:APPDATA = Join-Path $repoRoot '.dotnet-appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.dotnet-appdata'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget-packages'
$buildArtifacts = Join-Path $env:TEMP 'NorthstarGaugeInterface\dotnet-artifacts'

New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $buildArtifacts | Out-Null

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = 'dotnet'
}

$project = Join-Path $repoRoot 'src\Gauge.Interface.App\Gauge.Interface.App.csproj'
$artifactRoot = Join-Path $repoRoot 'dist'
$publishDirectory = Join-Path $artifactRoot "publish\$Runtime"
$archivePath = Join-Path $artifactRoot "Northstar-Gauge-Interface-$Runtime.zip"

$resolvedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
$resolvedPublishDirectory = [System.IO.Path]::GetFullPath($publishDirectory)
if (-not $resolvedPublishDirectory.StartsWith($resolvedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Publish directory must remain inside the repository: $resolvedPublishDirectory"
}

if (Test-Path -LiteralPath $resolvedPublishDirectory) {
    Remove-Item -LiteralPath $resolvedPublishDirectory -Recurse -Force
}

& $dotnet publish $project `
    --configuration $Configuration `
    --runtime $Runtime `
    --self-contained true `
    --output $resolvedPublishDirectory `
    --artifacts-path $buildArtifacts `
    --configfile (Join-Path $repoRoot 'NuGet.Config') `
    --nologo `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$executable = Join-Path $resolvedPublishDirectory 'Gauge.Interface.App.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Published executable was not produced: $executable"
}

if (-not $SkipArchive) {
    if (Test-Path -LiteralPath $archivePath) {
        Remove-Item -LiteralPath $archivePath -Force
    }

    Compress-Archive -Path (Join-Path $resolvedPublishDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
}

Write-Host "Published: $resolvedPublishDirectory"
if (-not $SkipArchive) {
    Write-Host "Archive:   $archivePath"
}
