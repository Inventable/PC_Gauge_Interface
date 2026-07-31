param(
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishDirectory = Join-Path $repoRoot "dist\publish\$Runtime"
$executable = Join-Path $publishDirectory 'Gauge.Interface.App.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw "Publish the self-contained application before building the installer: $executable"
}

$compilerCandidates = @(
    (Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path $env:USERPROFILE 'AppData\Local\Programs\Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique

$compiler = $compilerCandidates | Select-Object -First 1
if (-not $compiler) {
    throw 'Inno Setup 6 or 7 compiler (ISCC.exe) is required. Install JRSoftware.InnoSetup.7 with winget, then rerun this script.'
}

$installerDefinition = Join-Path $repoRoot 'installer\NorthstarGaugeInterface.iss'
$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($executable).ProductVersion.Split('+')[0]
& $compiler "/DMyAppVersion=$version" $installerDefinition
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$installer = Join-Path $repoRoot "dist\Northstar-Gauge-Interface-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $installer)) {
    throw "Installer was not produced at the expected path: $installer"
}

Write-Host "Installer: $installer"
