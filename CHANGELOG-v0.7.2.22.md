# Update Express 0.7.2.22

- Icon cleanup: remove orange FIX IT! asset; header tools button is ClickIt only; FIX IT page + help accordion use blue U
- Help accordion label: **Fix your Windows update issues here....**
- Self-update / dialog title bars force blue U via embedded HICON (`AppIcon`)
- Install diagnostics always-on: startup writes `Logs\install-update.log` (+ README); Settings open/export unchanged
- Tray after self-update: start agent/UI **before** Defender exclusions; `EnsureBackgroundAgentRunning` on UI/agent startup heals missing tray

Build: `BYGG-EXE.cmd` → `Publisert\UpdateExpress_v0.7.2.22.exe`
