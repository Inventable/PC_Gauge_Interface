# Gauge CLI Notes

The CLI is an engineering probe used to prove protocol behaviour before the desktop UI is built.

Run commands through the wrapper:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 <command>
```

## Connection

Cold gauges should be woken at `57600` baud first. A valid `IDENTIFY` puts the gauge into serial mode, turns on the PLL in firmware, and keeps the gauge in serial mode until power is removed.

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 wait-identify COM5 57600 60 100
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 verify-serial COM5 57600 460800 250
```

If the gauge is already in serial mode, direct fast commands can be used:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 identify COM5 460800
```

## Memory

Read the end-of-file address:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 find-eof COM5 460800
```

List recorded file entries:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 list-files COM5 460800
```

Download one file by index:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 download-file COM5 2 artifacts\gauge-file-002.rawbin 460800
```

Decode a raw binary download into count rows:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 decode-raw artifacts\gauge-file-002.rawbin 0x000097B0 1
```

Decoded rows currently contain raw pressure and temperature counts. Engineering-unit calibration is the next layer.

If the sensor calibration header includes a count bias, pass it as the fourth optional value to display legacy-scale counts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 decode-raw artifacts\gauge-file-002.rawbin 0x000097B0 1 12053700
```

## Sensor Calibration Data

Initialise the sensor before reading serial/calibration/polynomial data:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 initialise-sensor COM5 460800
```

Read sensor identity:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 read-sensor-serial COM5 460800
```

Read the sensor header/calibration record:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 read-sensor-cal COM5 460800
```

Read pressure and temperature polynomial coefficient tables:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 read-pressure-poly COM5 460800
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 read-temperature-poly COM5 460800
```

Polynomial payloads are ASCII tables of 16-character hexadecimal IEEE-754 double values. The CLI prints the raw ASCII and decoded coefficient rows.

The sensor header payload is parsed for:

- Reference clock.
- Sensor ID.
- Count bias.
- Pressure startup delay.
- PLL clock.
