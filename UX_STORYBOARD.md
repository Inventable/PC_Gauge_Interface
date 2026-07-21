# Gauge Interface UX Storyboard

## Design Intent

The new application should be built around the operator's job, not around the internal engineering structure of the gauge.

The old LabVIEW screenshots are reference material for workflow and required data only. They should not drive the visual style. The new interface should feel calm, clear, rugged, and task-focused for workshop and field use.

Current operator journey:

```text
Select Serial Port -> Automatic Gauge Discovery -> File Table -> Review -> Export
```

Advanced diagnostics and engineering controls should be available, but they should not dominate the normal operator path.

## Current Operator Screens

### Serial Setup

- Show only the Northstar wordmark, serial-port selector, refresh icon, and `Continue`.
- Prefer the remembered port, then a likely FTDI adapter.
- Show a short message only when no serial ports are available.
- Keep the default output folder in App Settings, not on this screen.
- Keep baud rate and protocol detail out of the operator workflow.

### Disconnected

- Hide the normal connected header and file table.
- Show only the enlarged official Northstar mark, `Disconnected`, and a small settings gear.
- Poll the selected port aggressively at 57600 baud so a newly powered gauge enters serial mode before logging starts.
- After a valid slow `IDENTIFY`, wait briefly and verify the fast link before showing files.
- Keep App Settings and Serial Settings available from the gear.

The activity mark remains mostly solid red while a translucent band moves around it. App Settings remembers the selected mode:

- `Slow`: 3-second rotation, then 1 second fully solid.
- `Fast`: 1.5-second rotation, then 2.5 seconds fully solid.

### Connected File Table

- Show the Northstar header, connection state, device serial number, firmware version, and settings gear.
- Keep the table focused on file index, relative/numeric size, duration, interval, download state, and actions.
- Default to newest file first and allow sorting by file number or size from either the heading or sort icon.
- Allow files below 10 samples to be hidden.
- Download automatically from highest file index to lowest; an operator-requested file takes priority.
- Show per-file progress and ETA rather than a detached global progress panel.
- Use record-type icons for acoustic packet data and raw acoustic logging.
- Change the row action from download to graph when plot data becomes available; offer ASCII `.rec` export beside it.
- Keep partial and completed data attached to the correct file so reviewing one row never displays another file.

### Review

- Place the data summary on the left and the large graph on the right.
- Show file, duration, sample count, interval, status, one-line quality result, P/T minima and maxima, and cursor values.
- Keep live download progress and ETA in the Review header.
- Plot pressure on the labelled left axis and temperature on the labelled right axis with one subdued shared grid.
- Provide Fit, rectangle zoom, zoom in/out, pan/wheel, and cursor modes; cursor mode must remain recoverable after zooming.
- Make a partial graph available once enough calibrated records exist and refresh it at a restrained rate during transfer.
- Offer the same legacy `.rec` export action used in the file table.

### Settings And Engineering

- App Settings contains the default output folder and disconnected-animation mode.
- Serial Settings selects or reopens the current port.
- Gauge Settings remains read-only until firmware supports safe validated writes.
- Engineering Mode contains communication integrity, support bundle export, diagnostics, and recovery-first firmware update.
- Raw protocol information and destructive commands remain outside the normal operator path.

### Recovery Behaviour

- Foreground requests take priority, followed by automatic downloads, followed by idle liveness checks.
- Three failed transport attempts constitute a communication failure.
- A failed active operation shows `Disconnected` immediately, then performs a short identity recovery check.
- Complete packets and partial progress are retained; reconnecting the same gauge within 10 seconds resumes from the retained offset.
- Closing the main window cancels and awaits active work, releases the serial port, and exits the process.

Current implemented path:

```text
Setup -> Disconnected / Connected File Table -> Automatic or Manual Download -> Review -> .rec Export
```

## Historical Screen Concept (Superseded)

The screen-by-screen concept below is retained for design traceability. It predates the cleaner state-driven shell above and is not the current application layout.

### Screen 1: Connect

Purpose: get the operator connected with minimum decisions.

Primary content:

- Large connection state: `No gauge connected`, `Searching`, `Connected`, `Connection lost`.
- Auto-detected serial ports.
- Manual port selector for fallback.
- Baud/profile selector hidden behind `Advanced connection`.
- Primary action: `Connect`.
- Secondary action: `Refresh ports`.

Operator-friendly behaviour:

- Default to auto-detect where possible.
- Remember the last successful port.
- Show plain language errors such as `Gauge did not respond` or `Port is already in use`.
- Keep raw command errors out of the main view.

Engineering detail available:

- COM port.
- Baud rate.
- Raw identify command/reply.
- Timeout and retry count.

### Screen 2: Device Summary

Purpose: tell the operator what is connected and whether it is ready.

Primary content:

- Gauge type: Memory Gauge or Acoustic Gauge.
- Device description.
- Device serial number.
- Firmware version.
- Sensor type and serial number.
- Pressure and temperature rating.
- Measurement interval.
- Memory usage and estimated remaining time.
- Gauge health/status banner.

Primary actions:

- `Download data`.
- `View existing job`.
- `Export last job` if a local job is already open.

Warnings:

- Sensor calibration missing or invalid.
- Memory nearly full.
- Gauge has recorded errors.
- Battery/status warning if available.

Operator-friendly behaviour:

- Use status labels such as `Ready`, `Attention needed`, `Unsafe to erase`, or `Download recommended`.
- Keep file-record addresses visible only in a details panel.

### Screen 3: Choose Download

Purpose: let the operator download the right file/run without reading raw memory addresses.

Primary content:

- List of available runs/files.
- For each run: index, approximate start/end, sample count, interval, duration, warnings.
- Toggle to hide short/small files.
- Clear selection summary.

Primary actions:

- `Download selected run`.
- `Download all runs`.
- `Cancel`.

Engineering detail available:

- Record count.
- Start address.
- Raw rate.
- File record validity.
- Missing address details.

### Screen 4: Download Progress

Purpose: make the transfer feel trustworthy and recoverable.

Primary content:

- Progress bar.
- Records read vs expected.
- Current operation.
- Estimated time remaining.
- Error/correction count.
- Connection state.

Primary actions:

- `Cancel download`.
- `Retry` when recoverable.

Operator-friendly behaviour:

- Keep the UI responsive during long downloads.
- Save partial raw data in a recoverable form.
- Do not silently discard raw data when decoding fails.
- Use plain-language messages for failures.

Completion states:

- `Download complete`.
- `Download complete with warnings`.
- `Download failed`.
- `Download cancelled`.

### Screen 5: Download Result

Purpose: summarize what happened and guide the next step.

Primary content:

- Success/warning/failure status.
- Expected records.
- Records found.
- Uncorrected record errors.
- Corrected record errors.
- Missing address warning.
- Raw file saved location.
- Decoded job status.

Primary actions:

- `Review data`.
- `Export`.
- `Save diagnostic bundle` if warnings/errors occurred.
- `Back to device`.

Operator-friendly behaviour:

- Do not show only counters. Convert them into a meaningful status such as:
  - `No data errors found`.
  - `Some records were corrected`.
  - `Some records could not be recovered`.

### Screen 6: Review Data

Purpose: inspect pressure and temperature data clearly.

Primary content:

- Large pressure/temperature chart.
- Pressure on a directly labelled left axis and temperature on a directly labelled right axis.
- One subdued grid tied to the pressure and elapsed-time axes; do not draw competing pressure and temperature grids.
- Pressure and temperature traces with distinct colours and solid/dashed line styles.
- Time axis with editable start time.
- Cursor readout: timestamp, pressure, temperature.
- Units selectors for pressure and temperature.
- Summary values: min, max, first, last.
- Data-quality indicators.

Primary actions:

- `Fit`.
- `Zoom window`, zoom in/out, wheel zoom, and pan.
- `Cursor`.
- `Export`.
- `Open data table`.

Operator-friendly behaviour:

- Chart should be the main focus.
- Keep raw counts and CRC columns in the data table, not on the main chart.
- Allow quick confirmation that the downloaded job looks sensible before export.

### Screen 7: Export

Purpose: produce files for onward use.

Primary content:

- Export format choices:
  - CSV.
  - Text/raw-style export.
  - JSON.
- Destination folder.
- Job metadata preview.
- Include raw data checkbox.
- Include diagnostics checkbox.

Primary actions:

- `Export files`.
- `Open folder`.
- Future: `Upload to website`.

Operator-friendly behaviour:

- Use a predictable default filename based on date, gauge serial, and run number.
- Confirm exactly what was exported.
- Make website upload a later layer on top of the same local job model.

### Historical Engineering Mode

Engineering Mode should be explicit and separate from the main operator flow.

Possible entry:

- Menu item: `Engineering Mode`.
- Requires confirmation.
- Optionally later requires a password or unlock phrase.

Engineering pages:

- Raw comms log.
- Command history.
- Error log.
- Sensor tools.
- Acoustic packet tools.
- Advanced acoustic settings.
- Memory functions.
- Device programming.

Destructive actions requiring confirmation:

- Erase all files.
- Reset device.
- Enter bootloader.
- Program serial numbers.
- Change memory mode.
- Sensor pass-through mode.

### Historical First Implementation Target

The first usable application should implement only enough UI to support the core path:

```text
Connect -> Device Summary -> Download -> Review Data -> Export
```

At that stage, the command-line probe had proven the memory-gauge protocol path and the first Avalonia shell was moving toward a state-driven operator flow:

```text
Setup -> Disconnected / Connected File Table -> Download -> Review Graph
```

The next UX work identified at that stage was live-hardware validation and tighter progress, cancellation, graph status, warnings, and settings/engineering-mode separation. Those items have since been incorporated into the current design above.

The command-line probe exists to prove and preserve:

- Serial port enumeration.
- `IDENTIFY` command.
- Packet framing.
- CRC handling.
- Device response decoding.
