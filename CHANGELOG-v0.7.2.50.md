# Update Express 0.7.2.50

## Dump tip (dbpu)

- Tip: `cursor/oem-exe-txt-buttons-9a90` (PR #106) @ `35955fa`
- Base: 0.7.2.49 dump (`b7cac4a`) + cherry-pick of #106
- Stack: #106 (EXE vs TXT buttons) → #102 (OEM Lenovo+Dell+HP) → #101 → #96 → #92 → #91 → #90 → #86 → 0.7.2.46 tip
- Also retained from 0.7.2.49: Windows interval label (#99), App Control self-update (#95)

## OEM download EXE vs TXT (#106)

- Lenovo catalog: `DownloadUrl` = package (EXE/ZIP/ISO/…); `ReadmeUrl` = TXT README; HTML stays on `DetailsUrl`.
- **Open OEM download** / double-click → package URL (never prefer readme).
- New **TXT file download** button when a readme URL exists.
- `OemDriverSearch` prefers `DownloadUrl` over `DetailsUrl` for `ExternalUrl` (Dell/HP too).

## Also in this dump (retained)

- **#102** — OEM Lenovo + Dell + HP catalogs behind OEM-drivere
- **#99** — Norwegian Windows monitor interval label
- **#95** — App Control self-update via install-dir maintenance helper

## Build notes

- Built from full Windows feature tip (#106 on 0.7.2.49 dump tree), not logo-only `main`.
- Authenticode dry-run (no `UE_SIGN_MODE`).
- Critical: `latest.json` sha256 must match the published EXE asset byte-for-byte.
