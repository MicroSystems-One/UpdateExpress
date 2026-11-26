# Update Express 0.7.2.54

## Dump tip (dbpu)

- Base: 0.7.2.53 dump (`e099c5a` / #116) + Diagnostics follow-up from PR #118 head `be5dfe1` (commits `560aa85` + `be5dfe1`)
- Folded onto richest feature dump tip (not logo-only `main`); MicroSystems banner logo retained
- **Slice C (inventory) excluded** — blocked; no AdvancedPending / SafeDefault / K1–K3 / Silent / Tray / RMM inventory changes
- Diff is Diagnostics report text only (`WindowsUpdateReader.cs`, `FeatureUpdateOfferReader.cs`, changelog)

## Diagnostics follow-up (UPD-173 / UPD-181)

- Header: `OS Build` (CurrentBuild.UBR), `DisplayVersion`, `EditionID`, `InstallationType` from `HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion` (missing → `(not set)`)
- Neutral Settings-offer wording in `FeatureUpdateOfferReader.DiagnosticLines()` — no unconditional «WUA COM does not return this Settings offer»; candidate line uses SVHA wording «Potential feature update candidate…»
- New `DIAGNOSTICS SUMMARY` section: search outcomes, warnings, raw/unique counts, elapsed breakdown
- `SECTION ELAPSED MS` for Driver classification and Feature updates
- Extended searches remain Diagnostics-only; install engine / SafeDefault / K1–K3 untouched

## Also in this dump (retained)

- **#116 / 0.7.2.53** — UPD-173 Slice A+B optional/Preview honesty
- **#112 / #114** — UPD-168 Codex (migrate delete; resume scanning; cycle mutex) + channel SHA
- **#109** — Tray Påminnelse when Main closed
- **#106** — OEM Open download prefers package EXE; TXT readme on separate button
- **#102** — OEM Lenovo + Dell + HP catalogs
- **#99** — Norwegian Windows monitor interval label
- **#95** — App Control self-update via install-dir maintenance helper

## Build notes

- Built from full Windows feature tip (0.7.2.53 dump tree + #118 Diagnostics follow-up), not logo-only `main`.
- Authenticode dry-run (no `UE_SIGN_MODE`).
- Critical: `latest.json` sha256 must match the published EXE asset byte-for-byte.
- Prior local/test binary `0.7.2.54-test` is superseded by this production build.
- Keep **0.7.2.53** release assets available for rollback.
