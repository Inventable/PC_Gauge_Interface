# Device Management and Diagnostics

Device Management is for named troubleshooting and maintenance procedures
with an expected result. It is not a raw serial-command console.

Routine operator diagnostics are available separately under
**Settings > Diagnostics**:

- **File Information** is the authoritative review page for **Review
  warnings**. It names every active warning or error and also records the
  healthy V3 validation evidence.
- **Gauge Events** presents the current command-70 health status, the last
  recorded deployment event, and whether a protected logging-fault capsule is
  available.

A V3 recording ending after power removal is ordinary operation. Event 13 is
shown as **Power removed or logging stopped**, not as a crash. A missing footer
on an otherwise valid open file is healthy and does not create a warning.
Command 70 reports a protected capsule's presence and generation but does not
expose the capsule context fields; the operator page identifies this protocol
limit.

## Connection Snapshot

Use the connection snapshot when the gauge appears connected but file discovery, calibration, or device identification is suspect.

Open **Settings > Device Management** after the normal connection attempt. The snapshot reports:

- Selected serial port and fast-link baud rate.
- The loaded V2 file table or V3 catalog, including its actual storage format.
- Connected-sensor calibration for V2, or the number of validated file-local
  calibration headers for V3.
- Firmware, device and PCB identity, measurement interval, memory mode, erase state, and raw identify bytes.
- Communication integrity for the current connection session: completed transactions, retry attempts, wire-frame CRC errors, recovered transactions, final failures, and the last issue.

Expected healthy result:

- Transport shows the selected COM port at 460800 baud.
- File table is available and its entry count is plausible for the gauge.
- V2 sensor calibration says it was captured from the connected sensor. V3
  calibration reports the number of validated file-local calibration records;
  an empty V3 catalog explains that calibration will be read from each future
  file rather than reporting a false capture failure.
- Device identity fields contain values rather than placeholders.
- Communication Integrity says `Good`; retries, CRC errors, recovered transactions, and failures are zero.

`Review` means the session needed one or more retries but has not suffered a final transaction failure. `Error` means a transaction exhausted all attempts or the serial port could not open. If that failure disconnects the gauge, the panel retains and labels the ended session rather than allowing aggressive wake polling to erase the evidence. A newly started connection resets the counters.

If transport is unavailable, return to Serial Settings and verify the adapter/port before testing the gauge. If the file table is unavailable but identity is valid, investigate memory-table communications. If calibration is not captured, investigate sensor power and sensor communications before trusting converted P&T data.

Use **Save Support Bundle** to preserve this evidence as a timestamped ZIP. The app remembers the last support-bundle folder. The archive contains:

- `diagnostics.json` with application/runtime details, selected transport,
  parsed gauge identity, V2 file-table or V3 catalog state, every file's
  download and data-quality result, command-70 gauge-event status, parsed
  calibration metadata, a connection-session integrity summary, and recent
  communication events.
- Connected-sensor calibration payloads under `calibration/` for V2.
- Each atomic V3 file's calibration payloads under
  `calibration/file-NNN/`.

The session summary separates wire CRC, timeout, I/O, protocol, port-access, and other error counts, and includes the last issue even after disconnection. The detailed history records port-open failures, transaction retries, recovery after a retry, and final three-attempt failures. Each item includes port, baud, command, attempt count, failure category, exception type, first/last UTC timestamps, and occurrence count. Equivalent events within five seconds are coalesced, and only the latest 100 entries are retained. Successful transactions contribute to the summary but are not written as individual events.

The archive is intentionally bounded and does not duplicate downloaded gauge memory or exported jobs. It may be saved while disconnected so the last captured state and failure history remain available for troubleshooting. A healthy session may legitimately contain no communication events.

The diagnostic snapshot and support-bundle export are read-only. Firmware
programming remains a separately confirmed Device Management operation.
