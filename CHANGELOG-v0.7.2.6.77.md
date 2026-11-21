# v0.7.2.6.77 — Self-update dialog follows app language string table

- Self-update prompt title, body, legend, and button captions resolve through `AppLanguage` (ReminderSchedule.Language + en→no string table), matching the rest of the UI localization pattern.
- Button captions are no longer hardcoded Yes/No/Cancel or Ja/Nei/Avbryt in C#; they come from the same table as the body legend.
- Still uses the custom WinForms dialog (not OS MessageBox), so captions follow the app language instead of the Windows UI language.
