# Update Express v0.7.2.6.33

- Installation Manager footer rebuilt as a single fixed 27-DIP Grid row; Install selected updates and Close share the same baseline and exact height. A local WPF button template prevents OS/theme sizing differences.
- Close label now remains Close (disabled during installation), rather than changing from Cancel after completion.
- Installer embeds the original multi-resolution U icon and writes a standalone, version-named ICO file to the install directory; desktop and Start Menu shortcuts reference that file instead of the EXE icon index. The new filename bypasses previously cached icons.
- Uninstall removes the version-named icon.

Build with BYGG-EXE.cmd on Windows with .NET 10 SDK. Source changes are not compiled or runtime-tested in this Linux environment.
