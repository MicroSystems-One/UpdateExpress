# Update Express v0.7.2.6.10

- Read-only WUA driver diagnostics now report CanRequestUserInput, RebootBehavior, Impact, IsDownloaded and EulaAccepted.
- Installation-time driver recheck records WUA installation behavior in driver-recheck.log.
- Driver requiring interaction is explicitly blocked in quiet-only pilot; unknown interaction status is also blocked.
- No firmware install, unattended interaction bypass or automatic reboot enabled.
- Versioned publish script and project metadata updated.

Windows WPF build and installation require testing on Windows with .NET 10 SDK.
