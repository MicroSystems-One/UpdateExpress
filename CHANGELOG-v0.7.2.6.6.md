# Update Express v0.7.2.6.6 (test source)

- Read-only installed PnP driver inventory through Win32_PnPSignedDriver.
- Compare offered and installed versions only for a unique PnP device ID match to WUA hardware ID.
- Status: Newer, Older, Same version, Recommended (WUA assigned), Firmware, Available (unverified).
- Never auto-select based on version comparison; firmware never auto-selected.
- Driver details and diagnostics show installed version and classification rationale.
- Feature-update diagnostics explicitly distinguish WUA-visible offers from Windows Settings-only offers. No independent Windows Settings offer detection implemented; 26H2 may still not appear.
- Windows WPF compilation and runtime tests required.
