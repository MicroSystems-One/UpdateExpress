# Update Express — optional / Preview WUA honesty (UPD-173)

## Slice A — Diagnostics

- Diagnostics header: Discovery Method, Server Selection label (`0` = Default – configured Windows Update source), Service ID (`all-zero` = default / not explicitly set), application policy lines (not machine-verified Yes/No).
- Per WUA search: `ISearchResult.ResultCode` (Succeeded / SucceededWithErrors / Failed / Aborted / …), Warnings, HRESULT on exception, raw count before app filtering, elapsed ms; total diagnostics elapsed ms.
- Added untyped search A; BrowseOnly set comparison and Preview/title scan reuse cached rows (no repeated Search).
- Explicit note that failed/partial searches with count 0 do not mean the PC is fully up to date.
- Documents CollectSoftwareUpdates / SafeDefault limit for manual Preview install (handle in Windows Update; Install Selected not extended).
- Extended searches remain Diagnostics-only (not scan / Silent / Tray / RMM).

## Slice B — UI honesty

- When Include optional is on: discreet ⓘ + tooltip (NO/EN approved wording) and «Open Windows Update» / «Åpne Windows Update» via existing `FeatureUpdateOfferReader.OpenWindowsUpdate()`.
- Existing feature-update Open Windows Update control unchanged.
- No concrete Preview KB unless COM confirms it.

## Slice C — Inventory

- **Blocked.** No changes to AdvancedPending, CollectSoftwareUpdates, drivers, UpdateCategoryRules, SafeDefault, K1/K2/K3, Silent, Tray, or RMM.

## Publish

Await SVHA approval before shipping a production build.
