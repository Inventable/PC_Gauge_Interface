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

To debug cold-start detection without switching to the faster baud rate, send one `IDENTIFY` at `57600` and dump any raw bytes received:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 probe-identify-raw COM5 57600 2000
```

This command does not try `460800`. It reports the exact transmitted bytes, the raw received bytes, and whether the received bytes decode as a valid gauge frame.

## Bootloader Probe

Once a gauge is already responding in fast serial mode, validate entry into the resident bootloader and return to the installed application without writing flash:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-probe COM5 460800 115200
```

The application bootload request and loader reset request are each sent once without automatic retry. Read-only bootloader version discovery may make three attempts. The probe does not send erase, flash-write, EEPROM-write, or configuration-write commands. See `docs/BOOTLOADER.md` for the protocol and recovery rules.

If a gauge is already in loader mode, inspect or exit it directly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-version COM5 57600
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-reset COM5 57600
```

The reset command is sent once. Both `bootloader-probe` and `bootloader-reset` immediately check for the application at `57600` even when the reset acknowledgement is lost; they never repeat an ambiguous reset automatically.

## Firmware Images And Programming

Inspect the production application artifact before connecting a gauge:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 firmware-inspect C:\REPOS\PIC_Memory_Gauge\dist\Offset\production\Memory_Gauge.X.production.hex
```

The inspector requires an `Offset/production` path, validates every Intel HEX checksum, rejects data below `0x0800`, rejects unsupported address regions, and excludes recognized PIC ID/configuration metadata from programming. StandAlone, Combined, and unified artifacts are refused.

After verifying serial mode, program a memory gauge by confirming its printed device serial explicitly:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 firmware-program COM5 C:\REPOS\PIC_Memory_Gauge\dist\Offset\production\Memory_Gauge.X.production.hex 3807522001 PROGRAM 460800 115200
```

Programming is capped at the validated `115200` loader baud. The updater erases `0x0800` first, erases the remaining application rows, writes populated rows in descending address order, verifies every row, performs a final non-start readback pass, and writes the `0x0800` start row last. It never writes PIC ID/configuration bytes or addresses occupied by the resident loader.

If an interrupted update leaves the unit in loader mode, rerun the complete verified image through the explicit recovery path:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 firmware-recover COM5 C:\REPOS\PIC_Memory_Gauge\dist\Offset\production\Memory_Gauge.X.production.hex RECOVER 115200
```

Recovery cannot read the gauge serial because the application is unavailable. It therefore requires the exact `RECOVER` token and validates loader device ID `0x6126` before mutation. Do not use `bootloader-reset` after an interrupted erase/write unless the complete image, including `0x0800`, has been verified.

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

If the sensor calibration header includes a count bias, pass it as the fourth optional value to display legacy-scale counts:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 decode-raw artifacts\gauge-file-002.rawbin 0x000097B0 1 12053700
```

Write the same decoded raw rows to CSV:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 export-raw-csv artifacts\gauge-file-002.rawbin artifacts\gauge-file-002.csv 0x000097B0 1 12053700
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

Capture the complete sensor calibration bundle to files:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 capture-sensor-calibration COM5 artifacts\gauge-file-002-cal 460800
```

The capture command power-cycles the sensor, waits briefly, and retries sensor initialisation before reading the payloads. This recovers the common case where the gauge is in serial mode but the downstream sensor UART is stale.

This writes:

- `sensor-serial.txt`
- `sensor-header.txt`
- `pressure-poly.txt`
- `temperature-poly.txt`

Convert a downloaded raw memory file directly into calibrated pressure and temperature CSV:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 export-calibrated-csv artifacts\gauge-file-002.rawbin artifacts\gauge-file-002-calibrated.csv artifacts\gauge-file-002-cal\sensor-header.txt artifacts\gauge-file-002-cal\pressure-poly.txt artifacts\gauge-file-002-cal\temperature-poly.txt 0x000097B0 1
```

The calibrated export applies the sensor header `Bias` to stored 24-bit counts, calculates pressure and temperature frequencies from `PLLClk`, and evaluates the Phase Sensors polynomial payloads.

## Latest Calibrated Download

Once the gauge is already in fast serial mode, download the newest memory file, capture the current sensor calibration bundle, and write a calibrated CSV in one step:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 download-latest-calibrated COM5 artifacts\latest-job 460800
```

This writes:

- `sensor-serial.txt`
- `sensor-header.txt`
- `pressure-poly.txt`
- `temperature-poly.txt`
- `gauge-file-###.rawbin`
- `gauge-file-###-calibrated.csv`

For a cold gauge, wake it first with `wait-identify` or `verify-serial`, then run the latest calibrated download command at `460800`.
