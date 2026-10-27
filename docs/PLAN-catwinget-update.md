# NetPaw: "Update from CatWinget" (operator feature, 2026-10-27)

## Goal
A button in the tray and a CLI command that resolve the newest NetPaw MSI from the CatWinget source,
download it, verify it twice (manifest hash and Authenticode to the pinned root) and install it, with no
winget source registered on the machine and no user-supplied URL anywhere in the path.

## Given state (measured 2026-10-27)
- CatWinget REST source: `https://winget.goes.moe`, winget REST API 1.0–1.6. `GET
  /packageManifests/smol-kitten.NetPaw` answers with `Versions[]` (0.14.0, 0.13.3), each with
  `Channel` (null today), `Installers[]` (x64, machine, msi, `InstallerUrl` on
  github.com/smol-kitten/netpaw/releases/download/…, `InstallerSha256`, `ProductCode`,
  `UpgradeCode`), and `DefaultLocale` (no `ReleaseNotesUrl` yet). `GET /information` lists the server
  versions.
- The release job publishes `NetPaw-<v>.winget.json` (facts: identifier, version, installer URL, sha256,
  product and upgrade codes, release-notes URL) and dispatches `netpaw-release` to catwinget, which
  builds the manifest from it.
- Existing update path (`NetPaw.Core/Updates`): `UpdateChecker` asks the GitHub releases API once a day,
  accepts asset URLs only under `AssetPrefix` (releases/download of this repo), downloads the MSI and
  `SIGNATURES.md`, and `UpdateTrust.Verify` requires the sha256 from SIGNATURES.md **and** an Authenticode
  chain to the root pinned in the build (`R0`, 64 MB cap). The tray re-verifies the file right before
  `msiexec /i` (UseShellExecute, so UAC prompts); nothing runs for a standard user without UAC. Policy
  `AllowUpdateCheck` (ADMX) and the user setting gate the check.
- The service (LocalSystem) has no update intent. Tiers: StandardUser / NetworkConfigOperators / Admin,
  per-intent minimum via `PolicyTiers`, registry policy `Tier.<Intent>`, floors.
- 1.0 plan: G15 (t-dded4a, 0.17) is "standard-user update path: service-side verified install vs
  updates need an admin". this feature answers G15.
- Tests: 364 Core cases. `UpdateVerifyTests` has fixtures for hash and Authenticode refusal.

## Requirements
- Resolve from the CatWinget REST source only. base URL pinned in the build. HTTPS with certificate
  validation. no automatic redirects. a redirect is followed only to a host on the allow-list.
- Pick the newest version for the machine's architecture (x64) and scope (machine). stable channel by
  default. an opt-in prerelease channel.
- Verify the manifest's sha256 **and** the MSI's Authenticode signature against the pinned root before
  anything runs. Refuse a downgrade unless the caller said so explicitly (CLI flag, never the button).
- Never install from a user-supplied URL or path. The installer that runs is the one the service itself
  downloaded and verified.
- Admin-only action by default. a policy can disable it (ADMX) and can set the tier. every step in the
  audit log.
- Unit tests with a fixture source. no live install in CI. The real upgrade runs on the G11 harness.
- Must not happen: the service running a file the tray handed it. a URL from settings or a request;
  the Authenticode check skipped because the hash matched. the stable channel offering a prerelease.

## Options considered
- A. Tray downloads + verifies, then `msiexec` with UAC (today's path, new source only). Works for
  admins. a standard user gets a UAC prompt or nothing. Not chosen alone: it does not answer G15.
- B. New service intent `InstallUpdate`: the service resolves, downloads into its own folder, verifies
  twice, then starts `msiexec /qn /l*v` detached. Chosen: the trust decision stays in the one process
  that is already trusted, and the client only names a version.
- C. Elevated helper exe (scheduled task or COM elevation). Rejected: a third privileged surface.
- D. `winget upgrade` through the registered source. Kept as the fallback when the source is registered
  and the service is absent (portable mode).

## Chosen approach
B with A as the no-service fallback. One pure resolver in Core (`CatWingetSource`) is shared by the
service, the CLI and the tray. only the service installs. The intent is Admin by default with a floor of
NetworkConfigOperators, and a policy switch turns the feature off. The service never accepts a URL or a
path. it accepts a version string and resolves it itself.

## Design
- **Core** `Updates/CatWingetSource.cs`: `const BaseUrl = "https://winget.goes.moe"`;
  `const PackageId = "smol-kitten.NetPaw"`; allow-listed download hosts: `github.com` under
  `UpdateChecker.AssetPrefix`, and `winget.goes.moe`. `Resolve(json, current, arch, scope, channel,
  allowDowngrade)` → `CatWingetUpdate(Version, InstallerUrl, Sha256, ProductCode, ReleaseNotesUrl)` or a
  reason. Rules: highest `PackageVersion` whose `Channel` matches (null/"stable" for stable, "prerelease"
  for the opt-in channel). installer with `Architecture == x64`, `Scope == machine`, `InstallerType ==
  msi`. `InstallerUrl` host on the allow-list. `Version > current` unless `allowDowngrade`.
  `ReleaseNotesUrl` from `DefaultLocale.ReleaseNotesUrl`, else the GitHub tag URL built from the version.
- **Core** `Updates/UpdateTrust.VerifyManifest(bytes, sha256FromManifest, roots)`: the hash must match and
  `MsiAuthenticode.Verify` must pass; both reasons are reported; the hash alone is never enough.
- **HTTP**: a client with `AllowAutoRedirect = false`, default certificate validation, 15 s timeout for
  the manifest, 64 MB cap for the MSI. a 3xx is followed at most twice and only to an allow-listed host.
- **Service** intent `InstallUpdate { Version?, Channel?, AllowDowngrade }` (PolicyIntent
  `InstallUpdate`, default tier Admin, floor NetworkConfigOperators). Steps, each an audit line:
  resolve → download to `%ProgramData%\NetPaw\service\updates\` (service-owned ACL) → hash → Authenticode
  → write `update.log` → start `msiexec /i "<file>" /qn /norestart /l*v "<log>"` detached with a 2 s
  delay → answer the client before msiexec stops the service. Refuses when policy `AllowCatWingetUpdate`
  is 0, when the version is not newer (unless AllowDowngrade), when either check fails, when the
  download exceeds the cap, or when the service folder check fails.
- **Policy**: `AllowCatWingetUpdate` (ADMX + Intune OMA-URI sample, default 1). `Tier.InstallUpdate`
  (registry tier override, floor NCO). `docs/policy.md` rows.
- **Tray**: "Update from CatWinget" in the menu and in Settings → Updates: shows current vs available,
  the release-notes link, "Install" (service mode: sends the intent. no service: fallback A with UAC;
  neither possible: explains). "Prerelease channel" checkbox in Settings → Updates.
- **CLI**: `netpaw-cli update --source catwinget [--check] [--channel prerelease] [--allow-downgrade]`;
  `--check` never installs; `--allow-downgrade` is CLI-only.
- **Channels**: stable = `Channel` null or "stable". prerelease = `Channel == "prerelease"` within the
  same PackageIdentifier (the recommendation to the catwinget session. if catwinget publishes a separate
  identifier instead, `PrereleasePackageId` is a second constant and the resolver queries both).
- **Fallback D**: when the source is registered (`winget source list` names winget.goes.moe) and the
  service is absent, the tray offers `winget upgrade --id smol-kitten.NetPaw` through the shell.

## Steps
1. This plan page. Orchestrator review (security-model change: a new privileged intent and a new
   download path). PR, docs only.
2. Core resolver + `VerifyManifest` + fixture JSON (the live manifest of 0.14.0 saved as a test asset) +
   unit tests: newest-version selection, channel rules, arch/scope filter, allow-list refusal,
   downgrade refusal, hash mismatch refusal, Authenticode refusal with hash match, redirect refusal.
   PR, Core only.
3. Service intent + policy + tier + audit lines + `docs/policy.md`. verify-windows: a StandardUser
   call is refused with "needs tier", an Admin call against an unreachable source fails at "resolve"
   with the reason and installs nothing. PR, orchestrator review before merge.
4. ADMX + Intune sample for `AllowCatWingetUpdate` and `Tier.InstallUpdate`. PR, deploy/ only.
5. Tray button + Settings channel checkbox + CLI command. QuickPanel greying in user mode. PR.
6. Docs: `features.md` Updates section, `usage.md` CLI, SECURITY.md threat-model lines (the service
   downloads from one pinned host, verifies twice, runs its own file). PR.
7. Harness item (PLAN-tests E): a real upgrade from the previous MSI through the intent on a clone,
   with a fixture CatWinget server on the peer host serving the manifest. After PLAN-G11a step 3.
8. Fallback D (winget upgrade when the source is registered). PR, tray only. Optional, last.

## Sequence in the roadmap
After v0.15.0 final and the 0.16.0-rc.1 cut: steps 1–2 now (docs and pure Core, no risk), steps 3–6 as
the 0.17 (M3) item that replaces G15 (t-dded4a closes into this plan), step 7 with the harness, step 8
after. It does not block the 1.0 bar. the 1.0 bar's "standard-user update path decision" is answered by
B.

## Verification
- Unit: every rule in step 2 has a test. the fixture manifest is the live 0.14.0 document.
- verify-windows: the two refusals in step 3 appear in the job summary. no msiexec runs on the runner.
- Harness: `RESULT|update|installed:<from>-><to>` and the service answers Status with the new version.
- Audit: one line per step for one install, in order, in the Event Log.

## Risks
- msiexec stops the service that started it: the service answers first and starts msiexec detached
  with a delay. the harness measures that the upgrade completes and the new service starts.
- CatWinget down or wrong: the button says "source unreachable" and installs nothing. the GitHub check
  stays as it is.
- The MSI hosted on GitHub while the manifest comes from CatWinget: two hosts, both pinned. a redirect
  to any other host is refused.
- A prerelease channel without a catwinget convention: the constant is one line to change. stable is
  unaffected.
