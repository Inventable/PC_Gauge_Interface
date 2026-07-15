param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$SkipArchive
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$env:APPDATA = Join-Path $repoRoot '.dotnet-appdata'
$env:LOCALAPPDATA = Join-Path $repoRoot '.dotnet-appdata'
$env:DOTNET_CLI_HOME = Join-Path $repoRoot '.dotnet-home'
$env:NUGET_PACKAGES = Join-Path $repoRoot '.nuget-packages'

New-Item -ItemType Directory -Force -Path $env:APPDATA, $env:LOCALAPPDATA, $env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES | Out-Null

$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $dotnet = 'dotnet'
}

$project = Join-Path $repoRoot 'src\Gauge.Interface.App\Gauge.Interface.App.csproj'
$artifactRoot = Join-Path $repoRoot 'artifacts'
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
    --configfile (Join-Path $repoRoot 'NuGet.Config')

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
