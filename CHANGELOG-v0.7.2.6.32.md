# Update Express v0.7.2.6.32 – driver diagnostics test source

- Driver WUA operation audit: SafetyCheck, Eula, Download, Install, Failed, including native item HRESULT, ResultCode, download state and reboot flag.
- Exceptions log inner-exception chain and identify the failing stage. Generic .NET HRESULT is not presented as a confirmed WUA driver error.
- Installation Manager action buttons aligned at 27 px high with centered content.
- No bypass of driver identity, hardware ID, firmware block, or interactive-consent checks. No automatic restart.
- Windows WPF build must be verified on the target Windows 11 system using BYGG-EXE.cmd; this package is source, not a prebuilt installer.

Diagnostic file: C:\ProgramData\UpdateExpress\driver-recheck.log
Additional run log: %LOCALAPPDATA%\UpdateExpress\installation-manager.jsonl
