# G4 design: commit-confirmed apply (service-side timer + liveness)

Design page for PLAN-improvements-25 step 19. Orchestrator review before any code (steps 20a–20c).
Operator decision 2026-10-16: ships **behind a setting, OFF by default**; a policy may enable it.

## Goal
An apply that cuts the caller off is undone by the service on its own, within a known time, without
the caller's help.

## Given state (measured on main, 2026-11-03)
- Every adapter-bound apply runs through `IntentHandler.Journaled`: it writes `last-apply.json` (the
  pre-state: DHCP on/off, IPv4 addresses, first gateway, DNS) before the first command, then
  `inflight.json` for the duration. `Interrupted()` reports a leftover `inflight.json` at service start.
- `RevertLastApply` (0.16) re-applies that snapshot to the same adapter, MAC-checked, with the
  virtual-adapter and user-profile gates. It is a request; nobody calls it when the caller is gone.
- `IntentHandler` has an injectable `Clock` and a per-caller `ListenCooldown` (0.16): timing logic is
  already testable without real timers.
- The service token holds `SeChangeNotifyPrivilege` only; `netsh` works with it (VM-measured, #99).
- `ApplyAndVerify` already waits `settleMs` and compares the adapter against the profile.
- There is no timer, no probe, and no persisted "pending" state in the service today.
- The tray shows a status line from `LastApplySummary()`; the CLI has `revert`.

## Requirements
- R1. OFF by default. Nothing changes for a host that never turns it on.
- R2. The decision to revert is made in the service, from service-side evidence. A client that
  disappears cannot block it.
- R3. The window is bounded: the service reverts at the deadline unless confirmed, and earlier when
  liveness is lost. Deadline and probes come from one clock.
- R4. A service restart inside the window still reverts (the pending state is on disk).
- R5. A profile that intentionally has no gateway or no DNS is not reverted for lacking them.
- R6. A manual revert, a confirm, or a new apply on the same adapter ends the window exactly once.
- R7. No new privilege. Probes use what the service can already do (ARP via `arp`/ICMP via `ping`
  from `System32`, a UDP DNS query from managed code). No raw sockets.
- R8. Audit: arm, confirm, revert-by-timeout and revert-by-liveness are four distinct log lines.

## Options considered
- A. Client-side timer (tray counts down and sends Revert). Rejected: the client is the thing that
  gets cut off (RDP session, remote tray) and a crashed tray leaves the host broken.
- B. Service timer only, no liveness. Simple and bounded, but a 60 s window on a cut-off host is 60 s
  of no path; and a short window makes a slow DHCP lease look like a failure.
- C. Service timer + liveness as an early-out, probing only what the profile defines (chosen). The
  timer is the guarantee; liveness shortens the outage on a clear loss.
- D. Liveness only, no deadline. Rejected: a profile that leaves the host reachable but wrong (a lab
  network) would never revert; and a flapping probe would revert good applies.
- E. Rollback on `ApplyAndVerify` diff. Already exists as a report; it says the adapter is not what the
  profile asked for, not that the path is lost. Kept as is, not a trigger.

## Chosen approach
Option C. The request carries the window; the service arms a deadline on its clock, persists it next
to the snapshot, probes gateway and DNS of the applied profile every few seconds, and reverts through
the existing `RevertLastApply` path on deadline or on sustained probe loss. A confirm intent clears
it. Everything is driven by `Clock` and a `Tick()` so the logic is unit-tested with a fake clock;
the real timer only calls `Tick()`.

## Wire, setting, policy
- `ServiceRequest.ConfirmSeconds` (int?, protocol v2 field, see G5 page): 0 or null = off,
  5..600 accepted, else refused. Applies to ApplyProfile, Dhcp, Reach and RevertLastApply (a revert
  can itself be confirmed).
- New `RequestKind.ConfirmApply`: clears the pending window on the named adapter (identity per G5).
  Tier: the same tier the original apply needed (the policy table gets `ConfirmApply` = StandardUser,
  raisable). Any caller of that tier may confirm; the audit line names both callers.
- User setting `CommitConfirmSeconds` (tray Settings, CLI `--confirm N`), default 0 (R1).
- Policy `CommitConfirmMinSeconds` (HKLM, DWORD): when set, the service treats every eligible apply as
  if the client had asked for at least that window. The tray reads it to show the countdown. This is
  the "a policy may enable it" path.

## Service behaviour
- Arm: after `Journaled` succeeds with `ConfirmSeconds > 0`, write `confirm-pending.json`
  (adapter id + MAC, deadline, profile gateway + DNS, caller, armed-at) next to `last-apply.json`,
  log "armed commit-confirm: N s on {adapter} for {caller}".
- Tick (every 2 s, from the service timer; in tests from the fake clock): if no pending → return. If
  `now ≥ deadline` → revert, reason `timeout`. Else probe (R5): gateway, if the profile has one, by
  `ping -n 1 -w 1000 <gw>` from System32 (ARP-resolves on the link; ICMP may be filtered, so a
  `arp -a` hit for the gateway counts as alive too); DNS, if the profile has servers, by one UDP query
  for `.` to the first server with a 1 s timeout from managed code. Three consecutive ticks with every
  defined probe failed → revert, reason `liveness`. A profile with neither gateway nor DNS → timer only.
- Probes start after `settleMs` (the same settle `ApplyAndVerify` uses), so a lease or a link
  renegotiation is not counted as loss.
- Revert: the existing `RevertLastApply` body with caller `commit-confirm (timeout|liveness)` and the
  tier gates evaluated as **Admin** (the service acts for itself; the original apply already passed its
  gate). Delete `confirm-pending.json` first, then revert (R6: a revert that itself fails is logged,
  never re-armed).
- Confirm: `ConfirmApply` → delete `confirm-pending.json`, log "confirmed by {caller}".
- A new apply on the same adapter cancels the pending window before it snapshots (the snapshot is the
  new pre-state). A manual `RevertLastApply` cancels it too. A pending window on another adapter is
  untouched (one pending file per adapter id).
- Start-up: `Interrupted()` also reads `confirm-pending.json`; if the deadline passed or the probes
  fail now → revert at start (R4), else re-arm with the remaining time.
- Status: `ServiceStatus` gains `PendingConfirm` (adapter alias, seconds left) so the tray shows a
  countdown with a *Keep* button and the CLI prints it.

## Steps (each one PR; orchestrator review on 2)
1. This page (review). Answer the open questions below.
2. 20a Service: `ConfirmSeconds`, `ConfirmApply`, `confirm-pending.json`, `Tick()`, probes behind
   `ILiveness` (fake in tests), start-up handling, policy `CommitConfirmMinSeconds`, audit lines, status
   field. Tests: deadline revert, liveness revert after 3 failed ticks, confirm clears, new apply replaces,
   manual revert clears, restart inside window reverts, no-gateway profile is timer-only, out-of-range
   window refused, policy minimum raises a 0 request.
3. Tray + CLI: setting, `--confirm`, countdown + *Keep*, `netpaw-cli confirm`.
4. 20b Harness: apply a profile on the test NIC with `--confirm 30` whose gateway does not exist →
   `RESULT|g4-liveness|reverted in N s` (N < 30, test NIC back on DHCP). 20c: apply a good profile with
   `--confirm 30`, run `confirm` → still applied after 40 s. Plus: apply, kill the tray, no confirm →
   reverted at 30 s. Management NIC untouched in all three (the guard in `vm-verify.ps1`).
5. Docs: `policy.md` (two new rows), `threat-model.md` (scope-limits bullet replaced), `features.md`.

## Verification
- Unit (fake clock, fake liveness): the nine cases in step 2. Existing 396 stay green.
- Harness: the three `RESULT|g4-*` lines above, pasted into the PR.
- Exit (PLAN-v1.0 M3): "a simulated connectivity loss reverts within the timer on the VM".

## Risks
- False liveness loss on a network that filters ICMP and has no DNS: the gateway probe accepts an ARP
  hit, and a profile without DNS skips the DNS probe; worst case is a revert of a working apply at the
  deadline minus a few seconds, which the user re-applies with a longer window. Logged as `liveness`.
- The host loses its own path to the service? The pipe is local; the service keeps running. The one
  case that beats it is a service crash inside the window: `confirm-pending.json` covers it at restart.
- Timer in a `ServiceBase`: `System.Threading.Timer` on the thread pool; `Tick()` is re-entrant-safe
  (a lock, skip if running). The journal and the snapshot are written by the same handler, same lock.
- A policy minimum surprises a user applying from the console: the tray shows the countdown and the
  reason ("policy"), and `Keep` is one click.

## Open questions (orchestrator)
1. Should `ConfirmApply` require the **same caller** (user SID) as the apply, or the same tier as
   written here? Same tier is simpler and lets an admin keep a colleague's apply; same SID is stricter.
2. Liveness probes from the service: `ping`/`arp` from System32 (consistent with G1, no new code) or a
   managed ICMP (`System.Net.NetworkInformation.Ping`, raw-socket free on Windows)? The page assumes
   the System32 tools; managed `Ping` is the fallback if the VM shows the tool call is too slow for a 2 s
   tick.
