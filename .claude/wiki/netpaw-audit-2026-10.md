---
title: 'NetPaw v0.14 audit: best practices vs current system'
scope: netpaw
tags:
- netpaw
- audit
- security
- v0.14
- release
summary: 'Source-backed audit of the v0.14 service architecture: what is verified
  strong, 16 gaps as tasks (0.15 cheap safety + QoL; 1.0 rollback/GUID targeting/IPv6/supply
  chain), and the threat-model decisions to document.'
related: []
web: []
---

# NetPaw v0.14 audit — best practices vs the current system (2026-10-15)

Scope: the privileged-service architecture that shipped in v0.14 (service + asInvoker tray/CLI + policy
tiers), audited against a source-backed checklist (named-pipe helper services, Windows network-config
safety, signing/attestation, ADMX/Intune, least-privilege desktop apps, 1.0 QA bar). Method: research
checklist → code facts (grep + the merged PRs #85/#87/#88/#89) → secscan (local + CI) → gaps as tasks.
Release state at audit time: v0.13.3 stable (public default), v0.14.0-rc.2 verified prerelease
(main @2388025), all v0.14 code gates merged.

## Verified strong (keep)

| Practice | NetPaw | Evidence |
|---|---|---|
| Requests are typed intents; the service re-plans from validated values, never a command line | Yes | `IntentHandler`, `ProfileGuard`, `IsSafeAdapterName`; injection test in verify-windows |
| Explicit pipe DACL, no Everyone/anonymous; NETWORK denied | Yes | `PipeServer.Acl()` (SYSTEM full; Admins/NCO/INTERACTIVE RW; NETWORK deny); VM-measured |
| Client authenticates the server (SCM PID) and connects at Identification level | Yes | `ServerCheck.Verify`; `TokenImpersonationLevel.Identification` |
| Caller tier from TOKEN_GROUPS, enabled-and-not-deny-only; filtered admin = StandardUser (deliberate) | Yes | `CallerPrivilege.TierOf`; 8 unit tests; #76 defect fixed |
| Per-request authorization with floor table, last-known-good (availability), audit per decision | Yes | `PolicyTiers`; golden + floor + LKG tests; audit to Windows Event Log (`Program.cs`) |
| DoS bounds: 64 KB line cap, 10 s read timeout, 8 concurrent, single apply lock | Yes | `ServiceProtocol.MaxLine`, `ReadTimeout`, `MaxConcurrent`, `_gate` |
| Anti-squat: FirstPipeInstance on first create; DACL withholds CreateNewInstance | Yes (partial, see G3) | `PipeServer.Run` |
| profiles.d gate: folder not a reparse point + SYSTEM/Admins owner; file owner checked on the OPEN handle | Yes | `MachineStore` (#85 MUST-2); VM-measured |
| ProgramData tree with explicit DACL (SY/BA full, BU read); perMachine MSI; asInvoker tray + CLI | Yes | `Package.wxs` PermissionEx, `Scope=perMachine`; both manifests |
| Autostart = HKCU Run, opt-in, no elevation; old elevated task removal is honest (reports failure) | Yes | `Autorun.MigrateFromScheduledTask` (#85 MUST-1) |
| Signed MSI/zips (Authenticode + RFC 3161), pkg attestations, verify-signed in CI, winget hash from the signed MSI | Yes | ci.yml sign-bin/sign-pkg strict on tags; `verify-signed`; release job |
| No DLL-dir manipulation; no plugin load from ProgramData | Yes | grep: none |
| Telemetry is a separate opt-in build; default binary carries no telemetry strings (CI-asserted) | Yes | ci.yml "no telemetry strings" check |
| Public repo carries no internal addressing (after #90) | Yes | RFC 5737 / 02:00:00 everywhere |

## Gaps → tasks (netpaw scope)

| Id | Task | Severity | Target |
|---|---|---|---|
| G1 | t-414e12 netsh/ipconfig/powershell by bare name from a SYSTEM process → full `System32` paths | safety, cheap | 0.15 |
| G2 | t-db6795 `sc sidtype restricted` + `sc privs` next to the existing `sc failure` | safety, cheap | 0.15 |
| G3 | t-45aec2 `PIPE_REJECT_REMOTE_CLIENTS` + FirstPipeInstance on every re-created instance | safety, belt-and-suspenders | 0.15 |
| G8 | t-8af0a5 SECURITY.md + threat-model page (documented decisions below) | docs | 0.15 |
| G12 | t-e69af6 show effective tier + why refused (tray/CLI) | QoL | 0.15 |
| G13 | t-73ace8 one-click revert from the inflight journal; current-vs-profile diff | QoL | 0.15 |
| G4 | t-cc99cb commit-confirmed rollback (service-side timer/liveness; the UI may sit on the NIC being changed) | safety, feature | 1.0 |
| G5 | t-8ff0bb target adapters by GUID/LUID + MAC, alias display-only (alias is NCO-renameable) | safety | 1.0 |
| G6 | t-62e6d4 IPv6 (document v4-only + v6 DNS-leak caveat in 0.15; support in 1.0) | correctness | 1.0 |
| G7 | t-1cb463 reproducible builds: global.json, NuGet lock, Deterministic/CIB, bin attestations, SBOM, `gh attestation verify` recipe | supply chain | 1.0 |
| G9 | t-e38207 policy schema version + RegNotifyChangeKeyValue + policy source in audit line | robustness | 1.0 |
| G10 | t-a9b04c private-PKI reality (SmartScreen) docs + public signing path | distribution | 1.0 |
| G11 | t-d31af0 disposable-VM e2e harness (second mgmt NIC) + security tests as code | QA | 1.0 |
| G14 | t-3e2f95 fleet-actions@v2.1.8 by tag not SHA (8×) — CLOSED: first-party tags are immutable by ruleset, exact-tag pinning accepted; SHA only for third-party | supply chain | done |
| G15 | t-dded4a standard-user update path: service-side verified install vs "updates need an admin" (plan page first) | safety, feature | 0.17 |
| G16 | t-2d66f5 uninstall cleanup: document what stays (profiles.d, service state); clean the update cache | hygiene | 0.17 |
| G11a | t-f7c79c disposable-VM harness research spike (n4 driving Proxmox), own plan page before code | QA | 1.0 |

Also from the research, decided and to DOCUMENT (threat model, G8), not change:
- A UAC-filtered admin (deny-only Administrators SID) is a StandardUser. Stricter than MSRC's boundary, same
  as netsh/Set-NetIPInterface. Keep.
- INTERACTIVE (incl. RDP / fast-user-switching sessions) may apply StandardUser-tier intents. This is the
  product's purpose; the audit line records the session. A policy can tighten every tier. Keep, document.
- The NetworkConfigOperators tier is broader than the OS NCO group (it allows routes/metrics/reset). It is a
  deliberate delegation through the service, not an OS-group claim. Document; policy can raise it to Admin.
- The last-known-good cache is an AVAILABILITY control: a readable policy always wins; the cache applies only
  when the live policy is unreadable/invalid, and never below the floor. Keep.

## Validation evidence at audit time
- CI (real Windows): MSI install, service up, pipe ACL + squat test, standard-user reach via service,
  managed profile by id, DeleteRoute privilege gate, elevated-admin gate (#76 regression), injection refused,
  user-owned planted file ignored, service recovery — all green on the rc.2 tag run.
- VM150 (real Win11, 2026-10-12): asInvoker manifest, service runs, managed profile through the folder+handle
  gate, NETWORK logon denied by the pipe, real static apply on a secondary NIC + revert, HKCU autostart.
- Unit: 322 Core tests green (policy tiers golden/floor/LKG, appliesTo, autorun honesty, change channel).
- secscan: 0 secrets; 2 real items fixed (#91); the rest is the fleet pinning policy (G14).

## Reading order for 0.15 / 1.0
0.15 (QoL + cheap safety): G1, G2, G3, G8, G12, G13, plus #89 already merged, t-dd3d27 (External vSwitch as
HostFacing), QuickPanel greying. 1.0: G4, G5, G6, G7, G9, G10, G11 and the docs bar (install/admin/threat
model/rollback runbook). See the 1.0 plan page.
