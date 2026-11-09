# Update Express v0.7.2.6.7 – driver installation pilot

- Manual installation of **one** selected WUA driver marked `Newer` or WUA-assigned, after explicit confirmation.
- Recheck WUA UpdateID and hardware ID before download/install; no automatic restart.
- Firmware remains **blocked** pending separate hardware/power/BitLocker safety validation. Do not test firmware installation in this build.
- Priority for driver updates is `Unknown` when no trustworthy severity metadata is available; driver status remains separate.
- Existing WUA software update and WinGet paths preserved.

**Not tested on Windows in this environment.** Build and test only on a disposable test PC, with backup/restore plan. Do not distribute to customers.
