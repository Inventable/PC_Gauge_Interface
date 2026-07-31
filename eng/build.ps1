param(
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

& $dotnet build (Join-Path $repoRoot 'Gauge.Interface.sln') `
    --artifacts-path $buildArtifacts `
    --configfile (Join-Path $repoRoot 'NuGet.Config') `
    --nologo `
    --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE"
}
