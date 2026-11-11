# v0.7.2.6.74 — Bitdefender ATC / Access Denied on maintenance

- Clearer Norwegian maintenance error when install/repair/upgrade hits Access Denied (antivirus quarantine / lock, e.g. Bitdefender Advanced Threat Control).
- Retry `File.Copy` / delete / icon write also on `UnauthorizedAccessException` (not only `IOException`), with a slightly longer backoff.
- Guidance in the dialog: restore from quarantine and use folder/process exclusions for `%LOCALAPPDATA%\MicroSystems\Update Express\` (filename-only exclusions are not enough for ATC).
