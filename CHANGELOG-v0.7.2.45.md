# Update Express 0.7.2.45

## Unattended «update until clear»

- Confirm **Install at HH:mm** (Applications / Critical / General) arms an overnight run.
- At the scheduled time, Applications install without a second confirmation when the run is armed.
- After the confirmed queue (and reboot resume), rescans Windows (update-until-current scope) and Applications, installing newly offered updates until clear.
- Failed items are recorded and skipped for the rest of that run (no retry loops).
- Soft idle gate: recent keyboard/mouse activity defers the scheduled start (~15 minutes).
- Result window (Successful vs Failed/skipped) when you return after the quiet / next-morning window.

Code only — no dump/channel in this change.
