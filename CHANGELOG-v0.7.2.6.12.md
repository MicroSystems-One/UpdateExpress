# Update Express v0.7.2.6.12

- Check for Updates and Install Selected Updates both have fixed width 180 WPF units and height 30.
- Connectivity indicator now reads Microsoft TCP: OK when all TCP probes succeed; partial/failure diagnostics remain unchanged.
- Version label and build script updated to v0.7.2.6.12.
- Driver installation implementation is unchanged from v0.7.2.6.11.
- Proxy Settings are not implemented in this build; existing .NET proxy display is diagnostic only.

Build on Windows with .NET 10 SDK using BYGG-EXE.cmd. Linux environment cannot validate Windows WPF compilation or installation.
