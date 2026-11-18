# Update Express

Windows update management application for MicroSystems technicians and MSPs.

**Current version:** 0.7.2.36  
**Platform:** Windows 10/11 x64  
**Stack:** .NET 10 / WPF / C#  
**GitHub home:** https://github.com/MicroSystems-One/UpdateExpress

## Build

Requirements: .NET 10 SDK on Windows.

```bat
BYGG-EXE.cmd
```

Output: `Publisert\UpdateExpress_v0.7.2.36.exe`

After publish, `BYGG-EXE.cmd` calls `tools\Sign-Release.ps1`. Until a code-signing identity is configured, that step **dry-runs** (exit 0) and leaves the EXE unsigned. To enable signing later, set `UE_SIGN_MODE=azure` or `signtool` plus the documented env vars (no secrets in git). Sample Azure metadata: `tools\azure-signing.metadata.sample.json`.

## Update channel

Clients read:

`https://raw.githubusercontent.com/MicroSystems-One/UpdateExpress/main/latest.json`

Release EXE assets are published as GitHub Releases on this same repository.

## Notes

- Do not commit published EXE binaries or `bin/` / `obj/` folders.
- See versioned `CHANGELOG-v*.md` files for release history.
