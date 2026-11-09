# Update Express v0.7.2.6.5

- Fixed COM IWindowsDriverUpdate metadata access via dynamic dispatch (previous reflection on COM objects returned empty values).
- Added conservative driver classification and explanation with hardware ID, model, manufacturer and class. Newest/Older compare WUA-offered candidates only, never claim installed driver is older.
- Added read-only driver details and diagnostic classification report.
- Diagnostics button displays Please wait… and disables during collection; restores after success/failure.
- Added explicit feature-update visibility limitation and Windows Update Settings URI to diagnostics.
- No feature update detection from Windows Settings yet: Windows Settings offers cannot be reliably inferred from WUA raw search.
- No changes to installation safety rules.
