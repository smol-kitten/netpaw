# NetPaw: 25-step improvement plan (2026-10-27)

## Goal
Close the measured gaps between v0.16 on main and the 1.0 bar in `docs/PLAN-v1.0.md` with 25 ordered,
verifiable steps, each one PR or one measurement.

## Given state (measured 2026-10-27)
- main 6d76a27. v0.14.0 is latest. v0.15.0-rc.1 published. v0.15.0 final staged on `release/0.15`
  @a5cf4aa, tag held until the 11-01 Actions-storage reset (t-1fa3e6). 0.16 M2 (#102–#105) is on main.
- Core tests: 269 test methods (21 theories), 360 cases, all green. No tests reference the CLI, the tray
  or the service project. those are covered only by `verify-windows` (47 assertions on a hosted runner).
- `src/NetPaw.Cli/Program.cs` is 630 lines, one file. `TrayApp.cs` 1137 lines. Docblock coverage 20 %.
- Security scan: 27 low (fleet-actions by tag at 3 sites, dependabot cooldown unset, one Path.Combine).
- Open tasks: G4 t-cc99cb, G5 t-8ff0bb, G7 t-1cb463, G9 t-e38207, G10 t-a9b04c, G11 t-d31af0/t-f7c79c,
  G15 t-dded4a, G16 t-2d66f5, t-1db04a (NETWORK deny), t-561758 (Dhcp no-op), t-b34fd9 (site dispatch),
  t-33f24d (IPv6, 1.x). Operator decisions 2026-10-16: IPv4-only 1.0, pinned root, G4 off by default.
- Harness: PLAN-G11a approved. step 2 waits for the pve thin-pool cleanup (local-lvm 87 %).

## Requirements
- Each step is one PR or one measured result, with its check.
- No pve change before the orchestrator's cleanup-done message. No `v*` tag without the orchestrator.
- Nothing here needs a paid certificate or IPv6 (decided out).
- Security-model changes (G4, G5, G9, G15) go to the orchestrator for review before merge.

## Options considered
- A. Feature-first (G4, G5 now): rejected, both change the wire or add timers. harness must exist first.
- B. Harness-first, then the cheap fixes, then the security-model items: chosen, each item then has a
  measured exit instead of a hosted-runner proxy.
- C. Docs-only 1.0: rejected, the M5 bar names tests-as-code and an e2e harness.

## Chosen approach
Fix what is measured broken (3 follow-ups) while the harness is blocked. build the harness as soon as
the cleanup lands. then run the security-model items against it in plan order (G9, G5, G4, G15).

## Steps
1. Tag v0.15.0 at the `release/0.15` head on the 11-01 wake (t-1fa3e6). Gate: the orchestrator's GO
   exists (m-6438) and the storage-hold release message is in the netpaw inbox before the tag command.
   Then verify latest, signed assets, site promotion. Check: `gh api releases/latest` = v0.15.0,
   `install.ps1` → 0.15.0.
2. t-561758: in `IntentHandler.Handle` treat `netsh ... source=dhcp` "already enabled" as a no-op success
   (match the exit text, or pre-check `PrefixOrigin`). Test: Dhcp on a DHCP adapter returns ok.
3. t-b34fd9, ask first: the release job (ci.yml `release`, exists, dispatches to catwinget) also
   needs to reach netpaw-site and netpaw-site-deploy. Step 3a: ask the orchestrator whether the existing
   dispatch token may cover those two repos, or whether the site repos should pull release events
   with their own scheduled check (no new token). Step 3b: implement the answer. Check: a tag push
   rebuilds the site within 10 min, measured by curl.
4. Dependabot cooldown (`cooldown: default-days: 7`) in `.github/dependabot.yml`. Check: secscan −2.
5. `UpdateChecker.cs:100` Path.Combine: validate `dir` with `Path.GetFullPath` under the cache root.
   Test: a `..` component is refused.
6. fleet-actions pin by SHA: route to fleet-actions (fleet policy, G14). record the answer in SECURITY.md.
7. Split `NetPaw.Cli/Program.cs` into `Commands/*.cs` with a testable `Run(string[] argv, IO io)`;
   add `NetPaw.Cli` to the test project. Test: `service status` output parsed from a fake pipe answer.
8. Extract tray logic that has no WinForms dependency (`DetectMode`, menu model, `RefreshState` text)
   into `NetPaw.Core/Ui/*`. test the tooltip text and the QuickPanel read-only post-pass.
9. Docblocks: `autodoc.py` drafts for the 80 % undocumented symbols, reviewed by file. target ≥ 60 %.
   Check: `symbols.py lint` count.
10. G16 (S): document what uninstall keeps. `RemoveFolder` for `%LOCALAPPDATA%\NetPaw\updates`.
    verify-windows: the cache folder is gone after `/x`.
11. G10 docs: the GPO/Intune root-distribution recipe and the SmartScreen note in `docs/signed-release.md`.
12. G11 step 2, one PR or measurement each, started only after the orchestrator's cleanup-done
    message id is recorded on t-f7c79c: 12a bridge `vmbr-nptest`, 12b pool `netpaw-harness`, 12c the
    pool-scoped token (ACL dump as `RESULT|acl|…`), 12d the peer LXC. Step 0 measurements per
    PLAN-G11a between 12c and 12d.
13. G11 step 3, split: 13a template 9160 (unattend with BitLocker off, build steps recorded in
    PLAN-G11a). 13b `tools/vm-harness.sh` gate → clone → verify. 13c the kill -9 orphan test. Exit: one
    green run with every `RESULT|gate|…` check exercised.
14. t-1db04a on the harness: SMB open from the peer host. assert the NETWORK deny refuses it and the
    `, network logon` marker appears if a request gets through. Record in the threat model.
15. Harness in CI: n4 job `verify-vm` runs nightly inside the G11 window (02:30–07:00 Berlin, per
    PLAN-G11a, outside PawCheck's 00:30–02:30). It reports RESULT lines as the job summary, no artifact
    upload (storage cap).
16. G9 full, two PRs: 16a `RegNotifyChangeKeyValue` policy reload. 16b policy source in the audit
    line. Harness test: a registry change is applied within 5 s without restart.
17. G5 design page (one PR): adapter identity by `NetCfgInstanceId` + MAC, protocol v2 with v1
    readable. Orchestrator review before any code.
18. G5 code, two PRs: 18a `ServiceRequest.AdapterId` + resolver prefers id over alias. 18b alias
    display-only in the tray and CLI. Harness test: a renamed alias still resolves, a swapped NIC
    order is refused.
19. G4 design page (one PR): timer + liveness on the M2 snapshot, setting `CommitConfirm` default
    off, policy `Tier.CommitConfirm`. Orchestrator review before any code.
20. G4, three steps: 20a service timer + liveness code (PR, orchestrator review). 20b harness test:
    a simulated connectivity loss reverts within the timer. 20c harness test: an explicit confirm
    cancels the timer.
21. G15 decision page: service-side verified install vs "updates need an admin". measure msiexec from
    the service on the harness. orchestrator decides.
22. G7: `global.json` SDK pin, NuGet lock file + `--locked-mode`, `Deterministic` +
    `ContinuousIntegrationBuild`, `gh attestation` on release assets. Exit: two builds give identical
    pre-signing hashes.
23. Diff-view half of t-73ace8: current-vs-profile table in `PlanPreviewForm` (addresses, gateway, DNS,
    routes), fed by `ProfileLint`-style pure code in Core. tests on the diff model.
24. STE pass on the user-facing docs (`features.md`, `usage.md`, `policy.md`): `stelint.py` ≤ 1.5/100w.
25. 1.0 bar audit: walk M5 line by line with measured evidence links. Gate: the orchestrator reviews
    the table and gives GO before `v1.0.0-rc.1` is tagged. No tag without that message.
## Verification
- Steps 1–11: PR merged on green plus the named check. Steps 12–22: one `RESULT|` line per claim from
  the harness, pasted into the PR. Step 25: the M5 table with links, reviewed by the orchestrator.

## Risks
- The thin-pool cleanup slips: steps 12–22 wait. steps 2–11 and 23–24 do not depend on it.
- G4 and G5 touch the wire and timers: design page and review first, code second, harness third.
- Hosted-runner CodeQL C# drops mid-run on runner-pve-r1 (seen 2026-10-15). it is not a required check.

## Review reconciliation

- Round 1 (nano): tag gates adopted, steps 1 and 25 now name the orchestrator message that must exist
  before any `v*` tag. Step 3 adopted as ask-first (3a/3b), the release job exists (ci.yml `release`,
  it dispatches to catwinget today), the token scope is the orchestrator's call. Cleanup-done
  confirmation adopted: the message id is recorded on t-f7c79c before 12a. The window adopted from
  PLAN-G11a (02:30–07:00 Berlin). Bundled steps split into sub-steps (12a–d, 13a–c, 16a–b, 18a–b,
  20a–c), each one PR or one measurement. Execution order adopted: non-pve, non-tag work first, one
  checkpoint, then pve and tag work.
