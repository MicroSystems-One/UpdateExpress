Update Express v0.7.2.4 - test source

New:
- Drivers/Firmware default scan uses WUA IsAssigned=1 to reduce unassigned driver offers.
- Check "All WUA drivers" and scan again to show full WUA driver list.
- Diagnostics still records raw WUA driver list AND assigned driver list.
- PME Control button: detect PME.Agent.PmeService, stop+disable or enable+start via Windows elevation (sc.exe).
- Does NOT modify Windows Update policy registry keys automatically.
- Does NOT disable N-able main monitoring agent.

Safety:
- PME may be restarted or reconfigured by N-able RMM. Manage policy centrally for lasting changes.
- Disabling PME can allow Windows Update to download/install automatically; do not use casually on production machines.
- Firmware updates remain manual-only. WUA IsAssigned is NOT a guarantee of identical Windows Settings offers.
- This source has not been compiled/tested on Windows in this environment. Build with BYGG-EXE.cmd and report errors.
