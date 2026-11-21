# Update Express 0.7.2.28

- Installation options again include profile (private / business / both), active hours, install days/time, and restart preference
- Optional “Install as Windows service” (LocalSystem, all-users); explicit consent; quiet uninstall stops and deletes the service
- Choices from Installation options set `SetupCompleted` so the Setup wizard is not re-asked
- Per-user tray agent remains for prompts when the service is installed
- Tip based on 0.7.2.27 (`582bbf6`)
