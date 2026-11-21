# Update Express 0.7.2.48

## Dump tip (dbpu)

- Tip: `cursor/model-product-lenovo-url-d37f` (PR #96) @ `a5597f7`
- Stack: #96 → #92 (OEM support header) → #91 (Kopiert/IM autoclose) → #90 (PC info Enhet + Lenovo) → #86 (Support dialog) → 0.7.2.46 tip (#85/#83)
- **Not included:** App Control self-update (#95 / `cursor/app-control-selfupdate-8136`) — merge onto tip conflicted in `InstallationManager.cs`

## Lenovo Model/Product + support URL (#96 / UPD-157)

- Resolve marketing model from CIM `Name` **or** `Version` (ThinkCentre M630e lived in `.Version` while `.Name` was empty/MTM).
- **Produsent/modell** = Manufacturer + marketing model; **Produktnr** = MTM (never park marketing Version in Produktnr).
- Lenovo support deep-link prefers MTM-shaped Produktnr.

## From prior tip (already in 0.7.2.47 dump tree)

- Manufacturer support header button (#92)
- Kopiert! toast + IM 15s auto-close (#91)
- PC info Enhet above Nettverk + earlier Lenovo swap (#90)
- Windows Security Support dialog (#86)
- Unattended UX (#85/#83)

## Build notes

- Re-applied WPF/WinForms type qualification from 0.7.2.47 dump so publish succeeds (Authenticode dry-run).
