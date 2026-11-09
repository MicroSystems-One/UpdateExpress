# Update Express v0.7.2.6.35

- Administrator-token check before starting any selected update installation.
- Explicit UAC elevation via `runas`, with cancellation handling.
- No unattended installation: after elevation, rescan and reselect updates manually.
- Existing 27-unit Installation Manager button layout, U icon, firmware block and driver defaults retained.

Note: The selected queue is not preserved across elevation in this safety-first release.
