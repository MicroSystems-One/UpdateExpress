# Update Express v0.7.2.6.30

- Installation Manager: uniform 27px footer buttons, rounded list border.
- Finish resolves pending/verifying rows to Not verified instead of leaving them stuck.
- Item-specific WUA result mapping; failed driver shows HRESULT when available.
- Rescan reconciles WUA and driver items, without treating missing results as success on their own.
- WinGet failures preserve exit code in queue.
- Elapsed timer updates during work and freezes on finish.

Build on Windows with .NET 10 SDK using BYGG-EXE.cmd. This source was not compiled on Windows in the delivery environment.
