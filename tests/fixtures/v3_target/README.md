# V3 target fixtures

These fixtures are immutable byte captures from PCB 100228 running the
PCB-100184 V3 functional-integration build. They let host applications
implement and regress the production V3 catalog/header/data path without a
connected gauge.

## Application fixtures

- `catalog-six-files-replica-0.bin` is the first 4 KiB catalog sector after six
  physical recording/service boots. Pages 0 through 5 are committed and page 6
  is erased.
- `latest-header-replica-0.bin` is the complete 4 KiB self-contained header
  extent for file `0x52330006`.
- `latest-data-replica-0.bin` is the first 16 data pages for that file. Four
  samples are committed; later pages are erased.

These captures came from `artifacts/v3-hil/20260724T231028Z` and
`artifacts/v3-hil/20260724T231043Z` while commit `9dfa7a208cc4` was programmed.

## BCH fixtures

- `bch-clean-data.bin` contains two clean committed pages.
- `bch-16-corrected-data.bin` is the matching target capture after 16 known
  data-bit clears in replica 0. Its first page must decode as corrected with
  exactly 16 corrected bits; the second page remains clean.
- `v3.1-compact-golden.hex` is the fixed 256-byte `MG3D` encoding-1 contract
  fixture. It contains a valid zero-count slot, an explicit null, non-zero
  24-bit counts, slot 32, CRC32C, and BCH16 parity.

These captures came from `artifacts/v3-hil/20260724T200644Z`.

Every `.expected.csv` file is the exact output expected from
`mg_log_inspect`. Run `tools/verify_v3_fixtures.ps1` after building the host
tools. SHA-256 values and provenance are in `manifest.json`.
