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

## Remaining Hardware Checks

- Physically unplug and reconnect a gauge while the app is open to verify the disconnected state and aggressive 57600-baud wake polling end to end.
- Repeat the workflow with a near-full gauge and a representative multi-day job.
- Verify the packaged build on a clean Windows PC with no .NET runtime installed.
