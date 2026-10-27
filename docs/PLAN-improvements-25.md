# NetPaw: 25-step improvement plan (2026-10-27)

## Goal
Close the measured gaps between v0.16 on main and the 1.0 bar in `docs/PLAN-v1.0.md` with 25 ordered,
verifiable steps, each one PR or one measurement.

## Given state (measured 2026-10-27)
- main 6d76a27. v0.14.0 is latest; v0.15.0-rc.1 published; v0.15.0 final staged on `release/0.15`
  @a5cf4aa, tag held until the 11-01 Actions-storage reset (t-1fa3e6). 0.16 M2 (#102–#105) is on main.
- Core tests: 269 test methods (21 theories), 360 cases, all green. No tests reference the CLI, the tray
  or the service project; those are covered only by `verify-windows` (47 assertions on a hosted runner).
- `src/NetPaw.Cli/Program.cs` is 630 lines, one file; `TrayApp.cs` 1137 lines. Docblock coverage 20 %.
- Security scan: 27 low (fleet-actions by tag at 3 sites, dependabot cooldown unset, one Path.Combine).
- Open tasks: G4 t-cc99cb, G5 t-8ff0bb, G7 t-1cb463, G9 t-e38207, G10 t-a9b04c, G11 t-d31af0/t-f7c79c,
  G15 t-dded4a, G16 t-2d66f5, t-1db04a (NETWORK deny), t-561758 (Dhcp no-op), t-b34fd9 (site dispatch),
  t-33f24d (IPv6, 1.x). Operator decisions 2026-10-16: IPv4-only 1.0, pinned root, G4 off by default.
- Harness: PLAN-G11a approved; step 2 waits for the pve thin-pool cleanup (local-lvm 87 %).

## Requirements
- Each step is one PR or one measured result, with its check.
- No pve change before the orchestrator's cleanup-done message. No `v*` tag without the orchestrator.
- Nothing here needs a paid certificate or IPv6 (decided out).
- Security-model changes (G4, G5, G9, G15) go to the orchestrator for review before merge.

## Options considered
- A. Feature-first (G4, G5 now): rejected, both change the wire or add timers; harness must exist first.
- B. Harness-first, then the cheap fixes, then the security-model items: chosen, each item then has a
  measured exit instead of a hosted-runner proxy.
- C. Docs-only 1.0: rejected, the M5 bar names tests-as-code and an e2e harness.

## Chosen approach
Fix what is measured broken (3 follow-ups) while the harness is blocked; build the harness as soon as
the cleanup lands; then run the security-model items against it in plan order (G9, G5, G4, G15).

## Steps
1. Tag v0.15.0 at the `release/0.15` head on the 11-01 wake (t-1fa3e6); verify latest, signed assets,
   site promotion. Check: `gh api releases/latest` = v0.15.0, `install.ps1` → 0.15.0.
2. t-561758: in `IntentHandler.Handle` treat `netsh ... source=dhcp` "already enabled" as a no-op success
   (match the exit text, or pre-check `PrefixOrigin`). Test: Dhcp on a DHCP adapter returns ok.
3. t-b34fd9: the release job sends `repository_dispatch netpaw-release` to netpaw-site and
   netpaw-site-deploy (needs a token with both repos; ask the orchestrator for scope). Check: a tag push
   rebuilds the site within 10 min, measured by curl.
4. Dependabot cooldown (`cooldown: default-days: 7`) in `.github/dependabot.yml`. Check: secscan −2.
5. `UpdateChecker.cs:100` Path.Combine: validate `dir` with `Path.GetFullPath` under the cache root.
   Test: a `..` component is refused.
6. fleet-actions pin by SHA: route to fleet-actions (fleet policy, G14); record the answer in SECURITY.md.
7. Split `NetPaw.Cli/Program.cs` into `Commands/*.cs` with a testable `Run(string[] argv, IO io)`;
   add `NetPaw.Cli` to the test project. Test: `service status` output parsed from a fake pipe answer.
8. Extract tray logic that has no WinForms dependency (`DetectMode`, menu model, `RefreshState` text)
   into `NetPaw.Core/Ui/*`; test the tooltip text and the QuickPanel read-only post-pass.
9. Docblocks: `autodoc.py` drafts for the 80 % undocumented symbols, reviewed by file; target ≥ 60 %.
   Check: `symbols.py lint` count.
10. G16 (S): document what uninstall keeps; `RemoveFolder` for `%LOCALAPPDATA%\NetPaw\updates`.
    verify-windows: the cache folder is gone after `/x`.
11. G10 docs: the GPO/Intune root-distribution recipe and the SmartScreen note in `docs/signed-release.md`.
12. G11 step 2 (after cleanup-done): bridge, pool, token, peer LXC; Step 0 measurements as `RESULT|…`.
13. G11 step 3: template 9160 (unattend with BitLocker off), `tools/vm-harness.sh` gate → clone → verify.
    Exit: one green run with every `RESULT|gate|…` check exercised, kill -9 orphan test passes.
14. t-1db04a on the harness: SMB open from the peer host; assert the NETWORK deny refuses it and the
    `, network logon` marker appears if a request gets through. Record in the threat model.
15. Harness in CI: n4 job `verify-vm` runs nightly inside the window; reports RESULT lines as the job
    summary; no artifact upload (storage cap).
16. G9 full: `RegNotifyChangeKeyValue` policy reload + policy source in the audit line. Harness test: a
    registry change is applied within 5 s without restart.
17. G5 design page: adapter identity by `NetCfgInstanceId` + MAC; protocol v2 with v1 readable.
    Orchestrator review before code.
18. G5 code: `ServiceRequest.AdapterId`, resolver prefers id over alias, alias display-only.
    Harness test: a renamed alias still resolves; a swapped NIC order is refused.
19. G4 design page: timer + liveness on the M2 snapshot, setting `CommitConfirm` default off, policy
    `Tier.CommitConfirm`. Orchestrator review before code.
20. G4 code + harness test: a simulated connectivity loss reverts within the timer; an explicit confirm
    cancels it.
21. G15 decision page: service-side verified install vs "updates need an admin"; measure msiexec from
    the service on the harness; orchestrator decides.
22. G7: `global.json` SDK pin, NuGet lock file + `--locked-mode`, `Deterministic` +
    `ContinuousIntegrationBuild`, `gh attestation` on release assets. Exit: two builds give identical
    pre-signing hashes.
23. Diff-view half of t-73ace8: current-vs-profile table in `PlanPreviewForm` (addresses, gateway, DNS,
    routes), fed by `ProfileLint`-style pure code in Core; tests on the diff model.
24. STE pass on the user-facing docs (`features.md`, `usage.md`, `policy.md`): `stelint.py` ≤ 1.5/100w.
25. 1.0 bar audit: walk M5 line by line with measured evidence links; open `v1.0.0-rc.1` only when every
    line has one.

## Verification
- Steps 1–11: PR merged on green plus the named check. Steps 12–22: one `RESULT|` line per claim from
  the harness, pasted into the PR. Step 25: the M5 table with links, reviewed by the orchestrator.

## Risks
- The thin-pool cleanup slips: steps 12–22 wait; steps 2–11 and 23–24 do not depend on it.
- G4 and G5 touch the wire and timers: design page and review first, code second, harness third.
- Hosted-runner CodeQL C# drops mid-run on runner-pve-r1 (seen 2026-10-15); it is not a required check.
