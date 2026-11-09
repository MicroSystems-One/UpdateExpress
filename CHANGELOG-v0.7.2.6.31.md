# Update Express v0.7.2.6.31 (source test build)

- Allows selecting multiple verified non-firmware drivers in one installation queue. Each is installed sequentially with a fresh WUA UpdateID and hardware-ID check.
- Driver errors are logged per driver, and subsequent drivers are still attempted.
- Results verification now iterates over every selected driver, not just the first.
- Installation Manager action buttons explicitly fixed at 27 WPF device-independent pixels, vertically centered.
- Firmware remains blocked. No automatic reboot. Optional drivers are not automatically selected.
- Known issue: WUA driver failures require reviewing Activity Log and driver-recheck.log; this version does not bypass WUA or driver restrictions.
- Build on Windows with .NET 10 SDK via BYGG-EXE.cmd; Windows compilation has not been verified here.
