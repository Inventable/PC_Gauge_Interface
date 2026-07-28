# V3 Gauge Application Integration

## Status

On 27 July 2026 the completed storage-mode contract was validated against the
V3.0 PCB-100228 gauge on COM8 at 460800 baud. IDENTIFY reported mirrored mode;
command 73 reported storage end `0x01ff0000` and target mask `0x03`; all eight
files were listed; and the latest file downloaded as 1,338,880 raw bytes with
10,460 decoded samples, no corrected pages, and no alternate-page recovery.
Sensor Live then started, returned ten valid samples during a 15-second smoke
test, and stopped normally. The desktop protocol, decoding, erase, and user
interface work described below retains the existing V2 path.

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
3. Probe the non-preferred catalog's corresponding first-unused record and
   scan it further only when it contains a valid continuation.
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

An open V3 file is also a normal operator state: power removal is the usual
end-of-job transaction and no footer is required. The application must not
show **Review warnings** merely because a valid file is open. It warns only
for concrete conditions such as corrected or missing data, sequence gaps,
mirror divergence, failed acoustic packets, or a memory-service requirement.

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
- Command 70 supplies a preferred-chip hint and accumulated failed-chip mask.
- Clean preferred pages are decoded without reading the alternate copy.
- A corrected, malformed, CRC-failed, absent, or unexpected preferred page
  triggers a read of exactly the corresponding alternate page.
- Clean data is preferred over corrected data. Valid divergent copies are
  retained as evidence and produce a memory-service warning.
- Counts are converted through the existing Quartz calibration path and exposed
  through the same graph and record export used by V2.
- Automatic download runs newest-first and retains the existing cancellation,
  disconnect, and reconnect behaviour.
- Raw uncalibrated evidence is retained internally for diagnostics but no
  separate operator save icon is shown.

## Diagnostics

**Settings > Diagnostics** provides the review destination for every
**Review warnings** file state:

- **File Information** lists every downloaded file, its severity, and every
  applicable validation reason. Open-file state, page checksums, BCH
  corrections, missing scheduled readings, page/sample sequence gaps, mirror
  use/divergence, and memory-service requirements are stated explicitly.
- **Gauge Events** translates the latest command-70 journal event into
  operator language, shows current degradation/failover state, and reports
  whether the gauge holds a protected logging-fault capsule.

Event 13 is displayed as **Power removed or logging stopped** and is a normal
state. It must not use crash/failure language. Event 14 and genuine watchdog,
storage, recorder, or sensor failures remain warnings. Command 70 exposes the
presence and generation of the protected crash capsule but not its internal
context fields; the application states that limitation instead of inventing
details.

## Operator Interface

- The file table has no redundant title row.
- Refresh is in the file-table column header and is visible only while the
  file-table page is displayed.
- File discovery displays a dedicated **Downloading File Info** waiting page
  with the animated Northstar mark and PC-calculated completion percentage.
  It appears only after a gauge identity has been verified and the erase
  interlock is clear. The file table is revealed only after catalog and header
  validation finishes.
- The redundant green Connected badge is removed; device identity remains in
  the header summary.
- The connected header identifies the product from the reported device type:
  `100196` is **Constellation Q177**; `100160` and `100230` are
  **Constellation Q150**; and `100187` and `100200` are
  **Constellation Acoustic Quartz Gauge**. An unrecognised identity is shown
  as **Gauge Type NNNNNN** rather than being given a misleading product name.
- **Ignore small files** is stored under **App Settings** and persists between
  launches.
- One record-save action is shown after calibrated data is ready.
- **Gauge Settings** provides the requested interval presets plus a custom
  integer-seconds value, verifies command 46 by re-reading `IDENTIFY`, and
  displays an estimated record duration from remaining catalog capacity.
- Every storage-mode change enters the erase workflow and writes/verifies
  command 50 only after the erase interlock has cleared.
- V2 and V3 expose full-capacity and mirrored selections. V3 mode `1` uses lazy
  mirror fallback; mode `0` never probes mirror addresses. Command 73 must
  agree with `IDENTIFY`, target mask, and the mode-specific storage end.

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
has cleared. If command 65 clears an old interlock while preparing recovery,
the same verified gauge still starts a new command-64 erase from block zero.
Command 64 is sent only once: if its acknowledgement is lost, the application
uses command 65 as the authoritative readback instead of blindly starting a
second erase. Erase transactions use a 2-second response timeout and a 7-second
overall deadline because current firmware services the active flash operation
before replying to command 65.
Connection loss clears both discovery and erase overlays; reconnect performs
identity/interlock routing again. The firmware safety and performance contract is in
`docs/V3_ERASE_SAFETY_AND_PERFORMANCE.md`.

## Automated Evidence

The protocol test executable currently contains 75 passing checks. Coverage
includes:

- exact V3.1 command-73 parsing, capability fallback, and
  rejection of inconsistent geometry/mode/mask combinations;
- command-70 failed-chip read-order hints and service state;
- primary-only clean catalog recovery;
- chip-2-only publication and longer alternate catalog-prefix recovery;
- exact latest-file logical-end recovery from target captures;
- lazy mirror fallback;
- clean and 16-bit-corrected BCH pages;
- fixed full-codeword V3.1 golden bytes, all 33 slots, partial pages, valid
  zero counts, explicit null slots, Timer1 phase, and one-through-sixteen-bit
  BCH correction;
- corruption/torn-write rejection across the prefix, bitmap, records, CRC,
  parity, reserved bitmap, and null-record rules, including stale CRC with a
  newly generated BCH codeword;
- fixed schema-2 calibration field lengths, raw embedded NUL bytes,
  multi-page commit framing, whole-stream CRC32C, and malformed lengths;
- V3.2 encoding-2 CRC64 fallback and V3.0 generic CRC64 compatibility;
- CSV export rows that retain null slots with blank measurement fields and
  their scheduled fractional timestamp;
- completed-file recovery that reports an unrecoverable page but continues
  decoding later self-identifying pages instead of hiding the recoverable tail;
- six-file target catalog recovery;
- self-contained header calibration;
- open files without a footer;
- healthy open files do not become incomplete-file warnings, and diagnostic
  event wording distinguishes normal power removal from a logging fault;
- malformed required fields, sequence failures, padding failures, and
  over-limit BCH damage;
- lost command-64 acknowledgement recovery through command 65 without a
  destructive retry;
- Sensor Live payload and calibration-path decoding;
- V3 progress erase without retired commands 52/53, disconnect,
  restart-from-zero, and V2 fallback behaviour;
- V2/V3-compatible interval and storage-mode write payloads, post-write
  identity verification, and lost-acknowledgement recovery without a blind
  repeated write.
- V3 full-capacity downloads retain corrected pages, never issue a mirror
  read, and split command-24 reads at `0x02000000`.

Build, test, and self-contained Windows publish complete with zero compiler
warnings. The development scripts close running app instances first so COM and
publish files are released without screen automation.

## Remaining Live Checks

1. Open the desktop GUI on COM8 and confirm the CLI-validated firmware, mode,
   file sizes, and service warning render as expected.
2. Download and graph each file, then export the calibrated record.
3. Disconnect during discovery and early/mid/late download and verify immediate
   Disconnected recovery and automatic resume.
4. Repeat the incomplete-erase interruption matrix and verify restart from
   block zero, progress, completion detection, and cleared interlock.
5. Run Sensor Live for more than 60 seconds and confirm the rolling graph,
   stop cleanup, and unchanged deployment logging/catalog state.
6. Set a preset and custom sample interval and confirm the next recording
   header/catalog reports it.
7. On V2 and V3 firmware, change full/mirror mode with files present
   and confirm the app erases first, applies the new mode, and reports the
   corresponding capacity.
8. With V3 full mode active, cross the 32 MiB device boundary and confirm
   catalog discovery, latest-file logical-end recovery, download, and decode
   complete without mirror-address reads.
