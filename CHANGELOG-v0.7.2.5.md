# Update Express v0.7.2.5 — source preview

- Driver scan now independently verifies assigned driver identities using WUA `IsAssigned=1`.
- Only assigned, non-firmware drivers are preselected. Optional drivers and firmware are never preselected.
- Driver entries expose `DriverClassification` (Recommended / Optional / Firmware) for future UI improvements.
- The Select All checkbox in the driver tab now applies only to WUA-assigned drivers.
- Updated version metadata and diagnostics header.

## Important limitations
- This is a **source preview**, not a compiled or Windows-tested build.
- Driver installation remains unavailable in the original application and is intentionally not enabled by this patch.
- PME dialog, full UI translation and WUA progress enhancements remain pending.
- Do not deploy to customers before Windows build and regression testing.
