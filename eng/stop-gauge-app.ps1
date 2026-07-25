[CmdletBinding()]
param(
    [ValidateRange(1, 60)]
    [int]$TimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

function Get-GaugeInterfaceProcesses {
    @(
        Get-Process -ErrorAction SilentlyContinue |
            Where-Object {
                $_.ProcessName -ieq 'Gauge.Interface.App' -or
                $_.MainWindowTitle -ieq 'Northstar Gauge Interface'
            }
    )
}

$targets = Get-GaugeInterfaceProcesses
if ($targets.Count -eq 0) {
    Write-Host 'Gauge Interface is not running.'
    return
}

foreach ($process in $targets) {
    Write-Host "Closing Gauge Interface process $($process.Id)..."
    if ($process.MainWindowHandle -ne 0) {
        $null = $process.CloseMainWindow()
    }
}

$graceSeconds = [Math]::Min(3, $TimeoutSeconds)
$graceDeadline = [DateTime]::UtcNow.AddSeconds($graceSeconds)
do {
    $remaining = Get-GaugeInterfaceProcesses
    if ($remaining.Count -eq 0) {
        Write-Host 'All Gauge Interface instances closed.'
        return
    }

    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $graceDeadline)

foreach ($process in $remaining) {
    Write-Host "Forcing Gauge Interface process $($process.Id) to stop..."
    Stop-Process -Id $process.Id -Force -ErrorAction Stop
}

$stopDeadline = [DateTime]::UtcNow.AddSeconds([Math]::Max(1, $TimeoutSeconds - $graceSeconds))
do {
    $remaining = Get-GaugeInterfaceProcesses
    if ($remaining.Count -eq 0) {
        Write-Host 'All Gauge Interface instances stopped.'
        return
    }

    Start-Sleep -Milliseconds 100
} while ([DateTime]::UtcNow -lt $stopDeadline)

$ids = ($remaining.Id | Sort-Object) -join ', '
throw "Gauge Interface processes did not stop within $TimeoutSeconds seconds: $ids"
