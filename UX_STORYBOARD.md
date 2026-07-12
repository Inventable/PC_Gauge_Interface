# Gauge Interface UX Storyboard

## Design Intent

The new application should be built around the operator's job, not around the internal engineering structure of the gauge.

The old LabVIEW screenshots are reference material for workflow and required data only. They should not drive the visual style. The new interface should feel calm, clear, rugged, and task-focused for workshop and field use.

Primary operator journey:

```text
Connect -> Confirm Gauge -> Download -> Review -> Export
```

Advanced diagnostics and engineering controls should be available, but they should not dominate the normal operator path.

## Screen 1: Connect

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

## Screen 2: Device Summary

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

## Screen 3: Choose Download

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

## Screen 4: Download Progress

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

## Screen 5: Download Result

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

## Screen 6: Review Data

Purpose: inspect pressure and temperature data clearly.

Primary content:

- Large pressure/temperature chart.
- Pressure and temperature traces with distinct colours.
- Time axis with editable start time.
- Cursor readout: timestamp, pressure, temperature.
- Units selectors for pressure and temperature.
- Summary values: min, max, first, last.
- Data-quality indicators.

Primary actions:

- `Fit`.
- `Zoom`.
- `Cursor`.
- `Export`.
- `Open data table`.

Operator-friendly behaviour:

- Chart should be the main focus.
- Keep raw counts and CRC columns in the data table, not on the main chart.
- Allow quick confirmation that the downloaded job looks sensible before export.

## Screen 7: Export

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

## Engineering Mode

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

## First Implementation Target

The first usable application should implement only enough UI to support the core path:

```text
Connect -> Device Summary -> Download -> Review Data -> Export
```

Before the full desktop UI, build a command-line probe to prove:

- Serial port enumeration.
- `IDENTIFY` command.
- Packet framing.
- CRC handling.
- Device response decoding.

Once the protocol is proven, the first UI can be built around the operator storyboard above.

