# Update Express – Phase 1 app update check (source change on 0.7.2.6.35 baseline)

- Rename tray item to **Sjekk Windows-oppdateringer** (existing WUA scan).
- Add tray item **Sjekk Update Express-oppdatering**.
- Read public channel: `https://raw.githubusercontent.com/BlackAdder5/UpdateExpress-Releases/main/latest.json`
- Show up-to-date / update-available / check-failed dialogs.
- Optional open of HTTPS download URL in the browser.
- No automatic download into the install directory.
- No executable replacement.
- No version number bump in this change set; rebuild/publish when approved.
