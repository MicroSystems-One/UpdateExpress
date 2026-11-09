# Update Express v0.7.2.6.29 – Installation Manager preview

- Shared selection across Windows Updates, Drivers/Firmware and WinGet.
- Separate non-modal Installation Manager with live stage text, elapsed time, queue and package statuses.
- WUA and WinGet processed sequentially, with rescans after the run.
- Combined run ID in ActivityLog and local installation-manager.jsonl.
- Firmware remains blocked, drivers limited to one per run with separate warning. No automatic reboot.

IMPORTANT: This package has not been compiled on Windows. Run BYGG-EXE.cmd and report compiler/runtime errors.
