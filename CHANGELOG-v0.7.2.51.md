# Update Express 0.7.2.51

## Dump tip (dbpu)

- Tip: `cursor/paminelse-tray-when-main-closed-522d` (PR #109) @ `cbd39fe` (includes Codex P2 after `c174cae`)
- Base: 0.7.2.50 dump (`c82b360`) + cherry-pick of #109 (`c174cae` + `cbd39fe`)
- Stack: #109 (tray Påminnelse) → #106 (EXE vs TXT) → #102 OEM catalogs → …
- Also retained: #99 Windows interval label, #95 App Control self-update

## Påminnelse from tray when Main is closed (#109)

- Root cause: reminder timer lived only on `MainWindow`; service+tray never presented the scheduled dialog.
- Tray polls `MaintenanceReminderHost` every 30s when Main UI is not registered (`MaintenanceReminderPromptGate`).
- Pending reason from monitor-status / winget cache / automation prompt queues (+ reboot / ≥7d uptime).
- Confirmed reboot still via `RebootCoordinator`.
- Preserved: single-instance Påminnelse + 30-minute AFK soft-dismiss.
- **Codex P2 (`cbd39fe`):** cross-process `ReminderPrompt` mutex while dialog open (Main does not stack a second); Main `CheckReminder` syncs snooze/confirm/`LastPrompted` from `schedule.json` on each tick.

## Also in this dump (retained)

- **#106** — OEM Open download prefers package EXE; TXT readme on separate button
- **#102** — OEM Lenovo + Dell + HP catalogs
- **#99** — Norwegian Windows monitor interval label
- **#95** — App Control self-update via install-dir maintenance helper

## Build notes

- Built from full Windows feature tip (#109 @ `cbd39fe` on 0.7.2.50 dump tree), not logo-only `main`.
- Authenticode dry-run (no `UE_SIGN_MODE`).
- Critical: `latest.json` sha256 must match the published EXE asset byte-for-byte.
