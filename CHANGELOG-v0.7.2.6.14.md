# Update Express v0.7.2.6.14

- Move N-Able PME beside Activity Log in the Results toolbar; consistent button height.
- Move Include optional and All WUA drivers beneath the Results toolbar; default checked; existing selection safety unchanged.
- Enable Proxy Settings from the system tray; share the same dialog with the main-window proxy icon.
- Display separate WinHTTP machine-wide and current-user PAC controls; explicit confirmation for changes.
- Support WinHTTP direct access / host:port via elevated netsh and current-user PAC URL configuration.
- Add read-only diagnostics, local JSON backup, and PAC restore for the same Windows user.
- WinHTTP restore is manual from the saved netsh output. No background proxy changes.
- Build batch output version updated; no Windows runtime verification in Linux environment.
