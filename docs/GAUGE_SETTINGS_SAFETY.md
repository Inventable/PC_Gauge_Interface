# Gauge Settings Safety

This note maps the current serial commands in `PIC_Memory_Gauge` and `PIC_Acoustic_Gauge` to an appropriate desktop workflow. It is a design boundary, not permission to expose every firmware command in the UI.

## Retry Rule

Read-only, idempotent commands may use the standard three-attempt communication rule. A write command must not be repeated merely because its reply was lost: the gauge may have applied the first request. Each write needs command-specific recovery and, where possible, a readback before deciding whether another write is necessary.

## Operator Settings

| Setting | Firmware command | Payload | Required verification | Proposed access |
| --- | --- | --- | --- | --- |
| Measurement interval | `SET_MEASURE_RATE` (46) | Seconds as unsigned 16-bit, little endian | Re-identify and compare `measure_int` | Gauge Settings |
| Memory mode | `SET_MEM_MODE` (50) | One byte (`0` full, `1` mirror) | Full erase, then re-identify and compare `memory_mode` | Gauge Settings, erase-gated |
| Acoustic pulse interval | `SET_PULSE_INT` (20) | Unsigned 16-bit, little endian | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic address | `SET_ACOUSTIC_ADDR` (21) | One byte | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic transmit setup | `SET_TX_INTERVAL` (54) | Interval low/high, address, command, acoustic type | Firmware needs a supported readback | Acoustic engineering workflow |
| Acoustic recording | `SET_RECORD_SETTINGS` (59) | Enable flag, record length low/high | `GET_RECORD_SETTINGS` (58) | Acoustic engineering workflow |

The normal Gauge Settings page permits measurement-interval and supported
storage-mode changes. Changes are disabled while any memory transaction is
active. The connected device type and serial are verified immediately before
each write, and `IDENTIFY` must report the requested value afterwards.

## Measurement Interval Contract

The firmware inspection confirms that the stored interval is in seconds and that command 46 writes its two payload bytes directly to EEPROM as an unsigned little-endian value. Identify returns the same value, and each new file-table start record copies it into that file's metadata.

The application permits 1 through 65,535 whole seconds. It offers the common
operator presets from 1-10 seconds, 20 seconds, 30 seconds, 1, 2, 5, 10, 20 and
30 minutes, and 1 hour, plus a custom entry. The write changes the stored
deployment interval and is described as applying to the next recording; it
does not reinterpret existing file metadata or attempt to change a recording
already in progress.

Commands 46 and 50 are non-idempotent EEPROM writes from the host's point of
view. The serial transport sends each only once. If the reply is lost, the app
uses `IDENTIFY` readback to decide whether the requested value was applied; it
does not blindly repeat the write.

The settings page estimates remaining record time using the selected interval
and loaded catalog. V2 uses eight logical bytes per sample. V3 uses up to 18
samples per 256-byte physical page and reserves a 4 KiB atomic header for the
next file. It is an estimate: page fill, recovery reservations, or future
format changes can reduce actual duration.

## Storage Mode Contract

The host never changes storage mode while committed files exist. Selecting a
different supported mode opens the existing erase page. Only after the full
erase finishes, command 53/65 has cleared the erase interlock, and the gauge
remains the expected serial does the app send command 50. Cancelling the erase
confirmation leaves the mode unchanged. A write failure after a verified erase
is reported as a mode-change failure without falsely marking the erase
incomplete; the now-empty gauge can be retried.

Both V2 and V3 application paths expose mode `0` as 64 MiB full-capacity
storage and mode `1` as 32 MiB mirrored storage. In V3 mirror mode, host
recovery reads the second replica only after the primary fails validation. In
V3 full mode, the host never calculates or reads a mirror address.

The V3 application control is enabled in advance of the corresponding firmware
update. The V3 gauge must not be deployed or used for full-mode application
testing until the recorder honours `memory_mode`, reports a 64 MiB
`storage_end`, and passes the requirements in
`docs/V3_STORAGE_MODE_FIRMWARE.md`.

## Service Commands

Calibration mode (47) writes a calibration-required flag and a 16-bit period. Serial pass-through mode (49) changes the communications path to the sensor. Both belong in a purpose-built service procedure, not a general settings form.

Sensor power, initialisation, calibration reads, memory tests, core/error logs, and acoustic packet diagnostics can be added to Engineering Mode only when there is a concrete diagnostic procedure and an expected result to present.

## Destructive Commands

The following commands require a clearly named action, a verified connected
identity, an explicit confirmation, and a post-action recovery procedure:

- Progress erase (64/65), with legacy erase (30/52/53) only as a V2 fallback.
- Sensor Live (66-69), with a temporary one-second interval and no memory
  recording.
- Reset device (11).
- Enter bootloader (10).
- Erase error log (51).
- Raw internal/external EEPROM writes (23 and 25).

Erase, reset, and bootloader commands must never use an automatic blind retry. Raw EEPROM writes should not be exposed as normal UI controls.

The Gauge Settings **Erase Memory** action names both 32 MiB devices and warns
that all recordings will be lost. Modern firmware reports paired-block
progress. The host polls every 20 ms so the next block starts promptly on
current firmware. V2 firmware uses whole-chip erase and busy polling; its
displayed percentage and completion time are explicitly labelled as estimates,
while success still depends on the gauge reporting both chips idle and
accepting command 53. Both modes then re-read `IDENTIFY` and require
`erase_status == 0`. Cancellation or a lost reply returns the UI directly to
Disconnected and never sends command 53 or reports success.

Every nonzero memory-gauge `erase_status` is treated as a deployment lockout.
The application skips file discovery and opens erase recovery immediately.
Recovery waits for any in-flight flash command, resets the PIC without clearing
the interlock, reconnects, and starts command 64 from block-pair zero. Command
30 is used only when the firmware reports that command 64 is unsupported. This
preserves V3 progress updates and never resumes the remaining paired blocks.
Firmware safety and autonomous-sequencing requirements are defined in
`docs/V3_ERASE_SAFETY_AND_PERFORMANCE.md`.

The **Sensor Live** settings action retrieves calibration with commands 41-44
without first starting measurement, then uses commands 66-69 to run a temporary
one-second test. It displays the latest calibrated pressure and temperature and
a rolling 60-second graph. The test must never change the stored deployment
interval or write external memory. The firmware command and HIL contract is
defined in `docs/V3_SENSOR_LIVE_PROTOCOL.md`.

## Remaining Firmware Gaps

- Enforce a firmware-side nonzero measurement-interval range as defence in
  depth. The app currently enforces `1..65,535`.
- Implement and HIL-validate the V3 non-mirrored layout semantics before using
  the enabled full-capacity application option.
- Add or identify readback for acoustic pulse interval, acoustic address, and transmit settings.
- Record device type capability mapping so memory-only controls never appear for an acoustic gauge and vice versa.
