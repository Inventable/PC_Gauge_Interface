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

The application is a Northstar product. It should use Northstar branding without becoming a marketing page: the brand should appear through the window title, logo/wordmark, colour system, typography, spacing, action hierarchy, and calm industrial tone.

Initial Northstar brand references from `https://northstardst.com/`:

- Website positioning: downhole specialists, well intervention, downhole reservoir testing, global reach, reliability, agility, versatility, dependability.
- Primary site/logo asset captured locally at `src/Gauge.Interface.App/Assets/northstar-logo.svg`.
- Working palette: Northstar red `#CE0E2D` as the primary product colour, green `#2DA55D` as a healthy/connected accent, charcoal `#414149`, grey `#5D5D66`, pale steel `#EBF0F3`.

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

### Phase 5 UI Design Work

The first shell proves that the workflow is possible. The next Phase 5 work should turn it into a clean operator application:

1. Establish Northstar design tokens in the app resources.
2. Create a branded application header and clear connected/not-connected states.
3. Keep serial-port choice and default download folder in an entry/settings view, not permanently on the operator screen.
4. Poll the selected gauge port when idle so disconnect/reconnect state is obvious without repeated manual actions.
5. Refine the file table as the primary operator decision point, including only file index, relative size, download state, and graph/review state.
6. Add progress stages and cancellation for long reads/downloads.
7. Add a focused review screen with pressure/temperature chart, latest values, sample count, job duration, and export state.
8. Add a completion summary after download.
9. Move raw protocol details, EEPROM tools, and acoustic diagnostics into a separate engineering mode.
10. Validate the workflow with field-style scenarios: cold gauge, already-awake gauge, small battery-insertion files, bad CRC, disconnected cable, no files, and long download.

### Operator Workflow

The main path should be:

1. Select or auto-detect connection.
2. Identify gauge.
3. Show device readiness and memory summary.
4. Show the file table with clear size and interval information, newest file first.
5. Choose the file/run to download.
6. Download with clear progress, retry, and cancel handling.
7. Show completion summary.
8. Review pressure and temperature chart.
9. Export job files.

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
- Northstar branded design system.
- Operator usability testing pass.

Deliverable: deployable Windows engineering build.

Current status:

- `Gauge.Interface.App` Avalonia desktop project is in the solution.
- The first operator shell can list serial ports, wake/verify the serial link, connect/read the file table, show relative file sizes, suggest the most likely job file, download the selected file into a named job folder, and show latest pressure/temperature plus recent sample rows.
- The UI is wired to the shared `GaugeJobService`, so the proven CLI workflow is not duplicated in the desktop app.
- Northstar brand colours are now app resources with red as the primary colour and green reserved for accent/healthy states.
- The review panel uses ScottPlot 5 with a single elapsed-time axis, pressure on the left axis, temperature on the right axis, and one subdued grid tied to the pressure axis. Direct axis labels and solid/dashed traces avoid relying on a separate legend or colour alone.
- Review controls now provide drag-to-zoom-window, wheel/pan interaction, zoom in/out, and fit-all. File metadata, ranges, duration, and latest values are kept in the adjacent summary panel.
- Chart data is passed as packed numeric arrays and rendered with `SignalXY`; a 259,200-sample-per-series verification (three days at one-second intervals) rendered successfully during development.
- Downloaded file rows can export legacy-compatible ASCII `.rec` files through the native save dialog. The export includes the device/sensor preamble and tab-separated calibrated P&T rows, and the app remembers the last record-export directory for subsequent saves.
- Serial activity is exclusive: automatic/manual downloads take priority, then idle connected-state polling verifies the fast link every 500 ms. A failed three-attempt liveness transaction enters the disconnected state and resumes aggressive `57600`-baud wake polling.
- Automatic downloads run from the highest file index to the lowest so the latest file becomes available first.
- The file table starts in descending file-number order and supports ascending/descending sorting by file number or file size. It shows each file's measurement interval and an immediate calculated duration; duration remains stable during transfer and is corrected once after mixed records are classified.
- A downloading file becomes graphable as soon as its first complete records are calibrated. The Review graph refreshes from incremental samples every two seconds and mirrors the file row's percentage and estimated time remaining; final completion replaces the preview with the fully parsed dataset.
- Automatic and manual downloads can be cancelled explicitly. Cancellation pauses the automatic queue, preserves any partial graph for inspection, and exposes a clear retry action that restarts the selected file before automatic work resumes.
- Review now provides a sample-snapped cursor with elapsed time, pressure, and temperature readout. Data quality reports file/data CRC errors and samples carrying battery warnings, using the same green/amber/red state language as the file table.
- The Review side panel uses consistent label/value tables for file, quality, cursor, duration, and explicit pressure/temperature minima and maxima. Live download progress and ETA remain in the Review header so metadata rows stay evenly spaced. Data values use a bundled, licensed Cascadia Mono face for stable live readouts. Cursor inspection is an explicit graph mode that can be restored after rectangle zoom, and downloaded files can be exported directly from Review.
- The header gear opens a compact menu for Serial Settings, read-only Gauge Settings, and Engineering Mode. Raw identity, transport, file-table, and calibration details remain outside the normal operator workflow.
- Initial serial setup is now a flat, wordmark-only screen containing just the port selector and Continue action. The default output folder has moved to App Settings. When no gauge responds, the normal shell collapses to a minimal `Disconnected` state with an enlarged sequential pulse around the official Northstar mark and a small settings menu for recovery. App Settings offers a calm four-second pulse by default while retaining the original fast animation for comparison.
- Engineering Mode can export a bounded support ZIP containing readable connection, runtime, identity, logical file-table, download-quality and calibration metadata plus the captured calibration payloads. It remembers its own last save folder and excludes downloaded job memory.
- The support bundle includes the latest 100 port-open, retry, retry-recovery and final transaction-failure events. Repeated equivalent events are coalesced over five seconds with first/last timestamps and occurrence counts, avoiding an unbounded raw serial log.
- Engineering Mode shows live communication integrity for the current connection session: completed transactions, retries, wire CRC errors, recovered transactions, failures, and the latest issue. A failed/disconnected session is frozen for inspection and support export until a new connection starts. The support ZIP carries the same summary with separate timeout, I/O, protocol, and port-access counts.
- Additional converted export formats are deferred until the required downstream/website format is known; legacy ASCII `.rec` remains the supported operator export.
- Automatic/manual priority, cancellation, retry, partial review, ETA accuracy, graceful close, and powered-gauge reconnect have been validated on live memory-gauge hardware. See `docs/LIVE_GAUGE_VALIDATION.md`.
- H0 recovery now has an explicit host lifecycle: every UI serial transaction has a seven-second operation deadline, sensor calibration has a ten-second deadline, and settings/cancel remain available while normal work is active. A failed download probes `IDENTIFY` after the transport's three attempts; loss of identity enters `Disconnected`, while a responding gauge leaves the file retryable.
- Disconnect recovery retains the current gauge's table, completed files, and partial raw download for ten seconds. Reconnecting the same device on the same port resumes from the retained byte offset; a different device or an expired window clears that state.
- Download recovery retains only complete confirmed packets. After the packet transport's three attempts fail, the app immediately selects the disconnected view before performing its short recovery `IDENTIFY` check. A valid same-device identity restores the file table and triggers one automatic retry from the retained byte offset; no response leaves the disconnected view active while discovery continues.
- Fast-link identity and memory reads use a 100 ms response/inter-byte timeout and a 500 ms whole-transaction deadline. At 460800 baud, the app's 1024-byte memory response takes about 22.4 ms on the wire and the firmware's 2048-byte maximum takes about 44.7 ms, including 8-N-1 framing. Three silent attempts plus retry delays therefore detect loss in roughly 340 ms. Sensor calibration retains its separate 2000 ms per-attempt and 7000 ms transaction allowances.
- Missing sensor calibration no longer blocks evidence capture. The app reports the sensor failure separately, downloads raw memory, and offers a `.raw` evidence export; calibrated plots and legacy ASCII `.rec` export remain unavailable until valid calibration is captured.
- `IDENTIFY` and `FIND_EOF` reject echoed or incomplete request frames. Identity requires a complete supported 22-byte memory-gauge or 32-byte acoustic-gauge payload, and `FIND_EOF` requires exactly four bytes.
- Main-window shutdown cancels and awaits polling, foreground work, and background downloads before disposing serial resources. The self-contained app was closed during COM8 activity on 21 July 2026; its process exited and COM8 was immediately openable by a second process.
- Firmware write commands have been classified by operator risk and readback requirements. Gauge Settings remains read-only until firmware can safely validate and apply interval changes at a clean file boundary; destructive and service commands remain isolated from the operator workflow. See `docs/GAUGE_SETTINGS_SAFETY.md`.
- Engineering Mode's existing read-only connection snapshot is now the first defined diagnostic procedure; future controls require similarly concrete procedures and expected results. See `docs/ENGINEERING_DIAGNOSTICS.md`.
- A cleaner state-driven UI is in progress: port setup first, disconnected state when no gauge responds, file-table view when connected, and focused graph review after download.

### Phase 6: Acoustic Gauge Support

- Add acoustic status view.
- Add last P&T readout.
- Add acoustic packet send/receive diagnostics.
- Add timestamp and node/status tooling.
- Keep acoustic-specific controls separate from normal memory-gauge workflows.

Deliverable: practical acoustic engineering tools.

Current status:

- Mixed acoustic-gauge memory files are safely classified by firmware record type. Only P&T records are calibrated and plotted; packet/bit-count data gets a ping indicator and raw ADC logging gets a scope-trace indicator. Multi-day files 6 and 14 have been validated on live acoustic hardware. See `docs/ACOUSTIC_GAUGE_VALIDATION.md`.

### Phase 7: Deployment And Future USB

- Package Windows installer or self-contained application.
- Define update process.
- Add crash/error log bundle export.
- Add USB transport implementation when hardware direction is known.
- Investigate macOS/Linux builds once Windows version is stable.

Deliverable: field-deployable application with a path beyond serial.

Current status:

- A repeatable `win-x64` self-contained publish script and zipped engineering distribution are available. The packaged executable has connected to live gauge hardware without the development runtime; clean-machine verification, code signing, installer selection, and update policy remain outstanding.
- The latest archive was rebuilt after support diagnostics were added and its packaged executable connected to the live acoustic gauge on COM5. Workshop pilots should continue with versioned self-contained ZIPs; routine field release should use Northstar signing, with a managed per-machine MSI deferred until field-image requirements are confirmed. Automatic updates remain deferred until offline and rollback ownership is defined.
- Engineering Mode now integrates the recovery-first memory-gauge firmware updater: production-offset image validation, device-serial confirmation, exclusive serial ownership, live progress, loader-only recovery state, post-reset identity verification, and firmware diagnostics in support bundles. The connected memory gauge and known production image were rechecked non-destructively after integration; staged interruption tests remain the release gate.

## Immediate Next Steps

1. Complete the H0 physical acceptance matrix: sensor absent, and gauge unplugged during table read, calibration, and early/mid/late download. Confirm `Disconnected`, COM release, same-gauge ten-second resume, and a usable settings/cancel route in every case.
2. Run staged bootloader interruption/recovery tests through the Engineering Mode control with hardware-programmer fallback, then package approved firmware with signed manifests. The first complete live update and application recovery passed at `115200`. See `docs/BOOTLOADER.md`.
3. Define the firmware changes needed for safe editable measurement intervals: sensor-specific limits, clean file boundary, immediate application, and failure recovery.
4. Verify the self-contained package on a clean Windows field laptop, confirm corporate/FTDI prerequisites, and start Northstar code-signing procurement before routine field distribution.
5. Keep additional export formats deferred until the website/downstream contract is known.

## TODO Reminder

- Finish the H0 unplug/sensor-absent lifecycle matrix on live hardware.
- Interruption-test the loader-only recovery path, then package approved images with signed manifests.
- Define and implement the firmware side of safe measurement-interval changes before enabling writes.
- Test the self-contained archive on a clean field laptop.

## Open Questions

- What is the largest memory download expected in normal field use?
- What file format does the website currently accept?
- Should exported jobs include both raw and converted data?
- How should field operators name jobs, wells, runs, or tools?
- Are multiple gauges ever connected at once?
- What safety confirmations are required before erase/reset/bootload commands?
