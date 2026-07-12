# Gauge Interface Project Plan

## Purpose

Build a modern desktop Gauge Interface application to replace the legacy LabVIEW software for Memory Gauges and Acoustic Gauges.

The first target is dependable Windows operation using the existing USB-to-serial adapter and legacy serial protocol. The design should keep Linux and macOS realistic for future releases, and it should allow a later move from serial transport to USB CDC, HID, or another USB protocol without rewriting the application.

The application will be used by engineering, workshop technicians, and field operators in oil and gas environments. It must be robust, clear, and usable under awkward field conditions rather than feeling like a fragile lab-only utility.

## Initial Scope

The first usable version should focus on:

1. Connecting to gauges over the existing serial interface.
2. Identifying connected Memory Gauge and Acoustic Gauge hardware.
3. Downloading stored memory files from the gauge.
4. Decoding raw pressure and temperature records.
5. Applying calibration conversion in the PC application.
6. Displaying pressure and temperature data locally.
7. Exporting downloaded jobs to practical file formats such as CSV, text, and JSON.

Later versions can add richer acoustic diagnostics, automatic website upload, firmware update/bootload tools, reporting, and more advanced job/database handling.

## Recommended Technology Route

Use C#/.NET with Avalonia UI.

Reasons:

- Supports Windows first while keeping Linux and macOS viable.
- Good fit for a deployable engineering desktop application.
- Strong separation between UI, protocol, serial/USB transport, calibration, and data export.
- Mature testing ecosystem for protocol and conversion code.
- Suitable for field-friendly, offline-first operation.
- Avoids locking the project into Windows-only UI technology too early.

Alternative options considered:

- WPF or WinUI: strong Windows choices, but weaker cross-platform path.
- Electron: productive and familiar for web-style UI, but heavier and less ideal for a hardware/protocol-heavy application.
- Tauri: lightweight and attractive, but Rust plus web frontend adds complexity unless there is a strong reason to choose it.
- Qt: technically strong for industrial serial tools, but C++ and deployment/licensing considerations add friction.

## Proposed Architecture

The application should be split into clear layers:

```text
Gauge.Interface.App
  Avalonia desktop UI, view models, field-friendly workflows

Gauge.Core
  Gauge services, use cases, job/session handling

Gauge.Protocol
  Packet framing, commands, CRC, binary record decoding

Gauge.Transport
  Serial transport now, future USB transport later

Gauge.Calibration
  Raw pressure/temperature conversion, coefficients, validation

Gauge.Data
  Job model, local session storage, imports, exports
```

The most important design decision is to hide the physical connection behind a transport interface. The protocol layer should not care whether bytes come from a serial port, USB CDC COM port, HID device, or another future USB route.

## Firmware Protocol Notes

The existing PIC firmware already defines the legacy host protocol.

Serial frame structure:

```text
0x55        Start/autobaud byte
command     uint8
length      uint16 little-endian
address     uint32 as low, high, upper, extended bytes
payload     0 to max block size bytes
crc16       CRC over frame body, transmitted high byte then low byte in firmware replies
```

Serial connection startup behaviour observed from firmware and hardware:

- At power-up, the gauge waits briefly for PC serial communication at `57600` baud.
- The first valid decoded serial packet, normally `IDENTIFY`, puts the gauge into serial-connected mode.
- In `PROCESS_SERIAL`, the firmware enables memory, turns on the PLL, delays briefly, resets the EUSART baud setting, initializes the file system, and remains in serial mode until power is removed.
- The PC should wait around `250 ms` after the slow `IDENTIFY`, then verify communication at the faster baud rate, currently `460800`.
- The PC should not send `START_PLL` as part of the normal wake-up/connection handshake.

Memory Gauge source reference:

- `C:\REPOS\PIC_Memory_Gauge\serial.h`
- `C:\REPOS\PIC_Memory_Gauge\serial.c`
- `C:\REPOS\PIC_Memory_Gauge\file_system.h`
- `C:\REPOS\PIC_Memory_Gauge\serial_sensor.h`

Acoustic Gauge source reference:

- `C:\REPOS\PIC_Acoustic_Gauge\serial.h`
- `C:\REPOS\PIC_Acoustic_Gauge\serial.c`
- `C:\REPOS\PIC_Acoustic_Gauge\acoustic.h`
- `C:\REPOS\PIC_Acoustic_Gauge\file_system.h`

Core shared commands include:

- `IDENTIFY`
- `READ_INT_EE`
- `WRITE_INT_EE`
- `READ_EXT_EE`
- `WRITE_EXT_EE`
- `READ_FILE_SECTOR`
- `READ_RECORD_SECTOR`
- `FIND_EOF`
- `READ_SENSOR_SN`
- `READ_SENSOR_CAL`
- `READ_SENSOR_P_POLY`
- `READ_SENSOR_T_POLY`
- `SET_MEASURE_RATE`
- `SET_MEM_MODE`
- `MEM_STATUS`

Acoustic-specific commands include:

- `TX_ENABLE`
- `TX_DISABLE`
- `SET_PULSE_INT`
- `SET_ACOUSTIC_ADDR`
- `TX_BYTE`
- `TX_PACKET`
- `READ_ACOUSTIC_PACKET`
- `RESET_ACOUSTIC_PACKET`
- `GET_LAST_PT`
- `GET_DEVICE_STATUS`
- `GET_TIMESTAMP`
- `START_SENSOR_MEAS`

The protocol implementation must be covered by unit tests before being trusted in the UI.

## Data And Calibration

The gauge stores raw pressure and temperature data. The PC application must convert that raw data to engineering units using calibration information read from the gauge or supplied with the job.

Reference material now exists in:

- `C:\REPOS\PC_Gauge_Interface\reference\Labview-screenshots`
- `C:\REPOS\PC_Gauge_Interface\reference\labview-exports\Example Export.raw`
- `C:\REPOS\PC_Gauge_Interface\UX_STORYBOARD.md`

These files describe legacy workflows and required data, not the desired visual design. The new interface should be a total redesign with field operators in mind.

Early work must identify:

- Exact memory record layout for P&T samples.
- Timestamp or sample interval handling.
- Sensor serial number and calibration record format.
- Pressure polynomial format.
- Temperature polynomial format.
- Units currently used by the LabVIEW software.
- Expected output format for website upload.

Sample LabVIEW outputs, screenshots, and known-good downloaded files will be essential test fixtures. They should be added under a controlled test-data folder once available.

The example export format includes job metadata followed by tabular records with:

- P counts.
- T counts.
- Converted pressure.
- Converted temperature.
- Sequence.
- Counter.
- Memory address.
- Timestamp.
- T frequency.
- CRC error flag.
- Corrected flag.
- Battery status.

The first conversion milestone should reproduce these columns from a known raw download and match the legacy pressure/temperature output within an agreed tolerance.

## Field-Ready UI Principles

The interface should be functional rather than decorative, but it should not copy the engineering-heavy LabVIEW layout. The old screenshots are useful for discovering features and workflows; the new app should feel like an operator tool first.

Important qualities:

- Clear connection status and selected COM port.
- Large readable primary data values.
- Explicit progress and retry behaviour during downloads.
- A persistent raw communication log for engineering diagnosis.
- Safe handling of destructive actions such as erase memory.
- Offline-first operation.
- Recoverable workflows after cable disconnects, bad packets, or power loss.
- Export paths that are obvious and repeatable.
- Minimal dependence on internet access.
- Clear separation between everyday operator actions and advanced engineering controls.
- High contrast, large click targets, and status language suitable for field use.
- Strong confirmation and summary screens after downloads, with errors presented as actionable warnings rather than raw counters only.

Initial main views:

- Connect.
- Device summary.
- Download.
- Review data.
- Export.
- Diagnostics.

### Operator Workflow

The main path should be:

1. Select or auto-detect connection.
2. Identify gauge.
3. Show device readiness and memory summary.
4. Choose the file/run to download.
5. Download with clear progress, retry, and cancel handling.
6. Show completion summary.
7. Review pressure and temperature chart.
8. Export job files.

The operator should not have to understand packet framing, command history, raw buffers, PLL state, sensor pass-through, or EEPROM functions during normal use.

### Engineering Mode

Engineering tools should still exist, but behind an explicit engineering mode.

Candidate engineering tools from the legacy screenshots:

- Raw command/reply log.
- Command timing and error counters.
- Sensor read/calibration tools.
- Acoustic packet send/receive tools.
- Error log view.
- Advanced acoustic settings.
- Advanced memory functions.
- Bootloader/reset/program serial actions.
- PLL and sensor power controls.

Engineering mode should be visually and behaviourally distinct from operator mode, with extra warnings around destructive or state-changing commands.

## Local Storage And Export

The app should keep a local job/session after download so operators can view data before export.

Recommended early storage approach:

- Store downloaded binary/raw data exactly as received.
- Store decoded data separately.
- Store calibration data and metadata with the job.
- Export CSV, plain text, and JSON from the decoded job.

Future website upload should build on the same job/export model, not bypass it.

## Development Phases

### Phase 0: Discovery And Test Fixtures

- Collect LabVIEW screenshots.
- Collect sample exported files.
- Collect one or more known-good gauge memory downloads.
- Document current user workflow.
- Confirm pressure/temperature conversion equations.
- Confirm serial settings and timing behaviour.

### Phase 1: Protocol Foundation

- Create .NET solution structure.
- Implement CRC16 and CRC8 matching firmware.
- Implement packet encoder/decoder.
- Implement command constants for Memory Gauge and Acoustic Gauge.
- Add unit tests using known firmware-compatible byte examples.
- Add serial transport abstraction.

Deliverable: protocol library with tests.

### Phase 2: Command-Line Gauge Probe

- List available serial ports.
- Connect to selected port.
- Send `IDENTIFY`.
- Decode device type, firmware version, serial numbers, memory mode, and erase status.
- Log raw request/reply bytes.

Deliverable: simple CLI that proves reliable communication before UI work expands.

### Phase 3: Memory Download

- Implement `FIND_EOF`.
- Read file and record sectors.
- Handle block sizes, retries, CRC failures, and disconnects.
- Save raw download locally.
- Decode P&T records.

Deliverable: repeatable memory download to local job file.

Current status:

- `FIND_EOF` is implemented and verified against a connected Memory Gauge.
- File table reads are implemented with file-record CRC8 validation.
- Raw file download by file index is implemented.
- Memory data records are decoded into pressure counts, temperature counts, counter, address, timestamp estimate, CRC status, and battery status.
- Latest-file download is available through a reusable core service and CLI command.

### Phase 4: Calibration And Data Display

- Read sensor serial/calibration/polynomial data.
- Implement raw-to-pressure/temperature conversion.
- Validate against LabVIEW output.
- Display decoded P&T values and chart.
- Export CSV/text/JSON.

Deliverable: first useful replacement for LabVIEW download/display/export.

Current status:

- Sensor initialisation over the gauge serial link is implemented.
- Sensor serial, header/calibration record, pressure polynomial, and temperature polynomial reads are implemented.
- Live polynomial payloads have been confirmed as ASCII rows of 16-character hexadecimal IEEE-754 double values.
- Coefficient parsing is implemented and covered by tests.
- Sensor header parsing is implemented for reference clock, sensor ID, count bias, pressure startup delay, and PLL clock.
- Raw decode can optionally apply the sensor count bias so count columns align with the legacy export scale.
- Pressure and temperature conversion now matches the legacy LabVIEW formulas, including count bias, oscillator frequency scaling, and pressure coefficient matrix orientation.
- Calibrated CSV export is implemented and verified against a live gauge download.
- A reusable core job service now captures sensor calibration, downloads the latest memory file, and builds calibrated samples for the future UI.

### Phase 5: Desktop UI

- Build Avalonia UI around proven core services.
- Connection panel.
- Device summary.
- Download workflow.
- Job viewer.
- Chart.
- Export.
- Engineering log.

Deliverable: deployable Windows engineering build.

### Phase 6: Acoustic Gauge Support

- Add acoustic status view.
- Add last P&T readout.
- Add acoustic packet send/receive diagnostics.
- Add timestamp and node/status tooling.
- Keep acoustic-specific controls separate from normal memory-gauge workflows.

Deliverable: practical acoustic engineering tools.

### Phase 7: Deployment And Future USB

- Package Windows installer or self-contained application.
- Define update process.
- Add crash/error log bundle export.
- Add USB transport implementation when hardware direction is known.
- Investigate macOS/Linux builds once Windows version is stable.

Deliverable: field-deployable application with a path beyond serial.

## Immediate Next Steps

1. Create the initial Avalonia desktop shell.
2. Build the operator-first connect/download/review workflow around the proven `GaugeJobService`.
3. Add a chart and latest-value summary for calibrated P&T data.
4. Add local job folder selection and repeatable export naming.
5. Keep engineering commands behind a separate diagnostics view.

## Open Questions

- What is the largest memory download expected in normal field use?
- What file format does the website currently accept?
- Should exported jobs include both raw and converted data?
- How should field operators name jobs, wells, runs, or tools?
- Are multiple gauges ever connected at once?
- Should the app support firmware bootload/update in the first release or later?
- What safety confirmations are required before erase/reset/bootload commands?
