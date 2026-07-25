# V3 Gauge Application Integration

## Status

On 25 July 2026 the operator confirmed a live V3.0 gauge connection, Sensor
Live operation, and V3 logging. The desktop protocol, decoding, erase, and user
interface work described below is implemented while retaining the existing V2
path.

The latest logical-end correction is covered by target-fixture tests but still
requires a repeat live download to confirm the displayed sizes and row health
against the connected gauge.

## Protocol Selection and Compatibility

- The application probes command 73 after normal identity verification.
- A valid command-73 capability payload selects V3.
- The standard invalid-command response selects the unchanged V2 catalog,
  calibration, download, and decoding workflow.
- Firmware display uses the identity bytes in reverse wire order. For example,
  wire bytes `1,2` display as firmware `2.1`.
- Existing V2 erase is retained as the fallback when progress erase command 64
  is unsupported.

## V3 Catalog and File Boundaries

The application performs authoritative host-side recovery:

1. Read the append-only primary catalog with raw command 24.
2. Accept its valid monotonic prefix.
3. Read the catalog mirror only if the primary fails host validation.
4. Validate each referenced committed, self-contained file header.
5. Use the next catalog record as the allocation bound for completed files.
6. Locate the logical end inside the final allocated sector.
7. For the latest file, use a bounded sector-frontier search and then inspect
   the final sector.

A logical file ends at the first page that is not a BCH/CRC-valid,
structurally valid page for that file ID and expected page sequence. A missing
footer is healthy. Erased or torn bytes after the last committed page are not
part of the file and must not contribute to its displayed size or data-health
result.

This distinction is important because the next file starts on a sector
boundary. Treating that boundary as the preceding file's exact end includes
the unused tail of its last sector, producing false CRC/uncorrectable-page
errors. For the latest file, accepting an unrelated valid V3 page during the
frontier search can incorrectly extend the displayed size toward the end of
the device. The current implementation requires the current file ID and
expected page sequence and consults the mirror only after primary validation
fails.

## Atomic Download and Decoding

- Every file carries its own sensor serial, sensor header, pressure polynomial,
  and temperature polynomial.
- Calibration is built from that file's header; the currently connected sensor
  is not used to decode a V3 recording.
- Clean primary pages are decoded without reading the mirror.
- A corrected, malformed, CRC-failed, or unexpected primary page triggers a
  read of the corresponding mirror page.
- Counts are converted through the existing Quartz calibration path and exposed
  through the same graph and record export used by V2.
- Automatic download runs newest-first and retains the existing cancellation,
  disconnect, and reconnect behaviour.
- Raw uncalibrated evidence is retained internally for diagnostics but no
  separate operator save icon is shown.

## Operator Interface

- The file table has no redundant title row.
- Refresh is in the connected header bar.
- The redundant green Connected badge is removed; device identity remains in
  the header summary.
- **Ignore small files** is stored under **App Settings** and persists between
  launches.
- One record-save action is shown after calibrated data is ready.

## Sensor Live

**Settings > Sensor Live** reads calibration without starting deployment
recording, starts a temporary one-second live session, polls data-ready state,
decodes each new sample, and displays the latest pressure/temperature plus a
rolling 60-second graph. Closing the page stops the live session. Firmware
commands and safety requirements are documented in
`docs/V3_SENSOR_LIVE_PROTOCOL.md`.

## External-Memory Erase

The application prefers V3 progress erase commands 64/65 and falls back to the
V2 whole-chip erase workflow only when command 64 is unsupported. A nonzero
erase interlock bypasses normal file discovery and requires a fresh erase from
the beginning. Disconnect or an unresponsive gauge returns immediately to the
Disconnected page; success is shown only after identity confirms the interlock
has cleared. The firmware safety and performance contract is in
`docs/V3_ERASE_SAFETY_AND_PERFORMANCE.md`.

## Automated Evidence

The protocol test executable currently contains 52 passing checks. V3 coverage
includes:

- capability fallback;
- primary-only clean catalog recovery;
- exact latest-file logical-end recovery from target captures;
- lazy mirror fallback;
- clean and 16-bit-corrected BCH pages;
- six-file target catalog recovery;
- self-contained header calibration;
- open files without a footer;
- malformed required fields, sequence failures, padding failures, and
  over-limit BCH damage;
- Sensor Live payload and calibration-path decoding;
- V3 progress erase, disconnect, restart-from-zero, and V2 fallback behaviour.

Build, test, and self-contained Windows publish complete with zero compiler
warnings. The development scripts close running app instances first so COM and
publish files are released without screen automation.

## Remaining Live Checks

1. Refresh the confirmed V3 gauge and verify every displayed file size matches
   the committed data pages, especially the latest file.
2. Confirm completed files now report **Ready** unless a real page/calibration
   fault is present.
3. Download and graph each file, then export the calibrated record.
4. Disconnect during discovery and early/mid/late download and verify immediate
   Disconnected recovery and automatic resume.
5. Repeat the incomplete-erase interruption matrix and verify restart from
   block zero, progress, completion detection, and cleared interlock.
6. Run Sensor Live for more than 60 seconds and confirm the rolling graph,
   stop cleanup, and unchanged deployment logging/catalog state.
