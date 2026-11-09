# Update Express v0.7.2.6 — Driver list presentation

- Display driver versions in the Version column, using WUA driver metadata where available, with a trailing title-version fallback.
- Remove the trailing version and standard WUA date/time suffix from driver display names (original Title preserved in tooltip and logs).
- Driver Status: Recommended (assigned), Firmware, Available; Newest/Older only when WUA reports the same nonempty hardware ID and model for at least two comparable versions.
- Green text for Recommended/Newest; orange for Firmware.
- No changes to driver installation policy: drivers and firmware remain non-installable through this preview.
- No changes to N-able PME service control, WinGet, or background monitoring.

NOTE: Windows WPF build and runtime tests are required.
