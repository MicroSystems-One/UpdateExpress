# Update Express 0.7.2.42

## Fix: WinGet installs missing from Historikk

Successful (and failed) Installation Manager WinGet installs now append matching rows to the same `wua-history-archive.json` Historikk reads, so Completed apps such as LibreOffice appear under Search results → History without a separate channel.

Also records driver/WUA Installation Manager Completed/Failed outcomes the same way. Archive-only IM rows keep their WinGet/Driver/Windows Update source (not labelled as “saved before Windows Update fix”).
