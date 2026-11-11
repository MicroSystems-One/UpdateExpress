# v0.7.2.6.73 — Deduplicate identical History rows

- History tab collapses fully identical update rows into one entry.
- Identity key: Update title + KB + Version + Status (empty KB/Version count as equal); distinct versions stay separate (e.g. Defender KB2267602).
- When collapsing, the newest Date/Time is kept.
- Success and failure for the same title remain both visible when Status differs.
