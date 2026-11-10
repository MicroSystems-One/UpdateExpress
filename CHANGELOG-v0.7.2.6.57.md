# Update Express v0.7.2.6.57

- Tray menu Settings now opens language, 12/24-hour clock, and date format. Reminders, Windows automation, and proxy stay where they are.
- Firmware updates stay visible when All WUA drivers is off, with priority Moderate (K3) unless Microsoft published a higher MSRC severity. Firmware is still never selected automatically.
- The Windows feature-update row, such as Windows 11 26H2, has Open Windows Update to install on the row itself.
- Show this PC under Windows Update Control: manufacturer, model, product number, serial number, and device name. Each value can be copied.
- WUA deep check reports end with a prompt the user can paste into an AI chat. The assistant must ask before offering a repair script, and treat reinstall as a last resort.
- Quick and Medium check free space on the Windows drive, then run sfc /scannow before resetting the Windows Update cache. Deep still adds DISM, and runs sfc /scannow again only when the first scan could not repair every file.
