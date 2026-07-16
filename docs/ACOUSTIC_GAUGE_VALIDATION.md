# Acoustic Gauge Validation

Date: 16 July 2026

## Hardware

- Port: COM5 through the workshop USB-to-serial adapter.
- Acoustic gauge device serial: 1.
- Firmware: 20.1.
- Device type: 100200.
- PCB type: 100198; PCB serial: 1.
- File-table measurement interval: 3 seconds.

## Mixed Record Format

The acoustic firmware stores several 16-byte record types in the same file:

- Types 1-4: pressure and temperature.
- Types 5-7: received/failed/sent acoustic packets.
- Types 9-10: acoustic bit-count diagnostics.
- Type 11: raw acoustic ADC logging.
- Type 8: timestamp metadata.

Only types 1-4 are calibrated and plotted as P&T. Packet and bit-count records set the file-row ping indicator; type 11 sets the raw scope-trace indicator. Acoustic decoding is deliberately deferred, but these records are no longer misinterpreted as sensor counts.

## Live Files

### File 6

- Downloaded 4,367,552 bytes (272,972 records).
- Produced 545,912 P&T samples, so 16 non-P&T records were excluded.
- The ping indicator appeared; no raw ADC trace indicator appeared.
- Final duration: 454.9 hours at a 3-second interval.
- Pressure range: 20.65 to 23.55 psi.
- Temperature range: 15.88 to 26.44 C.
- The previous graph spikes were absent after mixed-record filtering.

### File 14

- Downloaded 3,769,936 bytes (235,621 records).
- Produced 471,242 P&T samples with no excluded records.
- No acoustic indicators appeared.
- Final duration: 392.7 hours at a 3-second interval.
- Pressure range: 17.83 to 22.44 psi.
- Temperature range: 7.06 to 22.56 C.
- Data quality reported no warnings.

## Operator Behaviour

- File-table and Review views both show the file's measurement interval.
- File-table duration remains fixed during download, then updates once from the final P&T sample count.
- The `Suggested` marker has been removed; files continue to default to descending file order.
- Expected acoustic metadata does not create a P&T data warning. Failed acoustic packets still produce a warning.

## Transport Note

The app's normal 1024-byte memory reads completed both multi-day files. A diagnostic CLI attempt using 2048-byte reads encountered repeated frame CRC failures, so 1024 bytes remains the verified default for this gauge/adapter combination.

## Engineering Support Bundle

The Engineering save workflow was exercised against the same live gauge. The resulting ZIP reported COM5 connected, device type 100200, serial 1, firmware 20.1, all 16 logical files, EOF `0x008E06F0`, and captured calibration. Its entries were `diagnostics.json` plus the four sensor calibration payloads, and the app persisted the selected support-bundle folder for the next export.
