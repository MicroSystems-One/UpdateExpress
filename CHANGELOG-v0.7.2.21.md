# Update Express 0.7.2.21

- Header uses blue U (`UpdateExpress-U.png`); orange MicroSystems-M asset removed; shortcuts refresh IconLocation on upgrade (delete+recreate + SHChangeNotify)
- Win11-style **Help with Windows Update** accordion with CLICK IT icon; FIX IT window header uses ClickIt
- Install/update diagnostic logging: always-on `%LOCALAPPDATA%\MicroSystems\Update Express\Logs\` (`install-update.log` / `last-install.log`) covering copy/replace, shortcuts, Defender exclusions (incl. UAC cancel), process start/stop, post-install +2.5s presence probe
- Settings → Install / update diagnostics: verbose toggle, Open log folder, Open last log, Export log…

Build: `BYGG-EXE.cmd` → `Publisert\UpdateExpress_v0.7.2.21.exe`
