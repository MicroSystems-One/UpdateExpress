# Update Express v0.7.2.6.9 – driver recheck diagnostics

- Logs fresh WUA driver search criteria, all returned UpdateIDs, revisions and hardware IDs to C:\ProgramData\UpdateExpress\driver-recheck.log.
- Requires exact UpdateID and unique hardware-ID match; blocks firmware and interactive drivers.
- Distinguishes missing UpdateID from hardware mismatch. No automatic restart.
- Does not bypass Windows Update or force an unverified driver installation.
- Windows build and live WUA behavior require validation on the test PC.
