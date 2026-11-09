# Update Express v0.7.2.6.35

- Request UAC elevation when the installed interactive app launches, before scanning or selecting updates.
- Avoid second scan and lost selection caused by elevation at install time.
- The launcher Run action also elevates before opening MainWindow.
- Keep tray agent, uninstall, and maintenance entry points out of the interactive startup UAC flow.
- Keep existing installation-time elevation as a defensive fallback.
- No automatic install or restart. Firmware protections, icon resources and 27-unit Installation Manager buttons unchanged.

Note: We deliberately do NOT set requireAdministrator in the global application manifest because the same executable is used for a background tray agent and maintenance operations. The interactive path uses ShellExecute runas instead.

Windows build and runtime test required.
