# V3 Storage Mode Firmware Contract

## Current Compatibility

The desktop uses the existing memory-gauge commands for both V2 and V3:

| Command | Value | Required reply/readback |
| --- | --- | --- |
| `SET_MEASURE_RATE` (46) | Two-byte unsigned seconds, little endian | Reply `01`; `IDENTIFY[18..20]` equals the value |
| `SET_MEM_MODE` (50) | `00` full or `01` mirror | Reply `01`; `IDENTIFY[20]` equals the value |

The current PIC command service validates these payload lengths, persists the
values to internal EEPROM, updates `deviceData`, and returns them through
`IDENTIFY`. No new serial command is needed for the desktop settings screen.

V2 recording code honours memory mode `0` as 64 MiB full-capacity storage and
mode `1` as 32 MiB mirrored storage. The V2 desktop path can therefore change
either mode after external memory has been erased.

## V3 Limitation

The current V3 recorder writes every physical page to a primary and mirror
address and does not branch on `deviceData.memory_mode`. Command 50 can
therefore store and report full mode while logging remains mirrored. Until
firmware supplies real non-mirrored behaviour, the desktop restricts V3 to
mirrored mode.

## Firmware Work for V3 Full Capacity

To enable the V3 **Full capacity (64 MiB)** option safely:

1. Make the recorder choose its physical layout from the persisted memory mode
   before creating the first catalog/file record.
2. Define a full-mode address map that uses both 32 MiB devices as one logical
   64 MiB space and does not write a mirror replica.
3. Return the active layout or mode in V3 capabilities so the host does not
   infer layout solely from a mutable identity byte.
4. Define catalog/header placement and `storage_end` for both modes. Existing
   mirrored catalog recovery must remain unchanged.
5. Define read behaviour at the device boundary and ensure a single logical
   read cannot wrap from one device onto the other.
6. Ensure mode is sampled only when memory is empty. If a mode mismatch is
   found with a committed catalog, preserve the erase/deployment lockout rather
   than logging into an incompatible layout.
7. Add target tests for the final page on device 0, first page on device 1,
   catalog discovery, open-file logical-end search, download, and a power loss
   at the device boundary.

No “convert in place” path is required. The operator workflow always performs a
complete external-memory erase before changing mode, then writes command 50 and
verifies it with `IDENTIFY`.

## Measurement Interval Notes

The desktop accepts integer intervals from 1 through 65,535 seconds and treats
the value as the next recording's deployment schedule. Firmware should reject
zero even though the desktop already prevents it. If a particular sensor has a
narrower supported range, firmware should reject unsupported values with a
non-success reply and leave the previous EEPROM value unchanged.
