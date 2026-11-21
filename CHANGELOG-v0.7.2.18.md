# Update Express 0.7.2.18

- Silent install for RMM: `--apply-maintenance … --silent` (or `UE_SILENT=1`) skips UI dialogs; `parentPid 0` skips wait.
- N-able helper script: `scripts/Install-UpdateExpress-Nable.ps1` — silent install, disable PME, seed business Pilot policy (07–17 / daily 17:00 / reboot outside hours).

Build: `BYGG-EXE.cmd` → `Publisert\UpdateExpress_v0.7.2.18.exe`
