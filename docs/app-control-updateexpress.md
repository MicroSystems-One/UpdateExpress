# Microsoft App Control / WDAC and Update Express self-update

**For:** Admins / MSPs with Windows Defender Application Control (WDAC) / Smart App Control / App Control for Business.  
**Product:** Update Express (`MicroSystems-One/UpdateExpress`)  
**Symptom:** Self-update fails; Windows shows *«En programkontrollpolicy har blokkert denne filen.»* (EN: *A Windows App Control policy has blocked this file.*). Installed version unchanged.

---

## What Update Express does on self-update

1. Downloads the new EXE to `%TEMP%\MicroSystems-UpdateExpress-Update\…` (verify only — **not executed**).
2. Stages a detached helper named `UpdateExpress-Maintenance.exe` and starts it with `--apply-maintenance upgrade …`.
3. The helper waits for the UI to exit, then overwrites the installed `UpdateExpress.exe`.

**Helper staging order (current builds):**

| Priority | Location |
|---|---|
| 1 | All-users: `%ProgramFiles%\MicroSystems\Update Express\UpdateExpress-Maintenance.exe` (when writable) |
| 2 | Per-user: `%LOCALAPPDATA%\MicroSystems\Update Express\UpdateExpress-Maintenance.exe` |
| 3 | Fallback only: `%TEMP%\MicroSystems-UpdateExpress-Maintenance\…` (often blocked by App Control) |

Older builds staged the helper **only** under `%TEMP%`, which default enterprise App Control policies commonly deny for unsigned binaries.

---

## Recommended allowlist (no code-signing cert yet)

Until releases are Authenticode-signed (`UE_SIGN_MODE` / Azure Artifact Signing — see project signing notes), use **path rules** (or hash rules per build).

### Path rules (preferred without a publisher cert)

Allow **execute** for these folders (and optionally the exact EXE names):

| Scope | Path |
|---|---|
| Per-user install | `%LOCALAPPDATA%\MicroSystems\Update Express\` |
| All-users install | `%ProgramFiles%\MicroSystems\Update Express\` |

Files that must be allowed to run:

- `UpdateExpress.exe`
- `UpdateExpress-Maintenance.exe` (same folder)
- Optionally `UpdateExpress-Maintenance-*.exe` if a previous helper was still locked and a unique sibling name was used

Optional (download staging — **read/write**, execution not required):

- `%TEMP%\MicroSystems-UpdateExpress-Update\`

Do **not** rely on a blanket allow for all of `%TEMP%`.

### Publisher rule (when signing is available)

Once public releases are signed with a stable org identity:

1. Trust the **publisher** (Authenticode subject / TSA) in the App Control policy.
2. Prefer publisher + path over hash-only rules so each release does not need a new hash entry.
3. Keep path rules for the install folders as defense in depth.

Signing is **not** configured in every build environment (`UE_SIGN_MODE` unset → dry-run only). Until a cert ships, path/hash rules are the practical admin fix.

### Hash rule (last resort)

If policy requires file hashes: add a new hash for each published `UpdateExpress_v*.exe` **and** for the staged `UpdateExpress-Maintenance.exe` (same bits as the running build at upgrade time). Operational cost is high — prefer path or publisher.

---

## Policy authoring tips (App Control for Business)

1. Start from **Managed Installer** / managed installer + path complements if you already deploy via Intune/N-Able into Program Files.
2. Audit mode first: confirm Event Viewer → *Applications and Services Logs → Microsoft → Windows → CodeIntegrity → Operational* (and AppLocker/AppControl logs) show the blocked path.
3. Block events for `UpdateExpress-Maintenance.exe` under Temp on older builds are expected; after upgrading the **already installed** build once by hand (copy new EXE into the install folder), later self-updates should stage under the install folder.
4. Norwegian clients: the toast/dialog text *programkontrollpolicy* is the localized App Control block (Win32 `4551` / `ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION`).

---

## Manual recovery when self-update is blocked

1. Download the release EXE from the Update Express channel / GitHub Releases.
2. Copy it over the installed file:
   - Per-user: `%LOCALAPPDATA%\MicroSystems\Update Express\UpdateExpress.exe`
   - All-users: `%ProgramFiles%\MicroSystems\Update Express\UpdateExpress.exe` (elevated)
3. Or run the new EXE and use **Install / Repair** so maintenance can stage under the install folder.
4. Add the path rules above, then use in-app **Check for updates** going forward.

---

## Related

- Install scopes and maintenance flow: product README / `InstallationManager`
- Authenticode plan: project store `docs/code-signing-updateexpress.md` (Azure Artifact Signing / `UE_SIGN_MODE`)
