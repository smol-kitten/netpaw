# NetPaw: 50 feature candidates, scrutinized to 10 (2026-10-27)

## Goal
List 50 candidate features, score each against fixed criteria, and keep about 10 for the 1.x roadmap.

## Given state (measured 2026-10-27)
- Product: Windows tray + CLI + LocalSystem service over a named pipe. IPv4 profiles, reach mode, DHCP,
  routes, VLAN, presets, scan, ARP/pktmon diagnostics, managed profiles, policy tiers, signed MSI,
  winget facts, auto-update with pinned root.
- Decided out of 1.0: IPv6 (1.x milestone t-33f24d), paid certificate, G4 default-on.
- Users: the operator and the fleet. no external user base measured. Threat model: local admin trusted,
  standard user limited by tiers, service is the only writer.
- 1.0 bar (PLAN-v1.0 M5) is not met yet. features compete with it for the same person-hours.

## Requirements
- Every candidate gets the same five criteria and a one-line verdict.
- Keep ≤ 12. Each kept item names its measured need and its cost (S/M/L).
- Nothing kept may widen the service's job without an orchestrator security review.

## Scoring criteria (0–2 each, max 10)
N need (measured demand or a filed task) · S safety (does not widen the attack surface) ·
C cost (2 = S, 1 = M, 0 = L) · F fit (the service/intent model, IPv4-only 1.0) · V verifiable (harness or
unit test exists or is cheap). Keep ≥ 8, or ≥ 7 with N = 2.

## The 50 candidates
| # | Candidate | N | S | C | F | V | Σ | Verdict |
|---|---|---|---|---|---|---|---|---|
| 1 | Profile diff view before apply (t-73ace8 half) | 2 | 2 | 2 | 2 | 2 | 10 | KEEP |
| 2 | Revert history (last N snapshots, not one) | 1 | 2 | 1 | 2 | 2 | 8 | KEEP |
| 3 | Commit-confirmed rollback (G4, off by default) | 2 | 1 | 0 | 2 | 1 | 6 | 1.0 item, not a 1.x feature |
| 4 | Adapter identity by GUID (G5) | 2 | 2 | 0 | 2 | 1 | 7 | 1.0 item |
| 5 | IPv6 address/DNS/routes/reach | 1 | 1 | 0 | 0 | 1 | 3 | 1.x milestone on its own (t-33f24d) |
| 6 | Wi-Fi profile switching (netsh wlan) | 1 | 1 | 1 | 1 | 1 | 5 | drop: driver-specific, no demand |
| 7 | Per-profile firewall rules | 0 | 0 | 0 | 1 | 1 | 2 | drop: widens the service |
| 8 | Multi-VLAN trunk NICs | 0 | 1 | 0 | 1 | 0 | 2 | drop |
| 9 | Profile import/export as a signed pack (exists for repos) for users | 1 | 2 | 2 | 2 | 2 | 9 | KEEP |
| 10 | QR/URL share of a profile | 0 | 1 | 2 | 1 | 2 | 6 | drop |
| 11 | "Why can't I" panel: tier + policy + audit link in one view (G12 extension) | 2 | 2 | 2 | 2 | 2 | 10 | KEEP |
| 12 | Scheduled profile switch (time-based) | 0 | 1 | 1 | 1 | 1 | 4 | drop |
| 13 | Auto-switch by fingerprint confidence meter in UI | 1 | 2 | 2 | 2 | 1 | 8 | KEEP |
| 14 | Reach mode: multi-target (several subnets at once) | 1 | 1 | 1 | 2 | 2 | 7 | hold |
| 15 | Temp-address TTL (auto clear after N min) | 1 | 2 | 2 | 2 | 2 | 9 | KEEP |
| 16 | DHCP lease viewer (server, T1/T2, options) | 1 | 2 | 2 | 2 | 1 | 8 | KEEP |
| 17 | LLDP/CDP neighbour on the card (pktmon exists) | 1 | 2 | 1 | 2 | 1 | 7 | hold |
| 18 | MTU path discovery test | 0 | 2 | 1 | 2 | 1 | 6 | drop |
| 19 | Captive-portal detection | 0 | 1 | 1 | 1 | 0 | 3 | drop |
| 20 | DNS-over-HTTPS toggle | 0 | 1 | 1 | 1 | 1 | 4 | drop: policy-owned in Windows |
| 21 | Proxy settings per profile | 1 | 0 | 1 | 0 | 1 | 3 | drop: user-hive, not the service's job |
| 22 | Hosts-file entries per profile | 1 | 0 | 2 | 1 | 2 | 6 | drop: widens writes |
| 23 | Static ARP entries per profile | 0 | 1 | 2 | 2 | 2 | 7 | hold |
| 24 | Wake-on-LAN sender (exists) → scheduled WoL | 0 | 2 | 2 | 1 | 2 | 7 | hold |
| 25 | Port-check presets (exists) → service health sets | 0 | 2 | 2 | 1 | 2 | 7 | hold |
| 26 | Ping/latency sparkline on the card | 1 | 2 | 2 | 1 | 1 | 7 | hold |
| 27 | Incident timeline export (exists) → shareable HTML | 0 | 2 | 1 | 1 | 1 | 5 | drop |
| 28 | Event Log view inside the tray (audit lines) | 2 | 2 | 2 | 2 | 1 | 9 | KEEP (merge with 11) |
| 29 | Managed-profile editor for admins (GUI) | 1 | 1 | 0 | 2 | 1 | 5 | drop: Intune/GPO is the editor |
| 30 | ADMX for every policy tier (partial exists) | 2 | 2 | 1 | 2 | 2 | 9 | KEEP |
| 31 | Intune OMA-URI samples generated from the policy table | 1 | 2 | 2 | 2 | 2 | 9 | KEEP |
| 32 | Pipe protocol v2 with capability negotiation | 1 | 2 | 0 | 2 | 1 | 6 | with G5, not alone |
| 33 | PowerShell module wrapping the CLI | 1 | 2 | 1 | 2 | 1 | 7 | hold |
| 34 | REST/localhost API | 0 | 0 | 0 | 0 | 1 | 1 | drop: new attack surface |
| 35 | Remote management of other hosts | 0 | 0 | 0 | 0 | 0 | 0 | drop |
| 36 | Telemetry opt-in dashboard link (exists) → per-host health card | 0 | 1 | 1 | 1 | 1 | 4 | drop |
| 37 | Dark/light theme follow system | 1 | 2 | 2 | 1 | 1 | 7 | hold |
| 38 | Localization (de-DE first) | 1 | 2 | 1 | 1 | 1 | 6 | hold |
| 39 | Accessibility pass (screen reader names) | 1 | 2 | 1 | 1 | 1 | 6 | hold |
| 40 | Portable mode without service (exists, elevated) → explicit "portable" badge + docs | 1 | 2 | 2 | 2 | 1 | 8 | KEEP |
| 41 | CLI JSON output (`--json`) for every command | 2 | 2 | 1 | 2 | 2 | 9 | KEEP |
| 42 | CLI shell completion | 0 | 2 | 2 | 1 | 1 | 6 | drop |
| 43 | Profile templates from a scan result (one click) | 1 | 2 | 2 | 2 | 1 | 8 | hold (near 13) |
| 44 | Bonjour/mDNS discovery in scan | 0 | 1 | 1 | 1 | 1 | 4 | drop |
| 45 | SNMP sysName on scan rows | 0 | 1 | 1 | 1 | 1 | 4 | drop |
| 46 | "Network changed" toast with diff | 1 | 2 | 2 | 2 | 1 | 8 | hold (near 11) |
| 47 | Service self-test command (`netpaw-cli service selftest`) | 2 | 2 | 2 | 2 | 2 | 10 | KEEP |
| 48 | Crash-free rate in telemetry (exists) → release gate | 1 | 2 | 1 | 1 | 1 | 6 | drop as feature; CI item |
| 49 | Winget publish automation (facts exist) | 2 | 2 | 1 | 2 | 2 | 9 | KEEP |
| 50 | Offline docs in the help window from `docs/` | 1 | 2 | 2 | 1 | 1 | 7 | hold |

## Scrutiny result: the 10 kept
1. #1 Profile diff before apply (S). Need: t-73ace8 half left open.
2. #11 + #28 "Why can't I" view with the audit lines (M). Need: G12 shipped the data. no view yet.
3. #47 `service selftest` (S): pipe, token, folder ACL, policy source, last apply, in one report.
4. #15 Temp-address TTL (S). Need: temp addresses are the one state that outlives a session.
5. #9 Signed user profile packs (S): reuses PackSigner. makes profiles portable without a repo.
6. #41 CLI `--json` everywhere (M): the harness and scripts parse output today by regex.
7. #49 Winget publish automation (M): facts file exists. the manual step is the gap.
8. #30 + #31 ADMX for every tier + generated OMA-URI samples (M): the policy table is the source.
9. #16 DHCP lease viewer (S): the data is already read for `DhcpServers`.
10. #13 Fingerprint confidence meter (S): auto-switch exists. its decision is invisible.
11. #40 Portable badge + docs (S). kept as the eleventh because it is a docs item.
12. #2 Revert history (M). kept as the twelfth, behind G4's design.

Held (score 7, revisit after 1.0): 14, 17, 23–26, 33, 37, 43, 46, 50. Dropped: the rest, reason in the table.

## Operator decision (2026-10-27, q-8c78, relayed in m-8197)

**KEEP ALL 12** (the 10 kept and both extras). Sequenced after the 1.0 bar. #12 revert history waits
for G4's design page. Tasks filed, in list order: t-bf17f9, t-d4f175, t-74844a, t-63c9ba, t-ad7c18, t-0adb7a, t-de5d06, t-45c383, t-03c582, t-89db54, t-88bd68, t-82c880.

## Options considered
- A. Keep by score alone: rejected, it kept two 1.0 items (G4, G5) that are not 1.x features.
- B. Score, then remove 1.0 items and merge near-duplicates: chosen.
- C. Operator vote on all 50: rejected, 50 questions is not a decision round.

## Chosen approach
Score first, remove what the 1.0 plan already owns, merge duplicates, keep the top band. The kept
list goes to the operator as one decision (keep/trim), not fifty.

## Steps
1. Orchestrator review of the kept list. operator decision in the next round.
2. File one task per kept item with the score line. sequence after the 1.0 bar.
3. Re-score the held band after v1.0.0.

## Verification
- Each kept item has a task id and a cost. the held and dropped items have a reason in the table.

## Risks
- Scores are one person's estimate: the review round and the operator decision are the correction.
- The 1.0 bar slips if features start first: steps are sequenced after it.

Further rounds stop here
