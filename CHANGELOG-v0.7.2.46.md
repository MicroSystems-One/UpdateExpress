# Update Express 0.7.2.46

## Unattended follow-ups (#85) on until-clear (#83 / 0.7.2.45)

- **Result window:** Shows when measured keyboard/mouse activity returns (not login-only); soft auto-close after 5 minutes.
- **Reboot countdown UI:** Centered modal with live countdown and aligned buttons **Avbryt** | **Omstart nå** | **Utsett 30 minutter**.
- **Machine-scope installs:** Automated WinGet upgrades use `--scope machine` (all users). Schedule notices published to ProgramData so any signed-in user’s tray can toast.
- **Quiet session:** Empty-desktop heuristics (visible interactive apps + idle) defer the 17:00 unattended start.

## Also in this dump tip (0.7.2.45)

- Confirm **Install at HH:mm** arms overnight update-until-clear (`unattended-until-clear.json`).
- At schedule: install confirmed queues; Applications skip re-prompt while armed.
- After reboot resume: rescan Windows + WinGet apps until clear; failures skipped for that run.
- Soft idle gate (~15 min) before scheduled start; result window on return.

## Also from prior 0.7.2.44 tip

- Icon polish 18px refresh/PC (#80/#79); app-queue resume (#76); WinGet Historikk (#74); 0.7.2.41 UI stack.

Built from stacked tip `cursor/unattended-followups-a931` (`c8b8c5d` / PR #85) on #83 → 0.7.2.44 dump.
