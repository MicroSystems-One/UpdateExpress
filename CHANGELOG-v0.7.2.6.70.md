# v0.7.2.6.70 — Update-until-current review fixes

- Cancel a pending 90-second restart when the user stops the update cycle (`shutdown /a`).
- Hold the main-window busy guard while the cycle runs so Install / Windows Update fix cannot start in parallel.
- Reject reentrant cycle runners on the same UI thread (non-reentrant enter flag + mutex).
- Clear the tray resume-launch marker when the elevated runner starts so successive quick restarts can resume again.
