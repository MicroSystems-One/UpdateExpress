# Update Express – Windows automation B+C (source on 0.7.2.6.37 baseline)

## B – Policy + UI
- Three-category Windows automation policy (Defender / Critical / General).
- Maintenance panel UI for intervals and preferred times.
- Status lines on main monitor panel and per category.
- Critical/General modes stored as download+ask (not executed yet).
- Never auto-reboot remains forced.

## C – Defender (K1)
- Tray agent polls Defender automation every 5 minutes.
- When due (default every 60 minutes), runs `Update-MpSignature` via existing Defender path.
- Results written to `windows-automation-status.json` and activity/agent logs.
- No Windows restart.

Drivers/WinGet checkboxes remain preferences only.
