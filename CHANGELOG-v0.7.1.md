# Update Express v0.7.1 – Activity Log (test candidate)

- Per-user append-only activity.jsonl with timestamp, computer, Windows identity, execution mode, source, action, result and run ID.
- Activity Log opens in Notepad; Export Log copies the raw JSONL records to a user-selected file (the .txt extension still contains JSON Lines).
- Manual Windows Update and WinGet installation attempts/results are logged. Existing Windows Update history and previous JSONL files remain intact.
- Defender signature updater captures before/after versions and differentiates Installed, Already up to date, and Verification unavailable.
- Empty placeholder row removed from Windows Updates DataGrid.
- No automatic update installation or restart was added.

## Important limitations
- Windows Update history is maintained by Windows and is not retroactively attributed to Update Express.
- Logs are per Windows user under %LOCALAPPDATA%\MicroSystems\Update Express\Logs\activity.jsonl, including for all-users installations. This is not yet a machine-wide consolidated audit log.
- Portable mode uses the same per-user log path in this preview: strict no-trace portable logging remains planned and should be tested before deployment.
- Installation/repair/uninstall maintenance helper events are not yet integrated in the new activity log; they retain the existing AgentLog.
- A WinGet exit code of 0 means command completed, not that installed version was independently verified.
- No Windows WPF build was performed in this environment. Test on a VM before production.
