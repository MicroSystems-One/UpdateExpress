# Update Express 0.7.2.52

## Dump tip (dbpu)

- Base: 0.7.2.51 dump (`f77a78c` / #111) + UPD-168 Codex from dump tip review
- Stack: #109 tray Påminnelse → #106 EXE vs TXT → #102 OEM catalogs → …
- Also retained: #99 Windows interval label, #95 App Control self-update

## UPD-168 Codex fixes (from dump PR #111 review)

- **P1** `AutomationStatePaths.TryMigrateFrom`: after successful copy (or when shared already wins), delete the user-dir source so `ClearPending` on ProgramData cannot be resurrected by a later elevate+migrate.
- **P2** `UpdateUntilCurrent.TryLaunchResume`: treat persisted phase `"scanning"` (what `RunUpdateUntilCurrentAsync` writes); keep `"searching"` for older state files.
- **P2** `UpdateUntilCurrent.TryLaunchResume`: before elevating resume, probe the live cross-process cycle mutex so a running installer/scan does not spawn repeated UAC / Main windows (mutex covers installing **and** scanning; follow-up from dump PR #112 Codex).

## Also in this dump (retained)

- **#109** — Tray Påminnelse when Main closed (+ Codex P2 ReminderPrompt mutex / schedule sync)
- **#106** — OEM Open download prefers package EXE; TXT readme on separate button
- **#102** — OEM Lenovo + Dell + HP catalogs
- **#99** — Norwegian Windows monitor interval label
- **#95** — App Control self-update via install-dir maintenance helper

## Build notes

- Built from full Windows feature tip (0.7.2.51 dump tree), not logo-only `main`. MicroSystems banner logo retained.
- Authenticode dry-run (no `UE_SIGN_MODE`).
- Critical: `latest.json` sha256 must match the published EXE asset byte-for-byte.
