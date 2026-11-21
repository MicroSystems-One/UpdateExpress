# Update Express 0.7.2.47

## Dump tip (dbpu)

- Tip: `cursor/manufacturer-support-header-92ba` (PR #92) @ `781cebd`
- Stack: #92 → #91 (Kopiert/IM autoclose) → #90 (PC info Enhet + Lenovo field swap) → #86 (Support dialog link) → 0.7.2.46 tip (#85/#83)

## Manufacturer support button (#92)

- Header button «{Brand} support» left of Denne PCen for known OEMs (Lenovo, Dell, HP, ASUS, Samsung, Acer).
- Lenovo deep-link with Produktnr/MTM; Dell service-tag; others product/serial/support home.
- Hidden for unknown/generic makers.

## UX polish (#91)

- «Kopiert!» toast on copy icons (Denne PCen / MainWindow / OsSupport).
- Installation Manager Finished footer countdown: Closing in N s… (15 s).

## PC info (#90)

- Enhet section above Nettverk.
- Lenovo Model/Product mapping fixed; manufacturer title-case.

## Windows Security Support dialog (#86)

- Replaced plain MessageBox with WPF dialog; clickable Learn.microsoft.com link + Kopier.

## Build notes

- Re-applied WPF/WinForms type qualification from 0.7.2.46 dump so publish succeeds (Authenticode dry-run).
