# Gauge Settings Safety

This note maps the current serial commands in `PIC_Memory_Gauge` and `PIC_Acoustic_Gauge` to an appropriate desktop workflow. It is a design boundary, not permission to expose every firmware command in the UI.

## Retry Rule

Read-only, idempotent commands may use the standard three-attempt communication rule. A write command must not be repeated merely because its reply was lost: the gauge may have applied the first request. Each write needs command-specific recovery and, where possible, a readback before deciding whether another write is necessary.

## Operator Settings

| Setting | Firmware command | Payload | Required verification | Proposed access |
| --- | --- | --- | --- | --- |
| Measurement interval | `SET_MEASURE_RATE` (46) | Seconds as unsigned 16-bit, little endian | Re-identify and compare `measure_int` | Read-only until the firmware gaps below are closed |
| Memory mode | `SET_MEM_MODE` (50) | One byte (`0` full, `1` mirror) | Re-identify and compare `memory_mode` | Engineering Mode only |
| Acoustic pulse interval | `SET_PULSE_INT` (20) | Unsigned 16-bit, little endian | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic address | `SET_ACOUSTIC_ADDR` (21) | One byte | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic transmit setup | `SET_TX_INTERVAL` (54) | Interval low/high, address, command, acoustic type | Firmware needs a supported readback | Acoustic engineering workflow |
| Acoustic recording | `SET_RECORD_SETTINGS` (59) | Enable flag, record length low/high | `GET_RECORD_SETTINGS` (58) | Acoustic engineering workflow |

The normal Gauge Settings page remains read-only except for the dedicated
external-memory erase procedure described below. Changes are disabled while
any memory transaction is active and the connected identity is verified again
immediately before the destructive command.

## Measurement Interval Contract

The firmware inspection confirms that the stored interval is in seconds and that command 46 writes its two payload bytes directly to EEPROM as an unsigned little-endian value. Identify returns the same value, and each new file-table start record copies it into that file's metadata.

The current firmware does not validate the value, start a new file, or consistently restart measurement after the write. It also has two different sensor links:

- The legacy sensor command sends the interval as one ASCII digit, so only `0` through `9` can be represented correctly by that path.
- The modern sensor command sends `aut<decimal>`, which can represent the wider stored value.
- Acoustic firmware exposes a separate sensor-measurement restart command; memory-gauge firmware does not expose an equivalent host command.

Editing this value in the desktop app could therefore create a file whose table interval does not describe every sample, or set a value that one supported sensor cannot apply. Before enabling it, firmware should enforce a device/sensor-specific range, create a clean file boundary, apply the new schedule immediately and consistently, and provide verified readback with a defined recovery path.

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

## Firmware Gaps Before Editable Settings

- Define and enforce measurement-interval ranges for each supported sensor protocol.
- Make an interval change create a clean file boundary and restart measurement consistently on both gauge families.
- Define recovery when stored interval readback succeeds but sensor application fails.
- Add or identify readback for acoustic pulse interval, acoustic address, and transmit settings.
- Define behaviour when a write succeeds but its acknowledgement is lost.
- Record device type capability mapping so memory-only controls never appear for an acoustic gauge and vice versa.
