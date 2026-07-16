# Engineering Diagnostics

Engineering Mode is for named troubleshooting procedures with an expected result. It is not a raw serial-command console.

## Connection Snapshot

Use the connection snapshot when the gauge appears connected but file discovery, calibration, or device identification is suspect.

Open **Settings > Engineering Mode** after the normal connection attempt. The snapshot reports:

- Selected serial port and fast-link baud rate.
- Parsed file-table entry count and end-of-file address.
- Whether sensor calibration was captured.
- Firmware, device and PCB identity, measurement interval, memory mode, erase state, and raw identify bytes.

Expected healthy result:

- Transport shows the selected COM port at 460800 baud.
- File table is available and its entry count is plausible for the gauge.
- Sensor calibration says `Captured`.
- Device identity fields contain values rather than placeholders.

If transport is unavailable, return to Serial Settings and verify the adapter/port before testing the gauge. If the file table is unavailable but identity is valid, investigate memory-table communications. If calibration is not captured, investigate sensor power and sensor communications before trusting converted P&T data.

Use **Save Support Bundle** to preserve this evidence as a timestamped ZIP. The app remembers the last support-bundle folder. The archive contains:

- `diagnostics.json` with application/runtime details, selected transport, parsed gauge identity, complete logical file table, download and data-quality state, and parsed calibration metadata.
- The four captured sensor calibration payloads under `calibration/`, when calibration is available.

The archive is intentionally bounded and does not duplicate downloaded gauge memory or exported jobs. It may be saved while disconnected so the last captured state remains available for troubleshooting.

This procedure is read-only. A bounded recent communication-event log can be added after the transport has a structured logging model. Memory tests, sensor pass-through, reset, erase, and bootloader actions require separate documented procedures before UI controls are added.
