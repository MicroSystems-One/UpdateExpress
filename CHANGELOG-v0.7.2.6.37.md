# Update Express v0.7.2.6.37

- Phase 2a self-update from tray: Install / Open download / Later.
- HTTPS download to temp, SHA-256 verification, version checks.
- Replace installed EXE via maintenance `upgrade` helper.
- Preserve history, settings, shortcuts and agent preference.
- After upgrade: restart agent (if enabled) and open main window.
- No automatic Windows restart. No Authenticode requirement yet.
