# Update Express v0.7.2.6.13

- History: hide Category, Error Code and Priority columns; display failure HRESULT within Status; preserve original error code in underlying entries and exports.
- History: success/failure color cues and wider Status column.
- Proxy details: read-only window showing WinHTTP (`netsh winhttp show proxy`) and current-user PAC (`HKCU\...\Internet Settings\AutoConfigURL`), plus manual user proxy. Copy report button.
- No proxy changes, no PAC fetch, no driver installation behavior changes.
- Windows WPF app: compile/publish on Windows with .NET 10 SDK using BYGG-EXE.cmd.
