# NetPaw v0.2 plan — UI polish, enterprise deployment, online profile repos

## Goal

Ship NetPaw v0.2 where an admin can (1) do every daily action from one clean panel, (2) push
managed base profiles to a fleet with Intune or GPO, and (3) pull hardware setup profiles from
one or more online repositories that are not part of the build.

## Given state

- Repo `smol-kitten/netpaw`, branch `main`, tag `v0.1.0` released 2026-09-15.
- Layout: `NetPaw.Core` (models, planner, reach resolver, presets, JSON store; 74 tests),
  `NetPaw.Tray` (WinForms, 1150 lines, 6 forms), `NetPaw.Cli`.
- Presets are an embedded `presets.json` (58 entries) plus a per-user override file.
- Profiles and settings live in `%APPDATA%\NetPaw\` as JSON. No machine-wide location exists.
- The tray runs with `requireAdministrator`. No installer exists; the release is a zip.
- v0.1 UI: dark quick panel, a dense editor form with 13 rows, a settings dialog with 8 rows.
  Verified on the Win11 VM (VMID 150). The VM is stopped now.
- No standing rules exist for scope `netpaw`. No open tasks touch it.
- Fleet rules: branch → PR → merge, self-hosted Linux runner, cross-compile with
  `EnableWindowsTargeting`, README needs screenshots.

## Requirements

R1. The UI stays simple. One panel for daily use. No feature needs more than two clicks.
R2. The UI looks modern: consistent spacing, one accent color, readable at 100 % and 150 % DPI.
R3. Net-admin language everywhere: "adapter", "prefix", "gateway", not consumer wording.
R4. Enterprise: an admin can deploy read-only base profiles to many PCs with Intune (Win32 app
    or PowerShell script) or GPO. Users see them but cannot edit or delete them.
R5. Enterprise: an admin can pin settings (work adapter, panel hotkey, allowed repos) machine-wide.
R6. Online repos: a profile pack is a JSON index at a URL. The app fetches it on demand, caches it,
    and shows its entries as presets/profiles. Nothing from the pack is bundled in the build.
R7. Online repos: the user can add repo URLs. A company can pre-set repo URLs machine-wide.
R8. Online repos: a community pack lives in this repo under `packs/` and is served raw from
    GitHub. It is excluded from the build output.
R8a. Lockdown semantics are explicit policy keys, each default "allowed":
    `AllowUserProfiles` (save own profiles), `AllowUserRepos` (add repo URLs),
    `AllowReach` (reach mode), `AllowTempAddresses` (secondary temp addresses),
    `AllowDhcp` (DHCP button), `AllowUnsignedRepos` (show entries from repos without a pinned key).
    A denied action is hidden from the panel and menu and returns exit code 4 in the CLI.
R9. Must NOT: no auto-apply of any downloaded profile, no network access without a user action
    or an explicit machine policy, no telemetry, no size growth of the framework-dependent zip
    above 1 MB.
R10. Must NOT: no breaking change to `profiles.json` v0.1 files.

## Options considered

- O1. Rewrite the tray in WPF or WinUI 3 for a modern look. Not chosen: WinUI needs the
  Windows App SDK runtime, WPF doubles the exe size and the rewrite gains nothing the admins asked for.
- O2. Keep WinForms and refine the existing dark theme: spacing system, section headers, inline
  validation, adapter status chips. Chosen: smallest change, keeps the 630 KB binary.
- O3. Enterprise config via registry policy keys (`HKLM\Software\Policies\NetPaw`) with an ADMX
  template. Chosen for settings and repo URLs: this is what Intune (via OMA-URI / ADMX ingestion)
  and GPO both consume natively.
- O4. Enterprise profiles as files in `%ProgramData%\NetPaw\profiles.d\*.json`. Chosen: Intune
  Win32 apps and GPO file preferences can drop files; JSON stays diff-able; no registry size limits.
- O5. Store enterprise profiles in the registry as JSON blobs. Not chosen: hard to author, 1 MB
  value limit, invisible to git.
- O6. Online repo format = GitHub release assets. Not chosen: ties the format to GitHub. A plain
  `index.json` at any HTTPS URL works with GitHub raw, an S3 bucket, or an intranet IIS folder.
- O7. Signed packs. Chosen in reduced form for v0.2: the index format carries an optional
  Ed25519 `signature` over the canonical entries array. A repo URL can be pinned to a public key
  (user setting or policy). If a key is pinned, the app rejects an unsigned or badly signed index.
  If no key is pinned, the app shows the entries with an "unsigned" badge. Verification uses
  `System.Security.Cryptography` only; no new dependency. Key generation and signing live in
  `netpaw-cli pack sign`. The community pack is signed with a key kept in the repo's CI secrets.
  This settles the format now, so v0.3 can only tighten policy, not change data.
- O8. MSI/MSIX installer. Chosen as a minimal WiX MSI: Intune Win32 needs an install/uninstall
  command with a detection rule, and an MSI gives both for free. The zip stays for portable use.

## Chosen approach

Keep WinForms and the current architecture. Add a second, read-only profile source (machine
directory) and a policy reader (registry) in Core. Add a `RepoClient` in Core that fetches an
`index.json`, caches it under `%LOCALAPPDATA%\NetPaw\cache\`, and exposes entries as presets.
Ship a community pack under `packs/community/` in this repo, served raw from GitHub, and an MSI
for Intune/GPO deployment. Polish the three existing windows; do not add new ones.

## Steps

### A. UI polish (Tray)

1. Add `Theme.Spacing` (4/8/12/16 px) and a `Section(string)` header control. File: `Theme.cs`.
2. Rework `ProfileEditorForm` into three sections: *Identity* (name, adapter, hotkey),
   *IPv4* (mode, addresses, gateway, DNS, suffix), *Advanced* (metric, VLAN, routes, note).
   Collapse *Advanced* by default. Show inline errors under the field, not in one label.
3. Add an adapter status strip to the panel header: link state dot, speed, MAC on hover.
   File: `QuickPanel.cs`.
4. Add a read-only badge ("managed") and a source badge ("repo: company") to list rows in the
   panel and the editor. Files: `QuickPanel.cs`, `ProfileEditorForm.cs`.
5. Replace balloon tips with a small in-panel status line for success; keep balloons for errors.
   File: `TrayApp.cs`.
6. Test both windows at 150 % DPI on the VM. Fix clipped labels.

### B. Enterprise deployment (Core + docs + packaging)

7. Add `MachineStore`: read-only profiles from `%ProgramData%\NetPaw\profiles.d\*.json`
   (each file is a list of profiles). Mark them `Managed = true`. Merge into `Profiles` with
   the user list; managed entries win on name clash. File: `Store/MachineStore.cs`.
8. Add `PolicyReader`: read `HKLM\Software\Policies\NetPaw` values `WorkAdapter`,
   `PanelHotkey`, `ConfirmBeforeApply`, `RepoUrls` (REG_MULTI_SZ, each `url|keyId:base64`),
   and the six `Allow*` keys from R8a. Policy values override `settings.json` and grey out the field in the
   settings dialog. File: `Store/PolicyReader.cs`, `SettingsForm.cs`.
9. Add `netpaw-cli export --managed <file>` and `netpaw-cli import <file>` so an admin can build
   a base pack from a reference PC. File: `Cli/Program.cs`.
10. Write `deploy/NetPaw.admx` + `NetPaw.adml` for the policy keys. Folder: `deploy/gpo/`.
11. Add a WiX v5 project `deploy/msi/` producing `NetPaw-<version>.msi`: installs both exes to
    `%ProgramFiles%\NetPaw`, creates `%ProgramData%\NetPaw\profiles.d\`, optional per-machine
    logon task. Build it in CI on the tag job (WiX runs on Linux via the `wix` dotnet tool).
12. Write `docs/ENTERPRISE.md`: Intune Win32 app (install/uninstall/detection commands), Intune
    OMA-URI for the ADMX-ingested policies, GPO file preference for `profiles.d`, and the
    PowerShell one-liner alternative.

### C. Online profile repositories (Core + Tray + CLI + packs)

13. Define the pack format `docs/PACK-FORMAT.md`: `index.json` = `{ "name", "version",
    "updated", "entries": [ { "id", "vendor", "model", "kind": "preset|profile", "profile": {…},
    "note", "url", "sha256" } ], "signature": { "alg": "ed25519", "keyId", "value" } }`.
    A `profile` uses the v0.1 profile schema. The signature covers the UTF-8 bytes of the
    canonical (sorted keys, no whitespace) `entries` array.
13a. Add `Repos/PackSigner.cs` (sign, verify) and `netpaw-cli pack keygen|sign|verify`.
    Add a CI step that signs `packs/community/index.json` on the tag job with the secret key.
14. Add `Repos/RepoClient.cs`: `Fetch(url)` with `HttpClient`, 10 s timeout, ETag cache in
    `%LOCALAPPDATA%\NetPaw\cache\<hash>.json`, offline fallback to the cache. Verify `sha256`
    per entry when present.
15. Add `Repos/RepoSource.cs`: merges entries from all configured repos into the preset list
    with a `Source` label. User repos come from `settings.json` (`RepoUrls`), machine repos from
    policy. Policy `AllowUserRepos=0` hides the add-repo UI.
16. Tray: settings dialog gets a *Repositories* list (add/remove/refresh, last sync time, entry
    count, error text). Panel search includes repo entries with the source badge.
17. CLI: `netpaw-cli repo list|add <url>|remove <url>|sync|search <q>`.
18. Create `packs/community/index.json` from the 58 bundled presets plus full profiles for the
    common setup cases (MikroTik first boot, UniFi adoption, Fortigate port1, FRITZ!Box rescue).
    Add `packs/**` to the tray and CLI `.csproj` as `<None Remove>` so nothing ships in the build.
19. Ship a default repo URL for the community pack:
    `https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json`.
    It is listed but not fetched until the user clicks *Sync* or opens the repo tab (R9).
20. Add a CI job `packs-validate`: schema check of every `packs/*/index.json` and sha256 check.

### D. Wrap-up

21. Bump versions to 0.2.0, update README (new screenshots, enterprise + repo sections).
22. Verify on VM 150 (steps in Verification). Tag `v0.2.0`.

## Verification

- `dotnet test`: existing 74 tests pass. New tests: MachineStore merge (managed wins, read-only
  flag), PolicyReader precedence (policy > settings), RepoClient (cache hit, ETag, bad sha256
  rejected, offline fallback), pack schema. Target: ≥ 100 tests.
- On VM 150: drop a JSON into `%ProgramData%\NetPaw\profiles.d\`, restart the tray, the profile
  shows with the *managed* badge and the delete button is disabled.
- On VM 150: set `HKLM\Software\Policies\NetPaw\PanelHotkey`, restart, the settings field is
  greyed and shows the policy value.
- On VM 150: install the MSI silently (`msiexec /i NetPaw.msi /qn`), the detection path exists,
  `msiexec /x … /qn` removes it. Both commands exit 0.
- Unit: a tampered entry fails sha256; a tampered index fails the signature; an unsigned index
  with a pinned key is rejected; `AllowReach=0` hides the reach row and the CLI returns 4.
- On VM 150: add the community repo URL, click *Sync*, entry count > 58, search "mikro" shows a
  repo entry with the source badge. Pull the network cable (disable NIC) and restart: entries
  still show from cache.
- Size: `netpaw-win-x64.zip` stays under 1 MB (v0.1: 547 KB).
- README screenshots refreshed from the VM at 100 % DPI.

## Risks

- WiX on Linux: the `wix` dotnet tool builds MSI cross-platform, but custom actions do not.
  Mitigation: no custom actions; the logon task is created by the app, not the installer.
- Registry policy read needs `Microsoft.Win32.Registry`; it is Windows-only. Mitigation: guard
  with `OperatingSystem.IsWindows()`; tests use an `IPolicySource` fake.
- Community pack accuracy: wrong factory defaults waste an admin's time. Mitigation: every entry
  carries a `note` and a `url` to the vendor doc; the CI schema job rejects entries without `vendor`,
  `model`, `ip`. PRs are the correction path.
- Raw GitHub URL rate limits (60/h unauthenticated). Mitigation: ETag cache, manual sync only.
- Editor rework can regress the verified fields. Mitigation: `ReadFields`/`LoadFields` stay;
  only the layout container changes; the VM check covers save → apply → verify with `Get-NetIPAddress`.
- Scope creep: three feature areas in one release. Mitigation: three PRs (A, B, C), each
  mergeable alone; C depends on nothing in B except the policy `RepoUrls` key, which is read
  optionally.
