# Update Express v0.7.2.6.78

## Installation Manager — verify stuck / Not verified

- **Root cause:** `InstallSelectedWua` returned install results without `UpdateId`. The combined install path matched only by UpdateId/KB, so KB-less packages (e.g. PowerShell LTS) never entered `Verifying`. The rescan then refused to mark `Completed` unless prior state was `Verifying`, leaving **Finished: 0 completed … 1 unverified** with the elapsed clock stopped at `Finish`.
- WUA install results now include `UpdateId`; Defender signature results copy the requested `UpdateId`.
- Shared `WindowsUpdateReader.MatchInstallResult` (UpdateId → KB → title → index) used by Installation Manager and related install UIs.
- Post-install reconcile: installer success (`Verifying`) + absent from pending, or successful WUA history, → localized **Completed** / **Fullført**. Still-pending / unmatched → localized **Not verified** with retry guidance.
- Verify/rescan awaits pulse the elapsed timer and surface a clear **Verify timed out** / **Verifisering tidsavbrutt** terminal state with rescan/retry guidance instead of hanging mid-verify.
- Install STA work is never abandoned on soft timeout (stage text only); verify soft-timeouts keep `_busy` until background WUA/WinGet tasks drain so overlapping installs cannot start.
- `MatchInstallResult` positional fallback only when both sides lack `UpdateId` — never across conflicting identities.
- Finish summary localized (Ferdig/Finished) and does not leave rows stuck mid-verify without a terminal label.

Build on Windows with .NET 10 SDK using `BYGG-EXE.cmd`. This source was not compiled on Windows in the delivery environment.
