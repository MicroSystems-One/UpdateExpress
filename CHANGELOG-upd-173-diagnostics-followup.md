# Update Express — Diagnostics follow-up (UPD-173)

Follow-up to the 0.7.2.53 Diagnostics (Slice A) after SVHA's review of the REMOTESUPPORT1 report.
Report text only. No version number is set here; the release build assigns it.

## Diagnostics report

- Header: `OS Build` (CurrentBuild.UBR), `DisplayVersion`, `EditionID`, `InstallationType`, read from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion`. A missing value is printed as `(not set)`, never substituted.
- Feature updates: the line «WUA COM does not return this Settings offer» is replaced. The report now says either that no specific Windows Settings offer is identified or verified, or that the candidate comes from the registry compatibility assessment and is not a verified Settings offer. The general WUA COM / Settings limitation is stated separately.
- Feature updates: when a candidate exists, «Shown in Windows Updates: … Not a WUA package. Install from Windows Update in Settings.» becomes «Potential feature update candidate: {Version} (build {DestBuild}). Check availability in Windows Update Settings.» (SVHA wording), so the line cannot be read as confirming a Settings offer.
- New `DIAGNOSTICS SUMMARY` as the last section: searches planned / run / Succeeded / SucceededWithErrors / failed or not run, searches with warnings and warning total, raw rows, unique updates, unique updates from search C (BrowseOnly=1), unique Preview-looking updates, total elapsed with a breakdown.
- Section timing: `SECTION ELAPSED MS` for Driver classification and Feature updates. Driver classification runs the ordinary application scan (its own WUA searches plus the installed-driver inventory); that time was previously only visible as the gap between the query times and the total.

## Unchanged

- Diagnostics still runs only from the Diagnostics button. No new call sites; ordinary scan, Silent, Tray and RMM do not run the extended searches.
- No changes to `AdvancedPending`, `CollectSoftwareUpdates`, install engine, `SafeDefault`, driver classification or K1/K2/K3.
- Slice C remains blocked.
