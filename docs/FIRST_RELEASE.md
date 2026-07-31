# First Downloadable Release

## Release identity

- Application version: `1.0.0`.
- Git tag: `app-v1.0.0` on the exact clean source commit used to build.
- Public distribution repository: `Inventable/IT_Releases`.
- GitHub release tag: `suite-v1.0.0`.
- Stable catalogue: `channels/stable.json`.

The version is defined once in `Directory.Build.props`, embedded in the
assembly and installer, and displayed in **App Settings** together with the
short source revision. The public manifest must record the exact application
and firmware source commits.

## Build and stage

From a clean, approved source commit:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\build.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\publish-windows.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\eng\prepare-download-repository.ps1 `
  -FirmwareHex <approved-universal-offset-production-hex> `
  -FirmwareVersion 3.0 `
  -FirmwareSourceCommit <40-character-approved-firmware-commit> `
  -ReleaseNotes "Initial public firmware release"
```

The normal publish creates:

- `dist/publish/win-x64/` — self-contained application payload.
- `dist/Northstar-Gauge-Interface-win-x64.zip` — portable fallback.
- `dist/Northstar-Gauge-Interface-Setup-1.0.0.exe` — per-user, single-file
  installer containing the .NET runtime.

The staging command creates `dist/download-repository/`. Upload everything in
`release-assets/` to GitHub Release `suite-v1.0.0`; commit `README.md`,
`channels/`, `schemas/`, and `.gitignore` to the public repository. Publish the
release assets before updating `channels/stable.json`.

## Firmware update behaviour

When a gauge is connected, **Device Management > Firmware > Check Online**:

1. Downloads the stable catalogue over HTTPS.
2. Matches device type and PCB type.
3. Accepts only `offset-production` entries.
4. Selects the newest compatible version.
5. Downloads the HEX and verifies its catalogue SHA-256.
6. Runs the existing address/layout validation.
7. Enables the existing explicit **Program Firmware** action.

Programming remains operator-confirmed. The bootloader updater retains its
erase-first, descending write, readback verification, start-vector-last, and
post-reset identity checks.

## Release gates

Software-complete gates:

- Full automated test suite passes.
- Release solution build passes with zero errors.
- Self-contained publish and installer compile succeed.
- Installer metadata and application version both report `1.0.0`.
- Installer and firmware hashes match `SHA256SUMS.txt` and `stable.json`.

External/manual gates before calling the release production-ready:

- Select the approved clean firmware `3.0` Offset artifact from the private
  firmware release procedure; never publish Combined or StandAlone HEX files.
- Complete the staged bootloader interruption/recovery matrix with hardware
  programmer fallback.
- Install/uninstall and connect to a gauge on a clean Windows 10/11 machine.
- Procure Windows Authenticode code signing. The first internal demonstration
  may be unsigned, but Windows will show an unknown-publisher warning.
- Purchase/record the installer-builder commercial licence when appropriate.
- Protect `IT_Releases/main`, enable GitHub release immutability, and require
  manual approval before changing the stable catalogue.
