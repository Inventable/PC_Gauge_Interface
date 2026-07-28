# Windows Deployment

## Build

Create a 64-bit self-contained Windows build from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-windows.ps1
```

The build, test, development-run, and publish scripts close every running
`Northstar Gauge Interface` instance first. This releases the serial port and
published DLL file locks without using screen automation. They try a normal
window close for three seconds, then force-stop any remaining instance. They do
not stop unrelated `dotnet` processes. Compiler intermediates are written under
`%TEMP%\NorthstarGaugeInterface` to avoid stale or permission-restricted
repository `obj` directories.

To close the application without building:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\stop-gauge-app.ps1
```

Use `-KeepRunning` with `eng\build.ps1`, `eng\test.ps1`, or
`eng\publish-windows.ps1` only when retaining a running instance is intentional.

The script creates:

- `dist\publish\win-x64\Gauge.Interface.App.exe` and its runtime files.
- `dist\Northstar-Gauge-Interface-win-x64.zip` for distribution.

The output includes the .NET runtime, so the target PC does not need a separate .NET installation. Keep the published folder together; do not distribute only the executable.

Use `-SkipArchive` while iterating locally. The existing `eng\app.ps1` remains the development command.

## Distribution Direction

- Continue using the self-contained ZIP for workshop engineering pilots; it is transparent and does not yet imply an update infrastructure.
- Sign the application and final package before routine field/operator distribution so Windows can identify Northstar as the publisher.
- Prefer a signed, per-machine MSI when managed field-laptop deployment is required. Installer implementation should wait until the clean field image confirms FTDI driver, install-location, privilege, antivirus, and corporate software-distribution requirements.
- Use explicitly versioned, manually installed releases initially. Do not introduce automatic updates until offline-field behaviour, rollback, and release ownership are defined.

## Field Verification

Before calling a build field-ready:

1. Extract the archive on a clean Windows 10 or Windows 11 PC without a development runtime.
2. Start `Gauge.Interface.App.exe` and verify the Northstar branding and settings persistence.
3. Connect through the supported FTDI USB-to-serial adapter and verify slow
   wake, fast link, the device-type product title, file table, automatic
   download, graph review, and `.rec` export.
4. Disconnect and reconnect the gauge while the app remains open.
5. Confirm Windows security policy, antivirus, and driver installation requirements on the field laptop image.

Firmware Update is a Device Management pilot feature. Do not approve it for routine field deployment until the erase, middle-write, pre-vector, and start-vector interruption/recovery cases in `docs/BOOTLOADER.md` have passed with hardware-programmer fallback available.

Code-signing procurement and installer/update implementation remain deployment tasks. The self-contained archive is the engineering-pilot distribution format.

## Latest Local Preflight

On 16 July 2026 the current `win-x64` archive was rebuilt with 240 entries (49,273,925 bytes). Its executable was launched directly from the publish folder, without `dotnet`, and connected to the live acoustic gauge on COM5 as device 1 running firmware 1.20. The file table loaded with interval and duration columns. This confirms the current release payload locally; it does not replace the clean-machine checklist above.

The unpacked `dist\publish\win-x64` engineering build can be newer than that validated ZIP when `-SkipArchive` is used. On 22 July 2026 it was rebuilt with the wordmark-only serial setup, minimal animated disconnected state, App Settings, and the selectable Slow/Fast activity timings documented in `UX_STORYBOARD.md`. That UI build compiled successfully; it has not yet replaced the clean-machine or live-gauge release evidence above.

On 25 July 2026 the self-contained engineering build was updated with V3
catalog/header/data decoding, file-local calibration, newest-first automatic
download, progress erase/recovery, and Sensor Live. The operator confirmed live
V3.0 connection, Sensor Live, and logging. On 26 July the build was republished
after correcting V3 logical-end recovery and simplifying the file-table
controls. The 26 July settings build also adds verified sample-interval writes,
remaining-record-time estimates, and erase-gated V2 storage-mode changes while
enabling the same full/mirrored selection for V3 firmware. On 27 July the
application was updated for the completed V3.0 storage contract: exact
command-73 mode/target parsing, command-70 failover hints, lazy corresponding
page recovery, longer alternate catalog-prefix discovery, and full-mode
command-24 boundary splitting. V3 full mode uses the linear address space
through `0x03feffff` without mirror reads. File discovery now uses a dedicated
animated waiting page with host-calculated progress. All 75 automated protocol
checks pass and the build has zero compiler warnings. The hardware checks listed in
`docs/V3_APPLICATION_VALIDATION.md` remain engineering validation requirements;
this is not a signed field release.

On 28 July 2026 the operator review and diagnostics presentation was completed.
Healthy V3 files display **No errors**, normal power removal is not presented as
a crash, and warning details are available under **Settings > Diagnostics**.
**Device Management** now reports V2 file-table or V3 catalog state and
file-local V3 calibration, and support bundles include V3 catalog, per-file
calibration, file-quality, command-70 event, and protected-report status. The
file-list refresh control is visible only on the file-table page. The connected
header uses the reported device type to display **Constellation Q177**,
**Constellation Q150**, or **Constellation Acoustic Quartz Gauge**. The
self-contained Windows archive was rebuilt after all 75 automated checks passed
with zero compiler warnings.
