# Update Express – Phase 2a self-update (source on 0.7.2.6.36 baseline)

- Tray update dialog: **Yes = Install**, **No = Open download**, **Cancel = Later**.
- Download package to temp over HTTPS.
- Require SHA-256 from `latest.json` and reject mismatches.
- Verify downloaded product version is newer than installed.
- Replace via existing maintenance helper (`upgrade`), preserving history/settings/shortcuts/agent.
- After upgrade: restart background agent (if enabled) and open main window.
- No automatic Windows restart.
- No Authenticode requirement yet (Phase 2b).

Note: Install requires a build that already contains Phase 2a. Use bump/publish before customer testing.
