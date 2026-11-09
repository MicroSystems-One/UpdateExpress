# v0.7.2 — Download progress (initial safe implementation)

- During WUA download, show `Downloading 0/N updates — waiting for Windows Update Agent…`.
- After WUA completes the download request, count actual `IsDownloaded` states and report `Downloaded X/N`, with per-update KB and result/HRESULT.
- Keep progress bar indeterminate during the synchronous WUA operation. No invented percentage or byte count.
- **Limitation:** WUA's synchronous `Download()` does not provide live byte-level callbacks. True live percentages require a separately tested asynchronous WUA `BeginDownload` implementation; not included in this build.
- Windows Update installation behavior unchanged.
- Windows build not verified in this Linux environment.
