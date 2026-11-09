# Update Express v0.7.2.6.4

- Version is shown on the first launcher screen, using the existing InformationalVersion source.
- Removed duplicate version from installation options.
- Increased installation-options window height and bottom padding so Cancel/Install borders are not clipped at common DPI settings.
- Translated launcher, background monitoring, connectivity labels and tooltips to English.
- Kept WUA driver safety behavior unchanged: only IsAssigned=1 preselected, firmware never preselected.
- Windows Settings feature-update offers (such as 26H2) are not detectable from the supplied WUA diagnostics; no unverified availability claims or installation changes have been added.

Build with BYGG-EXE.cmd on Windows with .NET 10 SDK. This source package has not been compiled or UI-tested on Windows in this environment.
