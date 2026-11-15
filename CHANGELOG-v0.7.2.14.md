# Update Express 0.7.2.14

- Fix: after WinGet tech-mismatch (e.g. PowerShell) when you install via Microsoft Store, the main window no longer hangs with `_busy` stuck. Skips blocking WinGet rescan when all WinGet rows failed; background refresh; drain timeout ~20s unlocks UI.
- Includes 0.7.2.13: Failures Fix it/Close 27px; Failures lists IM failures; All unified/default; IM Status help.

Build: `BYGG-EXE.cmd` → `Publisert\UpdateExpress_v0.7.2.14.exe`
