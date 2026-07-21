# Live Gauge Validation

Date: 15 July 2026

## Hardware

- Port: COM5 through the workshop USB-to-serial adapter.
- Memory gauge device serial: 3807522001.
- Firmware: 2.0.
- Device type: 100230.
- PCB type: 100228; PCB serial: 3807522063.
- Gauge remained powered and in serial mode throughout the test.

## Results

- The app connected at 460800 baud to an already-awake gauge and read the 11-entry file table.
- With `Ignore small files` enabled, the expected eight files with at least ten samples were shown.
- Sensor calibration was captured before file conversion.
- Automatic download started with the highest file index and continued in descending order.
- A 185.3 KB file produced 23,714 calibrated samples and completed in about 11 seconds.
- Its displayed ETA tracked the transfer from eight seconds remaining to one second remaining without large jumps.
- Partial graph data became available during the transfer and the completed review reported no data warnings.
- Automatic cancellation left the current file retryable and paused the remaining queue.
- Retry completed the file and resumed the automatic queue.
- An operator-selected file took priority over automatic work; cancellation and retry behaved the same way.
- Closing the app during an automatic download released COM5 in under one second.
- Restarting connected to the powered gauge in under one second and displayed the file table in about 1.5 seconds.
- The self-contained `win-x64` packaged executable launched without `dotnet`, connected to COM5, and displayed the file table in 0.8 seconds.
- Bootloader `1.3` programmed the validated Offset production firmware image at `115200` baud in approximately 62 seconds. All 992 application rows were erased and verified, 317 populated rows were programmed and verified in descending order, and `0x0800` was committed last. Firmware `2.0`, file-table access, calibration access, and P&T memory reads were confirmed after reset. See `docs/BOOTLOADER.md`.

## Remaining Hardware Checks

- With the sensor detached, confirm file-table discovery reaches raw download within the ten-second calibration deadline and that settings/cancel remain usable.
- Physically unplug and reconnect a gauge during table read, calibration, and early/mid/late download. Verify the disconnected state, aggressive 57600-baud wake polling, and same-device resume within ten seconds.
- During the download unplug cases, verify that the file row resumes automatically from its retained percentage without requiring the file-table Refresh action. A second operation failure after identity has recovered may remain a visible operator-retry error.
- Repeat the workflow with a near-full gauge and a representative multi-day job.
- Verify the packaged build on a clean Windows PC with no .NET runtime installed.

## H0 Host Lifecycle Check

Date: 21 July 2026

- The self-contained app opened Serial Settings from the disconnected operator view with COM8 retained.
- While COM8 discovery was active, the Settings menu remained usable; selecting Serial Settings cancelled the work and returned to setup without restarting the application.
- `Continue` remained enabled when COM8 was unchanged, allowing the app to close, flush, and reopen the selected port as a recovery action.
- The app was restarted on COM8 and its window was closed while connection work was active.
- The visible window and `Gauge.Interface.App` process both exited within two seconds.
- A separate process opened COM8 at 57600 baud immediately after shutdown, proving that the application had released the port.

This verifies the host shutdown and same-port recovery mechanics. The physical unplug and sensor-absent matrix above remains required before H0 automated download commissioning resumes.
