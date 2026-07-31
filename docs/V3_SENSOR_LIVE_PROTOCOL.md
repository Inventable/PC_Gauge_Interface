# V3 Sensor Live Firmware Contract

This document defines the firmware contract used by the desktop **Sensor Live**
page. The design originated from firmware branch
`codex/software-readiness-hil-entry` at commit `78923ac` and reuses the existing
V3 `measurement_t` decoder. The operator confirmed the implemented V3.0
connection and Sensor Live workflow on 25 July 2026. All multi-byte fields are
little-endian.

## Operator Workflow

Opening **Settings > Sensor Live** must:

1. verify that the connected memory gauge supports Sensor Live;
2. retrieve the four sensor calibration payloads without starting autonomous
   measurement;
3. start the attached sensor at a one-second interval;
4. poll a small status/data-ready response;
5. read and decode only new measurements;
6. display the latest pressure and temperature and a rolling 60-second graph;
7. stop the live session when the page closes.

Sensor Live is a test mode. It must not reserve a file, create a V2/V3 header,
write a sample, alter the configured deployment interval, or modify the V3
catalog.

## Command Allocation

Commands 66 through 69 are allocated as follows:

| Command | Name | Request | Successful response |
| --- | --- | --- | --- |
| 66 | `SENSOR_LIVE_START` | `interval_seconds:u16` | 8-byte status |
| 67 | `SENSOR_LIVE_STATUS` | empty | 8-byte status |
| 68 | `SENSOR_LIVE_READ` | empty | 20-byte latest sample |
| 69 | `SENSOR_LIVE_STOP` | empty | `COMMAND_SUCCESS` (`0x01`) |

Firmware without this feature must retain the standard one-byte
`ERROR_INVALID_COMMAND` (`0xFF`) response. The application uses that response
to show “Sensor Live requires a firmware update”.

## Status Payload

Commands 66 and 67 return exactly eight bytes:

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 1 | Protocol version, currently `1` |
| 1 | 1 | State: `0=idle`, `1=starting`, `2=running`, `3=fault` |
| 2 | 1 | Flags: bit 0 data ready, bit 1 sensor initialised, bit 2 calibration available |
| 3 | 1 | Last error; zero unless state is fault |
| 4 | 4 | Latest live sequence; zero before the first valid measurement |

Bits 3-7 of the flags byte are reserved and must be zero.

`SENSOR_LIVE_STATUS` is read-only. It must never initialise the sensor, clear a
sample, or advance a state machine as a side effect. This allows the command to
act as both capability probe and inexpensive data-ready poll.

## Latest-Sample Payload

Command 68 returns exactly 20 bytes:

| Offset | Size | Field |
| --- | ---: | --- |
| 0 | 1 | Protocol version, currently `1` |
| 1 | 1 | `measurement_t.quality_flags` |
| 2 | 1 | `measurement_t.sensor_iteration` |
| 3 | 1 | Reserved, zero |
| 4 | 4 | Monotonic live sequence, starting at 1 |
| 8 | 4 | `measurement_t.monotonic_ticks` |
| 12 | 4 | `measurement_t.pressure_raw` |
| 16 | 4 | `measurement_t.temperature_raw` |

Pressure and temperature are the decoded 24-bit sensor values stored in the
low 24 bits of each 32-bit field. The high byte must be zero. These are the
same biased-down raw values stored by V3; the application adds the `Bias`
value from the sensor header before calibration.

If command 68 is called before a new sample exists, return the existing
one-byte busy/no-data value `0xFC`. A successful read consumes the data-ready
indication only if the returned sequence is still the newest sample. If a
sensor frame arrives during response construction, leave data-ready set so
the newer sample cannot be stranded. The sequence also lets the host reject
duplicates.

## Calibration Commands

Continue using:

- 41 `READ_SENSOR_SN`
- 42 `READ_SENSOR_CAL`
- 43 `READ_SENSOR_P_POLY`
- 44 `READ_SENSOR_T_POLY`

For V3 these commands must work while live measurement is idle and must not
require command 40 `INITIALISE_SENSOR` first. They may power and communicate
with the sensor as needed, but must leave autonomous measurement stopped.
Retain the existing `ERROR_SENSOR_COMMS` (`0xFD`) response for missing sensor,
timeout, or invalid prompt.

The application reads all four payloads before command 66 and builds the
existing `SensorCalibrationBundle`. Calibration parsing and count conversion
are shared with V3 file decoding.

## Start Behaviour

`SENSOR_LIVE_START` must:

1. accept only a supported interval; the application sends `1`;
2. reject operation while external-memory erase interlock is nonzero;
3. initialise/power the sensor using the normal supported sensor protocol;
4. reset the live sequence, data-ready flag, iteration tracking, and last
   error;
5. start autonomous measurement at the requested interval;
6. enter a nonblocking live state and return the status payload.

The one-second live interval is temporary. Do not write
`EE_MEASURE_INTERVAL`, call the deployment file-recording path, or enable
external-memory writes.

Sensor UART reception must continue to be serviced from the main loop. On a
CRC-valid frame, use the existing `sensor_decode_frame()`/`measurement_t`
pipeline, copy the measurement into a dedicated latest-live snapshot,
increment the 32-bit live sequence, and set data ready.

## Stop and Failure Behaviour

`SENSOR_LIVE_STOP` must stop autonomous sensor output, clear the live
data-ready/session state, restore the normal connected-idle state, and return
`0x01`. It must not start recording or change the deployment interval.

The live session must also be stopped safely on:

- PC serial timeout or disconnect;
- reset or bootloader entry;
- start of external-memory erase;
- sensor timeout or repeated CRC/length failure;
- transition into normal deployment recording.

Use a dedicated nonzero error code in the status payload for sensor
initialisation, timeout, CRC, length, iteration, and internal-state failures.
State must be `fault` whenever `last_error` is nonzero. Preserve the normal
gauge error log where applicable.

## Main-State Integration

The current main loop routes unsolicited sensor bytes through
`PROCESS_SENSOR_SERIAL`, and V3 already exposes:

- `isV3MeasurementReady()`;
- `peekV3Measurement()`;
- `clearV3Measurement()`;
- the shared `measurement_t` structure.

Add a Sensor Live session flag/state so a decoded measurement is copied to the
live snapshot instead of being enqueued to `v3_recording_session`. Serial PC
commands must remain responsive while the sensor is running. Do not block for
the next one-second reading inside any command handler.

The incomplete-erase deployment lockout has priority over Sensor Live:
commands 66 and 68 should reject while `EE_EXT_MEM_ERASE != 0`; command 67 may
still return an idle/fault status for diagnostics.

## Required Firmware/HIL Evidence

- Status command on old firmware returns `0xFF`; supported firmware returns the
  exact eight-byte idle status without starting the sensor.
- Commands 41-44 succeed before command 66 and no external-memory write occurs.
- Command 66 with interval 1 produces one live frame per second without
  changing the stored deployment interval.
- Command 67 remains responsive and read-only while frames arrive.
- Command 68 returns each sequence once, with correct raw counts, ticks,
  iteration, and quality flags.
- Inject a frame between the sample snapshot and data-ready clear and prove the
  newer sequence remains available.
- Run for longer than 60 seconds and prove no file, catalog, header, or sample
  is written to either flash device.
- Disconnect the PC, issue command 69, reset, and enter erase; each path must
  leave the sensor stopped and the gauge in a safe state.
- Inject missing-sensor, timeout, CRC, length, and iteration faults and verify
  state/error reporting.
- Compare a live sample decoded by the desktop application with the same raw
  counts processed through the V3 file calibration path.
