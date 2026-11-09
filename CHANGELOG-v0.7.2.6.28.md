Update Express v0.7.2.6.28

- Removed Error Code column from both WUA/driver/history and WinGet tables.
- Status shows HRESULT alone for failed WUA entries when available, otherwise original failure label.
- Full status and HRESULT remain available in Status tooltip and existing logs/CSV/PDF exports.
- Partial results use amber, successes green, failures red.
- No changes to install, proxy or background monitoring logic.
- Source-only release; Windows compilation and UI validation required.
