# Update Express v0.7.0 – installation scope preview

Based on v0.6.0.20.

- New installer options dialog: current user / all users, desktop and Start Menu shortcuts, optional per-user tray monitoring.
- Machine-wide installation targets Program Files, requires UAC, writes HKLM uninstall registration and common shortcuts.
- Current-user installation retains AppData/Local and HKCU uninstall registration.
- Run mode does not create application installation or register tray autostart.
- Agent state stays in user-writable AppData rather than Program Files.
- All-users background monitoring deliberately disabled pending a safe per-user enrollment mechanism.
- No automatic update installation added.

## Important limitations / testing
- Windows WPF build is not verified in this environment.
- Test current-user installation first on a disposable VM, then all-users with a separate test VM snapshot.
- Cross-scope migrations, upgrades with concurrent sessions, and uninstall across multiple Windows accounts are NOT validated; do not deploy to customer systems yet.
- The full activity log, portable-mode desktop export, scheduled installations and per-user agent enrollment for all-users installs are not implemented in this preview.
