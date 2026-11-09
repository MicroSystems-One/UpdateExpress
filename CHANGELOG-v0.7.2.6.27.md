Update Express v0.7.2.6.27

- Standardized visible column order across Windows Updates, Drivers/Firmware, Applications, and History: Select, KB, Version, Update, Status, Date / Time, Error Code, Priority.
- Removed redundant driver version column; unified version binding.
- Applications version displays installed -> available; package ID remains available as a tooltip on Update, while underlying data and upgrade behavior remain intact.
- Empty columns intentionally remain visible for a consistent layout.
- Removed tab-specific visibility toggles for KB, version, error code and priority.
- No changes to update installation, firmware safety or proxy logic.
