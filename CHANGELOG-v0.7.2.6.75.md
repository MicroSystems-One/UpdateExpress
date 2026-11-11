# v0.7.2.6.75 — Drivers / Applications automatic (download + ask)

- **Drivers automatic** checkbox is live: when enabled (and Windows automation is on), the tray agent scans assigned WUA drivers, downloads in the background, and asks before install (preferred time follows Critical, default 12:00). Firmware stays manual.
- **Application automatic** checkbox is live: when enabled, WinGet upgrades are scanned on the same schedule and require confirmation before elevated `winget upgrade`. Empty package whitelist = all pending upgrades.
- Removed “(optional, not implemented)” labels; EN/NO strings updated.
- Elevated helpers: `--apply-drivers-install`, `--apply-applications-install`.
- Status line includes Drivers / Apps results; settings persist via existing `update-policy.json`.
