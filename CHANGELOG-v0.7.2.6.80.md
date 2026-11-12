# 0.7.2.6.80

## Applications — faster auto-check + main UI sync (UPD-30)

- Automatic WinGet Applications scan cadence default **240 minutes** (4 hours), hard floor **60 minutes** (`ApplicationsIntervalMinutes` in policy). Scan NextDue uses interval minutes — no Critical preferred-time snap for scan scheduling. Ask-before-install unchanged.
- Tray `ApplicationsAutomation` writes `winget-available.json` after each scan (including NonePending). Open MainWindow applies the cache on activate / monitor tick / same-process `Changed`, so Applications tab and summary match without a second manual Check.
- After successful WinGet install (main Apps install, Installation Manager combined path, or elevated Applications helper) + rescan: packages leave `_appEntries`, the shared cache, and `applications-prompt.json` when no longer offered.
- Codex P2: `winget-available.json` writes are atomic (temp + replace) and non-fatal; post-install rescan failure preserves the previous snapshot (no empty wipe).

Built from full feature tip (`.79` IM verify + `.78` Not verified + `.77` dialog language + `.76` logo + `.75` Drivers/Apps + `.74` Bitdefender ATC).
