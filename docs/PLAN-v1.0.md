# Plan: NetPaw 0.15 → 1.0

For the orchestrator's review (revision 2 after GO-WITH-CHANGES, m-5784). Short sentences on purpose.
Sources: the operator's 2026-10-15 directive; the audit page .claude/wiki/netpaw-audit-2026-10.md (`wiki.py get netpaw-audit-2026-10 --scope netpaw`), which holds the gap list G1–G16.

## Goal

Ship v0.14.0 now. Then small themed releases that close the audit gaps, up to a 1.0 that an enterprise
can deploy, verify, update and roll back with confidence.

## Given state (measured 2026-10-15)

- v0.13.3 is the public stable. v0.14.0-rc.2 is commit 2388025 on main. It is verified: CI incl.
  verify-windows on the rc.2 tag run, the VM pass of 2026-10-12, the orchestrator's check (m-5066), the
  operator's go. Evidence: audit page, "Validation evidence". Commits after 2388025 on main (#89 user-mode
  UI, #90 doc ranges, #91 hardening nits) are NOT in rc.2 and ride 0.15; a tag at 2388025 is the rc.2
  tree exactly, and the tag's own CI run re-verifies it. Later merges to main do not change a tag.
- CI runners: build/sign/release on self-hosted n4; MSI build and verify-windows on GitHub-hosted
  windows-latest. n4 has quiet hours 16–24 Berlin.
- Audit: the core is strong (typed intents, pipe DACL + Identification + SCM-PID, TOKEN_GROUPS tiers,
  floor + last-known-good, handle-based profiles.d gate, Event Log audit, UpdateTrust-verified updates,
  signed releases). Gaps G1–G16 are tasks in scope netpaw. G14 is closed by fleet policy (first-party
  reusable-workflow tags are immutable by ruleset; exact-tag pinning accepted; SHA only for third-party).
- secscan: 0 secrets; #91 fixes the two real items.
- Stale docs on main: README.md:149 "No drivers, no service"; docs/ENTERPRISE.md:28 "No service, no
  scheduled task"; docs/signed-release.md rollback names v0.12.2 and omits "uninstall first"
  (MajorUpgrade refuses downgrades).

## Requirements (from the operator)

R1 Release 0.14 now. R2 Release a 0.15 with a QoL set. R3 A 1.0 plan and feature set. R4 Validate
functionality and ensure safety at each step. R5 Fleet rules: branch → PR → merge; security PRs get
the orchestrator's review; no `v*` tag without the orchestrator's OK; a final equals a verified rc.

## Options considered

- 0.14.0 from main head (includes #89–#91): needs an rc.3. Rejected: delays R1 for non-security items.
- Fold G1–G3 into 0.14.0: changes the verified build. Rejected.
- One big 1.0 PR chain. Rejected: each gap is its own reviewed PR; releases are cut as themes land.

## Chosen approach

v0.14.0 = the verified rc.2 commit. Then one theme per release, each cut only from a verified rc, each
item sized S/M/L, each milestone with a rollback line. Who verifies: netpaw (me) measures on CI and the
VM and records the evidence in the PR body; the orchestrator reviews every security PR before merge; the
operator verifies each rc before the final tag; the orchestrator gives the tag OK.

## Steps

### M0 — v0.14.0 (now)
- Tag v0.14.0 at 2388025 after 00:00 Berlin (n4 quiet hours); orchestrator GO on record (m-5760/m-5774).
  Release = latest, signed assets, notes = rc.1+rc.2 changes and known limits. The site promotes 0.14.0
  as the default install (intended). (S)
- Docs-only PR on main, not touching the build: fix the three stale docs above; add "uninstall first,
  then install the previous MSI" to the rollback recipe. (S)
- #90 merged; #91 merges on green (hygiene, orchestrator-cleared). (S)
- Rollback: uninstall 0.14.0, install the 0.13.3 MSI; profiles.d and settings are kept (schema unchanged).

### M1 — v0.15.0 "cheap safety + docs"
- G1 netsh/ipconfig/powershell by full `%SystemRoot%\System32` path; update FileName/CommandLine tests. (M)
- G2 trimmed `sc privs` + `sc sidtype unrestricted` next to `sc failure`; VM-measured 2026-10-16 that netsh still applies.
  `restricted` was tried and measured out: a write-restricted token gets "access denied" from `netsh ... set address`. (S)
- G3 `PIPE_REJECT_REMOTE_CLIENTS` + FirstPipeInstance on every re-created instance. (S)
- G8 SECURITY.md + threat-model page with the four documented decisions. (M)
- G6-docs: state IPv4-only and the IPv6 DNS-leak caveat. (S)
- G9-lite: fail closed on an unknown policy schema version (the cheap half of G9). (S)
- QuickPanel greying in user mode (completes #89). (S)
- Exit, safety (measured by netpaw, reviewed by the orchestrator): VM trace shows full-path launches;
  `sc qsidtype` = UNRESTRICTED and `sc qprivs` = the trimmed set with a working apply (VM pass 4, 2026-10-16); a remote pipe open
  is refused and a pre-start squat still fails loud; SECURITY.md and the threat model exist.
- Exit, release: CI green on the rc tag incl. verify-windows; unit suite green; operator installs the rc
  and confirms autostart + one apply; orchestrator tag OK.
- Rollback: uninstall, install 0.14.0 MSI; data unchanged.

### M2 — v0.16.0 "QoL"
- G12 show the effective tier and why an intent was refused (tray status + CLI) with the audit line. (M)
- G13 reworded: snapshot the adapter pre-state before every apply (the existing NetPawService
  snapshot-to-profile) and offer a manual "revert to pre-apply" from that snapshot. (M)
- t-dd3d27 only External vSwitch vEthernet is HostFacing. (S)
- Profile validation on save: gateway inside the prefix, duplicate-IP warning. (S)
- Exit (measured): a StandardUser DeleteRoute shows tier + reason; one click restores the pre-apply
  addresses/gateway/DNS (VM); Default Switch is not HostFacing. Release exit as M1. Rollback as M1.

### M3 — v0.17.0 "safe to apply from anywhere"
- G4 commit-confirmed rollback: on the M2 pre-state snapshot, add a service-side timer and a liveness
  check (gateway/DNS); the service reverts on its own if the caller does not confirm or liveness fails.
  Ships behind a setting, OFF by default; a policy may enable it (operator decision 2026-10-16). (L)
- G5 target adapters by GUID/LUID + MAC in intents and profiles; alias display-only; protocol v2, v1 kept
  readable. (L)
- G9 full: RegNotifyChangeKeyValue + policy source in the audit line. (M)
- G15 standard-user update path: decide service-side verified install vs "updates need an admin"; plan
  page first. (M/L)
- G16 uninstall cleanup: document what stays (profiles.d, service state) and clean the update cache. (S)
- Exit (measured): a simulated connectivity loss reverts within the timer on the VM; a renamed alias no
  longer redirects an apply; an update as a standard user either succeeds via the service or fails with
  the documented message. Rollback: uninstall, previous MSI; v1 profiles still readable.

### M4 — v0.18.0 "supply chain"
- G7 scoped: deterministic bin publish (Deterministic + ContinuousIntegrationBuild), global.json SDK pin,
  NuGet lock + locked restore, attest the bin artifact (attest:false today), SBOM attestation,
  `gh attestation verify` in the release recipe. MSI and Authenticode timestamps are not
  byte-reproducible: a stated non-goal. (M)
- G10 private-PKI reality in docs now (S). Decided 2026-10-16: 1.0 stays on the self-signed pinned root
  with the GPO/Intune root recipe; no paid OV / Azure Trusted Signing certificate; the SmartScreen
  "unknown publisher" note stays in the docs.
- G6 decided 2026-10-16: 1.0 is documented IPv4-only (the dual-stack DNS caveat is in docs/features.md,
  #101); IPv6 address/DNS/route/reach support is a 1.x milestone, not a 1.0 item.
- Exit: `gh attestation verify` passes on the release assets; two builds of one commit give identical
  PRE-SIGNING bin hashes (signed bins carry RFC 3161 timestamps and never match). Rollback as M1.

### M5 — v1.0.0: the bar
1.0 ships only when ALL of these hold:
- Zero open safety/high tasks in scope netpaw.
- verify-windows and the G11 harness are green on the 1.0 rc.
- Upgrades 0.13.3 → rc and 0.14.0 → rc are tested on the harness.
- Rollback (uninstall + previous MSI) is tested with settings and profiles preserved.
- Supported builds are named: Windows 11 23H2+ and Server 2022+ (Windows 10 is EOL).
- Security tests as code, each asserted in CI: pipe DACL, squat, filtered-token admin, remote pipe,
  oversized JSON, junction in profiles.d.
- Docs bar: install/uninstall (MSI + winget), admin guide (ADMX, Intune OMA-URI, policy table, audit log
  format), threat model, rollback/recovery runbook, docs/TELEMETRY.md linked, README screenshots.
- Support policy: supported builds, .NET 10 LTS dependency (to Nov 2028), pipe-protocol and
  policy-schema versioning with a deprecation window; a mismatched client is rejected with a clear message.
- G11a research spike (own plan page) → G11 harness: n4 drives Proxmox (clone from template, second
  management NIC, run the verify script, destroy). (L)
- Rollback: uninstall, install the last 0.x MSI; data schema unchanged.

## Verification

Per milestone: the measured exit lists above; CI green incl. verify-windows; unit suite green with each
gap's tests; orchestrator review on every security PR; finals only from a verified rc with the
orchestrator's tag OK. How VM evidence is recorded: the VM script prints `RESULT|key|value` lines; the
PR body carries those lines verbatim plus the CI run link, and the orchestrator reads them before the GO
(the same artifact set #85 used).

## Risks

- G4 is the hardest piece (timers + liveness on a host that may lose its own path): design page first,
  VM-measured; it ships OFF by default behind a setting (decided), so a defect cannot bite an unaware host.
- G5 changes the wire shape: protocol v2 with v1 readable.
- G2 `sc privs` trimmed too far breaks netsh: measured 2026-10-16 (#99, five VM passes) — SeChangeNotify
  alone works; `sc sidtype restricted` does not (netsh gets access denied) and was dropped.
- G15 service-side install widens the service's job: plan page and orchestrator review first.
- Operator time: each rc needs the operator; keep rcs small and few.

## Open questions (operator) — answered 2026-10-16

Decided by the operator (direct, 07:15 Berlin, relayed by the orchestrator in m-5838):

1. IPv6: **not in 1.0**. 1.0 is documented IPv4-only with the dual-stack DNS caveat; IPv6 is a 1.x milestone.
2. Signing: **keep the self-signed pinned root**; no paid certificate; the SmartScreen note stays in the docs.
3. G4 commit-confirmed rollback: **behind a setting, OFF by default**; a policy may enable it.
4. 1.0 date: **none** — it ships when the M5 bar is met.

With these settled, v0.15.0 final = rc.1 unless the rc shows a defect, after a 48 h soak (earliest
2026-10-18, outside the n4 quiet hours).

## Review reconciliation

- Round 1 (nano): the GO + timing now stated; #90/#91 are hygiene, not security-model; rc.2 evidence
  pointed to; the tag is a main commit, branch state cannot affect it.
- Round 2 (nano): measured exit criteria; QoL and safety split.
- Round 3 (nano): "tag not at main head" is by design (final = verified rc; stated in Given state with
  the tree/CI re-run argument); verifier roles now named; "dirty files" rebutted with `git status` =
  0 tracked changes on this branch.
- Orchestrator (m-5784, GO-WITH-CHANGES): all eight points folded in: G13 reworded + G4 narrowed; a
  1.0 bar; M1 split into 0.15/0.16 with S/M/L; M0 docs-only PR; standard-user update path (G15),
  uninstall cleanup (G16), TELEMETRY.md link, G14 closed, runners explicit, G11a spike; G7 scoped with
  MSI reproducibility a non-goal and G10 an operator decision; open operator questions; rollback lines;
  audit page path next to the G-ids.
- Round 4 (nano): adopted one point — the Verification section now states how VM evidence is recorded
  and reviewed. The rest repeats rounds 1–3 ("dirty files" = untracked tooling scratch, 0 tracked
  changes; the tag identity and CI re-run are stated in Given state; #90/#91 do not alter a tag).
