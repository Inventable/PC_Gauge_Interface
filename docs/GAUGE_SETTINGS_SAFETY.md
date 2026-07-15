# Gauge Settings Safety

This note maps the current serial commands in `PIC_Memory_Gauge` and `PIC_Acoustic_Gauge` to an appropriate desktop workflow. It is a design boundary, not permission to expose every firmware command in the UI.

## Retry Rule

Read-only, idempotent commands may use the standard three-attempt communication rule. A write command must not be repeated merely because its reply was lost: the gauge may have applied the first request. Each write needs command-specific recovery and, where possible, a readback before deciding whether another write is necessary.

## Operator Settings

| Setting | Firmware command | Payload | Required verification | Proposed access |
| --- | --- | --- | --- | --- |
| Measurement interval | `SET_MEASURE_RATE` (46) | Unsigned 16-bit, little endian | Re-identify and compare `measure_int` | Gauge Settings, after valid ranges and units are confirmed |
| Memory mode | `SET_MEM_MODE` (50) | One byte (`0` full, `1` mirror) | Re-identify and compare `memory_mode` | Engineering Mode only |
| Acoustic pulse interval | `SET_PULSE_INT` (20) | Unsigned 16-bit, little endian | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic address | `SET_ACOUSTIC_ADDR` (21) | One byte | Firmware needs a supported readback or status field | Acoustic engineering workflow |
| Acoustic transmit setup | `SET_TX_INTERVAL` (54) | Interval low/high, address, command, acoustic type | Firmware needs a supported readback | Acoustic engineering workflow |
| Acoustic recording | `SET_RECORD_SETTINGS` (59) | Enable flag, record length low/high | `GET_RECORD_SETTINGS` (58) | Acoustic engineering workflow |

The normal Gauge Settings page should remain read-only until measurement-interval units, limits, and the effect on the current logging file are confirmed. Changes must be disabled while any memory transaction is active and must show the connected device serial before confirmation.

## Service Commands

Calibration mode (47) writes a calibration-required flag and a 16-bit period. Serial pass-through mode (49) changes the communications path to the sensor. Both belong in a purpose-built service procedure, not a general settings form.

Sensor power, initialisation, calibration reads, memory tests, core/error logs, and acoustic packet diagnostics can be added to Engineering Mode only when there is a concrete diagnostic procedure and an expected result to present.

## Destructive Commands

The following commands require a clearly named engineering action, the connected serial number, an explicit confirmation, and a post-action recovery procedure:

- Erase external memory (30) and end-memory-erase (53).
- Reset device (11).
- Enter bootloader (10).
- Erase error log (51).
- Raw internal/external EEPROM writes (23 and 25).

Erase, reset, and bootloader commands must never use an automatic blind retry. Raw EEPROM writes should not be exposed as normal UI controls.

## Firmware Gaps Before Editable Settings

- Confirm measurement-interval units, accepted range, and whether a change starts a new file.
- Add or identify readback for acoustic pulse interval, acoustic address, and transmit settings.
- Define the erase completion/status sequence and expected timing.
- Define behaviour when a write succeeds but its acknowledgement is lost.
- Record device type capability mapping so memory-only controls never appear for an acoustic gauge and vice versa.
