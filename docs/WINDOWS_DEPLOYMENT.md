# Windows Deployment

## Build

Create a 64-bit self-contained Windows build from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\publish-windows.ps1
```

The script creates:

- `artifacts\publish\win-x64\Gauge.Interface.App.exe` and its runtime files.
- `artifacts\Northstar-Gauge-Interface-win-x64.zip` for distribution.

The output includes the .NET runtime, so the target PC does not need a separate .NET installation. Keep the published folder together; do not distribute only the executable.

Use `-SkipArchive` while iterating locally. The existing `eng\app.ps1` remains the development command.

## Field Verification

Before calling a build field-ready:

1. Extract the archive on a clean Windows 10 or Windows 11 PC without a development runtime.
2. Start `Gauge.Interface.App.exe` and verify the Northstar branding and settings persistence.
3. Connect through the supported FTDI USB-to-serial adapter and verify slow wake, fast link, file table, automatic download, graph review, and `.rec` export.
4. Disconnect and reconnect the gauge while the app remains open.
5. Confirm Windows security policy, antivirus, and driver installation requirements on the field laptop image.

Code signing and an installer/update mechanism remain later deployment tasks. The self-contained archive is the first engineering distribution format.
