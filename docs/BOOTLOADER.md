# Gauge Firmware Bootloader

This document records the protocol implemented by `PIC_BOOTLOADER` for the PIC18F26K80. The desktop application must treat firmware update as an Engineering Mode operation, separate from normal gauge communication.

## Memory And Recovery Model

- The resident bootloader occupies the protected boot block below `0x0800`.
- Gauge application images are linked with their reset vector at `0x0800`.
- On reset, the bootloader enters update mode when EEPROM address `0x0000` is non-zero. It clears that flag as it enters.
- It also enters update mode when the byte at application address `0x0800` is erased (`0xFF`).
- Otherwise it jumps to the application at `0x0800`.

Firmware programming must therefore erase the application start block first, program all other application blocks from highest address to lowest address, verify them, and program the block containing `0x0800` last. A power loss before the final block is committed then leaves `0x0800` erased and the unit automatically returns to the bootloader.

The updater must never write bootloader addresses below `0x0800`.

## Serial Protocol

The loader uses hardware autobaud on every transaction. Live non-programming tests passed completely at both `57600` and `115200` baud. Use `115200` as the initial firmware-programming rate; it doubles the historical rate while retaining clean command acknowledgements and application recovery.

Each host request is:

```text
0x55 | command | length-le16 | key-1 | key-2 | address-le24 | unused | optional data
```

`0x55` is consumed by the PIC autobaud hardware. The remaining header is nine bytes. Loader frames do not carry the gauge application's CRC16.

The commands implemented by the loader are:

| Value | Command |
| ---: | --- |
| 0 | Read version |
| 1 | Read flash |
| 2 | Write flash |
| 3 | Erase flash |
| 4 | Read EEPROM |
| 5 | Write EEPROM |
| 6 | Read configuration |
| 7 | Write configuration |
| 8 | Calculate checksum |
| 9 | Reset device |

Flash erase and write require unlock keys `0x55`, `0xAA`. The current device reports 64-byte erase and write blocks and a 256-byte maximum packet size through the version command; the updater must use the values reported by the connected loader rather than assume them.

Some loader responses do not update the echoed header length. The host must use the expected response size for the command: version returns 16 payload bytes and reset returns one status byte.

## Retry Rules

- Read-only discovery and verification commands may use the normal three-attempt communication rule.
- Bootloader entry, reset, erase, write, and configuration writes must not be retried blindly after a missing acknowledgement.
- A failed state-changing command must be resolved using a readback or a fresh loader discovery before deciding whether to repeat it.
- A missing reset acknowledgement is resolved by immediately probing for the application at `57600`; reset is not repeated when application recovery proves it succeeded.
- The application start block must only be written after every other block has passed readback/checksum verification.

## Non-Programming Probe

The CLI probe validates loader entry, version discovery, reset, and application reacquisition without erasing or writing flash:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-probe COM5 460800 115200
```

The gauge must already have a verified application serial connection. The probe sends the application `BOOTLOAD` command once, reads loader version information, sends loader reset once, and aggressively reacquires the application at `57600` baud.

An already-running loader can be inspected or exited without first contacting the application:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-version COM5 57600
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\gauge-cli.ps1 bootloader-reset COM5 57600
```

## Live Validation

On 16 July 2026, memory gauge `3807522001` (application firmware `0.2`) completed non-writing bootloader entry, version discovery, reset, and application reacquisition at both `57600` and `115200` baud.

The loader reported:

- Bootloader version `1.3`.
- PIC device ID `0x6126`.
- Maximum packet size 256 bytes.
- Erase block size 64 bytes.
- Write block size 64 bytes.
- Configuration bytes `18827936`.

At `460800`, version discovery succeeded but the reset acknowledgement timed out. The reset command is intentionally never repeated blindly. The CLI now immediately checks for the application after reset even when its acknowledgement is missing. Until flash programming and interruption recovery have been characterized at higher rates, use the fully validated `115200` rate.

## Programming Work Still Required

Before firmware writing is exposed in the desktop application:

1. Parse and validate Intel HEX files, including device family and application address bounds.
2. Build an erased application image and reject data below `0x0800`.
3. Erase application rows while preserving the resident loader.
4. Program non-start rows in descending address order with readback verification.
5. Verify the complete non-start image using readback and loader checksum support.
6. Program and verify the `0x0800` start row last.
7. Reset and confirm the expected application identity and firmware version.
8. Test power interruption at erase, middle-write, pre-vector, and post-vector stages with a hardware programmer available for recovery.
