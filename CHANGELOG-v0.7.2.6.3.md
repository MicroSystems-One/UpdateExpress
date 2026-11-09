# Update Express v0.7.2.6.3

- Fix invalid five-part NuGet/.NET version metadata; use valid 0.7.2.6 assembly/file/package versions.
- Preserve user-facing version 0.7.2.6.3 via AssemblyInformationalVersion, shared by installer and main window.
- Fix four C# type-resolution errors in InstallationOptionsWindow.cs.
- Update BYGG-EXE.cmd to publish the versioned executable, with explicit failure handling.
- Keep Include optional and All WUA drivers checked by default as already configured in XAML; this does not authorize auto-installation.
- Correct diagnostic header and update source version labels.

Build on Windows with .NET 10 SDK: run BYGG-EXE.cmd. Output: Publisert\UpdateExpress_v0.7.2.6.3.exe
This source package has not been compiled or run in a Windows/WPF environment here.
