# Update Express 0.7.2.46

## Unattended follow-ups (on 0.7.2.45)

- **Result window:** Shows when measured keyboard/mouse activity returns (not login-only); soft auto-close after 5 minutes.
- **Reboot countdown UI:** Centered modal with live countdown and aligned buttons **Avbryt** | **Omstart nå** | **Utsett 30 minutter**.
- **Machine-scope installs:** Automated WinGet upgrades use `--scope machine` (all users). Schedule notices published to ProgramData so any signed-in user’s tray can toast.
- **Quiet session:** Empty-desktop heuristics (visible interactive apps + idle) defer the 17:00 unattended start.

Code only — no dump/channel in this change.
