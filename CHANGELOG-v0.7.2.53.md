# Update Express 0.7.2.53

## Dump tip (dbpu)

- Base: 0.7.2.52 dump (`e7e5331` / #112) + UPD-173 Slice A+B from PR #115 head `1dd0f08`
- Folded onto richest feature dump tip (not logo-only `main`); MicroSystems banner logo retained
- **Slice C (inventory) excluded** — blocked; no AdvancedPending / SafeDefault / K1–K3 / Silent / Tray / RMM inventory changes

## UPD-173 Slice A — Diagnostics

- Diagnostics header: Discovery Method, Server Selection label (`0` = Default – configured Windows Update source), Service ID (`all-zero` = default / not explicitly set), application policy lines (not machine-verified Yes/No).
- Per WUA search: `ISearchResult.ResultCode` (Succeeded / SucceededWithErrors / Failed / Aborted / …), Warnings, HRESULT on exception, raw count before app filtering, elapsed ms; total diagnostics elapsed ms.
- Added untyped search A; BrowseOnly set comparison and Preview/title scan reuse cached rows (no repeated Search).
- Explicit note that failed/partial searches with count 0 do not mean the PC is fully up to date.
- Documents CollectSoftwareUpdates / SafeDefault limit for manual Preview install (handle in Windows Update; Install Selected not extended).
- Extended searches remain Diagnostics-only (not scan / Silent / Tray / RMM).
- Cache **all** WUA search rows for set compare / Preview scan; 150-entry limit applies to report display only (Codex P2).

## UPD-173 Slice B — UI honesty

- When Include optional is on: discreet ⓘ + tooltip (NO/EN approved wording) and «Open Windows Update» / «Åpne Windows Update» via existing `FeatureUpdateOfferReader.OpenWindowsUpdate()`.
- Existing feature-update Open Windows Update control unchanged.
- No concrete Preview KB unless COM confirms it.

## Also in this dump (retained)

- **#112 / #114** — UPD-168 Codex (migrate delete; resume scanning; cycle mutex) + channel SHA
- **#109** — Tray Påminnelse when Main closed
- **#106** — OEM Open download prefers package EXE; TXT readme on separate button
- **#102** — OEM Lenovo + Dell + HP catalogs
- **#99** — Norwegian Windows monitor interval label
- **#95** — App Control self-update via install-dir maintenance helper

## Build notes

- Built from full Windows feature tip (0.7.2.52 dump tree + #115 Slice A/B), not logo-only `main`.
- Authenticode dry-run (no `UE_SIGN_MODE`).
- Critical: `latest.json` sha256 must match the published EXE asset byte-for-byte.
- Prior local/test binary `0.7.2.53-test` (SHA `98cfb90d…`) is superseded by this production build.
