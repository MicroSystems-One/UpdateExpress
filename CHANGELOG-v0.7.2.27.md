# Update Express 0.7.2.27

- Install flow: setup before main UI; silent RMM defers shortcuts/tray until policy seed
- Stop locked-EXE race (stop all processes + staged `.new` replace); silent failures never MessageBox/rethrow
- Defender exclusions: bounded PowerShell I/O (no ReadToEnd hang); skip UAC in silent mode
- Full uninstall + `QuietUninstallString` for Settings Apps; N-able script self-elevates + install timeout
- Chevron column: monitor toggle outside gray fill; gear 28px; help expander border compensation
