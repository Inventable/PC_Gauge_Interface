# V3 Storage Mode Application Contract

## Implemented Firmware Contract

Memory Gauge V3.0 implements both storage layouts and reports the active
layout through `IDENTIFY` and command 73:

| Mode | Command-50 value | Logical storage end | Write-target mask |
| --- | ---: | ---: | ---: |
| Full capacity / non-mirrored | `0` | `0x03ff0000` | `0x01` |
| Mirrored | `1` | `0x01ff0000` | `0x03` |

The desktop parses the exact 32-byte command-73 schema. Capability flag bit 4
announces the diagnostic journal, byte 29 is the active memory mode, byte 30 is
the configured write-target mask, and only byte 31 remains reserved. Unknown
flags, modes, impossible geometry, and inconsistent mode/mask/end combinations
are rejected as malformed V3 data rather than treated as V2.

Command 73 is capability discovery for the connected gauge, not a decoder hint
to reinterpret bytes. The application requires format `3.1`, 33 scheduled
slots, BCH limit 16, checksum ID 2, and the mode-specific fields above. Unknown
minor versions or encodings fail with an unsupported-format error. Historical
V3.0 files remain readable through their own page magic/version/checksum.

## Storage Decoder Support

| Stored page | Selection | Integrity | Samples |
| --- | --- | --- | --- |
| V3.0 `MG3P` minor 0 DATA | explicit page minor | BCH16 then CRC64/ECMA | up to 18 ten-byte records |
| V3.1 `MG3D` encoding 1 | explicit encoding ID | BCH16 then CRC32C | up to 33 six-byte bitmap slots |
| V3.2 fallback `MG3D` encoding 2 | explicit encoding ID | BCH16 then CRC64/ECMA | up to 32 six-byte bitmap slots |

Generic V3.1 header, catalog, footer, checkpoint, and diagnostic pages use
`MG3P` minor 1 and CRC32C. Fixed calibration schema 2 preserves the exact raw
SER/HDR/PLP/PLT response bytes; schema-1 TLV headers remain read-compatible.
No decoder is selected from payload length or firmware version.

## Guarded Mode Change

Changing either V2 or V3 storage mode always uses the erase screen. The
application:

1. warns that every recording will be destroyed;
2. completes the whole-memory erase;
3. sends command 50 once with exactly one byte (`0` or `1`);
4. re-reads `IDENTIFY` and verifies `memory_mode`;
5. for V3, reads command 73 and verifies mode, target mask, and storage end;
6. reports the gauge ready only when all readbacks agree.

There is no in-place conversion, including when the loaded catalog appears
empty. This keeps the operator workflow independent of stale catalog state and
ensures the diagnostic journal and persistent failover capsule are cleared.

V3 uses progressive erase commands 64/65. Commands 52 and 53 are not used on
the V3 production path. The existing V2 path retains commands 30/52/53 as its
compatibility fallback.

## Full-Capacity Reads

Full mode is a single linear command-24 address space:

- catalog: `0x00000000` through `0x0000ffff`;
- file allocation: `0x00010000` through `0x03feffff`;
- diagnostic journal: `0x03ff0000` through `0x03ffffff`.

The host splits a command-24 request at physical boundary `0x02000000`; no
individual transaction crosses from one flash device to the other. Full mode
never calculates or requests a mirror fallback address.

## Mirrored Recovery

In mirror mode logical address `A` maps to chip 1 at `A` and chip 2 at
`A + 0x02000000`. The command-70 persistent failed-chip mask determines the
preferred read order but does not override page validation:

- mask `0x01`: prefer chip 2;
- mask `0x02`: prefer chip 1;
- mask `0x03`: probe both and require service.

The desktop validates the preferred page first and requests only the
corresponding alternate page when recovery is required. A clean valid copy is
preferred over a corrected valid copy. Divergent valid copies and any recovered
operation produce the operator warning **Data recovered; memory service and
complete erase required**.

Catalog discovery also probes the non-preferred first-unused record and scans
that catalog further only when it contains a valid continuation. This preserves
a longer committed prefix without routinely reconstructing both complete
catalogs.

No “convert in place” path is required. The operator workflow always performs a
complete external-memory erase before changing mode, then writes command 50 and
verifies it with `IDENTIFY`.

## Measurement Interval

Command 46 writes an unsigned 16-bit interval in seconds. The desktop accepts
whole-number intervals from 1 through 65,535 seconds, writes the command once,
and verifies the result with `IDENTIFY`. Firmware rejection or inconsistent
readback leaves the setting unapplied.
