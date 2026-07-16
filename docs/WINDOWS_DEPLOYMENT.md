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

## Distribution Direction

- Continue using the self-contained ZIP for workshop engineering pilots; it is transparent and does not yet imply an update infrastructure.
- Sign the application and final package before routine field/operator distribution so Windows can identify Northstar as the publisher.
- Prefer a signed, per-machine MSI when managed field-laptop deployment is required. Installer implementation should wait until the clean field image confirms FTDI driver, install-location, privilege, antivirus, and corporate software-distribution requirements.
- Use explicitly versioned, manually installed releases initially. Do not introduce automatic updates until offline-field behaviour, rollback, and release ownership are defined.

## Field Verification

Before calling a build field-ready:

1. Extract the archive on a clean Windows 10 or Windows 11 PC without a development runtime.
2. Start `Gauge.Interface.App.exe` and verify the Northstar branding and settings persistence.
3. Connect through the supported FTDI USB-to-serial adapter and verify slow wake, fast link, file table, automatic download, graph review, and `.rec` export.
4. Disconnect and reconnect the gauge while the app remains open.
5. Confirm Windows security policy, antivirus, and driver installation requirements on the field laptop image.

Code-signing procurement and installer/update implementation remain deployment tasks. The self-contained archive is the engineering-pilot distribution format.

## Latest Local Preflight

On 16 July 2026 the current `win-x64` archive was rebuilt with 240 entries (49,273,925 bytes). Its executable was launched directly from the publish folder, without `dotnet`, and connected to the live acoustic gauge on COM5 as device 1 running firmware 20.1. The file table loaded with interval and duration columns. This confirms the current release payload locally; it does not replace the clean-machine checklist above.
