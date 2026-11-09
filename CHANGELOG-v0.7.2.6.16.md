# v0.7.2.6.16 – Proxy test preview

- Adds explicit 15-second HTTP connectivity test through selected manual host:port or Windows current-user proxy resolver.
- Keeps Windows WinHTTP, Windows Update Agent and WinGet settings unchanged.
- Manual PAC URL is saved but NOT executed; test reports unsupported PAC clearly.
- Aligns Proxy Settings action buttons at 30px height.
- Does not implement proxy routing for WUA/WinGet; do not represent this as complete proxy support.
