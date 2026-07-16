# Engineering Diagnostics

Engineering Mode is for named troubleshooting procedures with an expected result. It is not a raw serial-command console.

## Connection Snapshot

Use the connection snapshot when the gauge appears connected but file discovery, calibration, or device identification is suspect.

Open **Settings > Engineering Mode** after the normal connection attempt. The snapshot reports:

- Selected serial port and fast-link baud rate.
- Parsed file-table entry count and end-of-file address.
- Whether sensor calibration was captured.
- Firmware, device and PCB identity, measurement interval, memory mode, erase state, and raw identify bytes.
- Communication integrity for the current connection session: completed transactions, retry attempts, wire-frame CRC errors, recovered transactions, final failures, and the last issue.

Expected healthy result:

- Transport shows the selected COM port at 460800 baud.
- File table is available and its entry count is plausible for the gauge.
- Sensor calibration says `Captured`.
- Device identity fields contain values rather than placeholders.
- Communication Integrity says `Good`; retries, CRC errors, recovered transactions, and failures are zero.

`Review` means the session needed one or more retries but has not suffered a final transaction failure. `Error` means a transaction exhausted all attempts or the serial port could not open. If that failure disconnects the gauge, the panel retains and labels the ended session rather than allowing aggressive wake polling to erase the evidence. A newly started connection resets the counters.

If transport is unavailable, return to Serial Settings and verify the adapter/port before testing the gauge. If the file table is unavailable but identity is valid, investigate memory-table communications. If calibration is not captured, investigate sensor power and sensor communications before trusting converted P&T data.

Use **Save Support Bundle** to preserve this evidence as a timestamped ZIP. The app remembers the last support-bundle folder. The archive contains:

- `diagnostics.json` with application/runtime details, selected transport, parsed gauge identity, complete logical file table, download and data-quality state, parsed calibration metadata, a connection-session integrity summary, and recent communication events.
- The four captured sensor calibration payloads under `calibration/`, when calibration is available.

The session summary separates wire CRC, timeout, I/O, protocol, port-access, and other error counts, and includes the last issue even after disconnection. The detailed history records port-open failures, transaction retries, recovery after a retry, and final three-attempt failures. Each item includes port, baud, command, attempt count, failure category, exception type, first/last UTC timestamps, and occurrence count. Equivalent events within five seconds are coalesced, and only the latest 100 entries are retained. Successful transactions contribute to the summary but are not written as individual events.

The archive is intentionally bounded and does not duplicate downloaded gauge memory or exported jobs. It may be saved while disconnected so the last captured state and failure history remain available for troubleshooting. A healthy session may legitimately contain no communication events.

This procedure is read-only. Memory tests, sensor pass-through, reset, erase, and bootloader actions require separate documented procedures before UI controls are added.
