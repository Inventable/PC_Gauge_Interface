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

& $dotnet build (Join-Path $repoRoot 'Gauge.Interface.sln') --configfile (Join-Path $repoRoot 'NuGet.Config')
