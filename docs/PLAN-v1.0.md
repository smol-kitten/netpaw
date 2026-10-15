# Plan: NetPaw 0.15 → 1.0

For orchestrator review before any 0.15 code. Short sentences on purpose. Source: the operator's
2026-10-15 directive and the audit page `netpaw-audit-2026-10` (14 gaps G1–G14 as tasks).

## Goal

Ship v0.14.0 now, then a 0.15 QoL + cheap-safety release, then close the audit gaps on the way to a
1.0 that an enterprise can deploy, verify and roll back with confidence.

## Given state (measured 2026-10-15)

- v0.13.3 is the public stable. v0.14.0-rc.2 (main @2388025) is verified by CI, the VM and the operator.
  All v0.14 code gates are merged (#85 service + asInvoker tray, #86 artifacts, #87 appliesTo, #88 policy
  tiers, #89 user-mode UI). #89 is not in rc.2.
- Audit: the core is strong (typed intents, pipe DACL + Identification + SCM-PID, TOKEN_GROUPS tiers,
  floor + last-known-good, handle-based profiles.d gate, Event Log audit, signed releases). Gaps: G1–G14.
- secscan: 0 secrets. #90 (public-repo addressing) and #91 (two hardening nits) are open.

## Requirements (from the operator)

R1. Release 0.14 now. R2. Release a 0.15 with a QoL set. R3. A 1.0 release plan and feature set.
R4. Validate functionality and ensure safety at each step. R5. Keep the fleet rules: branch → PR → merge,
security PRs get the orchestrator's review, no `v*` tag without the orchestrator's OK, final = a verified rc.

## Options considered

- 0.14.0 from main head (includes #89): needs an rc.3 cycle. Rejected: delays R1 for a UI nicety.
- Fold the cheap safety items (G1–G3) into 0.14.0: changes the verified build. Rejected: final must equal
  the verified rc. They go to 0.15.
- One big 1.0 PR chain: rejected. Each gap is its own reviewable PR; 0.15/0.16/0.17 are cut as they land.

## Chosen approach

v0.14.0 = rc.2 re-tagged. Then small releases, each cut only from a verified rc, each with one theme.

## Steps (milestones and their feature sets)

### M0 — v0.14.0 (now)
- Tag v0.14.0 at 2388025 after the orchestrator's OK. Release = latest, signed assets, notes = rc.1+rc.2
  changes and known limits. Site stays on stable (it only promotes non-prerelease). Merge #90, #91.

### M1 — v0.15 "QoL + cheap safety" (target: the week after 0.14.0)
QoL (operator's ask):
- #89 user-mode UI (already merged) + QuickPanel greying in user mode.
- G12 t-e69af6: show the effective policy tier and WHY an intent was refused (tray status + CLI), with a
  link to the Event Log audit line.
- G13 t-73ace8: one-click "revert to previous state" from the last apply (journal-based), and a
  current-vs-profile diff before apply.
- t-dd3d27: classify only External vSwitch vEthernet as HostFacing (Default Switch / WSL are internal NAT).
- Profile validation on save: gateway inside the prefix, duplicate-IP warning (extends ProfileGuard).
Cheap safety (from the audit, all small, all testable):
- G1 t-414e12: netsh/ipconfig/powershell by full `%SystemRoot%\System32` path (SYSTEM PATH-hijack surface).
- G2 t-db6795: `sc sidtype restricted` + trimmed `sc privs` next to the existing `sc failure`.
- G3 t-45aec2: `PIPE_REJECT_REMOTE_CLIENTS` and FirstPipeInstance on every re-created instance.
- G8 t-8af0a5: SECURITY.md + threat-model page (the four documented decisions from the audit).
- G6 (docs part): state IPv4-only and the IPv6 DNS-leak caveat.
Exit: rc → operator verification → v0.15.0.

### M2 — v0.16 "safe to apply from anywhere"
- G4 t-cc99cb: commit-confirmed rollback. The service snapshots full pre-state, arms a timer and a
  liveness check (gateway/DNS); if the caller does not confirm, or liveness fails, the service reverts on
  its own. Needed because the UI may sit on the NIC being changed (RDP).
- G5 t-8ff0bb: target adapters by GUID/LUID + MAC in intents and profiles; alias becomes display-only.
- G9 t-e38207: policy schema version (fail closed on unknown) + RegNotifyChangeKeyValue + policy source in
  the audit line.

### M3 — v0.17 "supply chain + IPv6 decision"
- G7 t-1cb463: global.json, NuGet lock + locked restore, Deterministic/ContinuousIntegrationBuild, bin
  attestations (today attest:false), SBOM attestation, `gh attestation verify` in the release recipe.
- G14 t-3e2f95: fleet-actions pinning policy (fleet decision; apply whatever the fleet decides).
- G6 t-62e6d4: IPv6 support (addresses, DNS, routes) or a firm, documented v4-only scope for 1.0.
- G10 t-a9b04c: SmartScreen/private-PKI reality in docs + a public OV / Azure Trusted Signing path.

### M4 — v1.0 "deployable, verifiable, recoverable"
Feature set (what 1.0 promises):
- Everything above, plus: G11 t-d31af0 disposable-VM e2e harness (second management NIC) and security
  tests as code (pipe DACL assert, squat, filtered-token admin, remote pipe, oversized JSON fuzz, junction
  in profiles.d) in CI.
- Docs bar: install/uninstall (MSI + winget), admin guide (ADMX, Intune OMA-URI, policy table, audit log
  format), threat model, rollback/recovery runbook, README screenshots.
- Support policy: supported Windows builds, .NET 10 LTS dependency (to Nov 2028), pipe-protocol and
  policy-schema versioning with a deprecation window; the service rejects a mismatched client with a clear
  message.
- Explicit non-goals vs NetSetMan-class tools: no scripts/hostname/printer/drive mapping (privilege-sensitive);
  listed in the README.
Exit: a 1.0 rc verified on the VM harness + CI + the operator; then 1.0.0.

## Verification (per milestone)

- CI green incl. verify-windows (MSI install, service, pipe ACL/squat, standard-user via service, gates).
- Unit suite green; each gap PR adds its tests (G1 updates the FileName assertions; G2/G3 are VM-measured).
- A VM pass on each rc (M2+ on the e2e harness once G11 lands); the operator verifies each rc.
- Each security PR: the orchestrator's independent review before merge. Final tags only from a verified rc.

## Risks

- G4 rollback is the hardest piece (timers + liveness on a host that may lose its own path). Mitigation:
  design-first (plan page), VM-measured, behind a setting until 1.0.
- G5 GUID targeting changes the intent/profile wire shape: version the protocol (`"v": 2`), keep v1 readable.
- G2 `sc privs` can break netsh if trimmed too far: measure on the VM before shipping; keep SeChangeNotify.
- IPv6 (G6) may not fit 1.0: the plan allows a documented v4-only 1.0.
- Operator time: every rc needs the operator's verification; keep rcs small and few.
