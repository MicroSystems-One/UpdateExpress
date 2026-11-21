# Update Express 0.7.2.49

## Dump tip (dbpu)

- Tip: `cursor/oem-drivers-search-b62c` (PR #102) @ `722ed51`
- Stack: #102 (OEM Lenovo+Dell+HP) → #101 (Lenovo URL) → #96 → #92 → #91 → #90 → #86 → 0.7.2.46 tip
- Also included: Windows interval label (#99), App Control self-update (#95; InstallationManager merge resolved)

## OEM drivers search (#102 / UPD-162/163)

- Checkbox **OEM-drivere** / **OEM drivers** beside All WUA drivers (default on).
- Lenovo / Dell / HP catalog providers via `IOemDriverCatalog` + `OemDriverSearch`.
- OEM rows: Source `OEM / {Brand}`, KB `OEM`; Open OEM download / double-click (not WUA-installable).
- WUA driver titles append ` · {version}`.

## Also in this dump

- **#99** — Norwegian Windows monitor interval label: **Sjekk Windows-oppdateringer hver (timer)**
- **#95** — Prefer install-dir maintenance helper so App Control can allow self-update; clear block errors; admin doc

## Build notes

- Re-applied WPF/WinForms type qualification from prior dumps so publish succeeds (Authenticode dry-run).
