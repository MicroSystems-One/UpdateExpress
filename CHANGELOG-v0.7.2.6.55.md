# Update Express v0.7.2.6.55

- Remove an update from Windows Updates when History already shows it as successful, without running Check for updates again.
- Match Defender security intelligence (KB2267602) by signature version, so a newer signature that is not installed still stays in the list.
- Other updates are removed only when the successful History event is newer than the last search.
