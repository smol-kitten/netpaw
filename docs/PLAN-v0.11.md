# Plan: NetPaw v0.11 "intent watcher"

## Goal
When a program on the machine sits on an unanswered connection (SYN_SENT for two samples), NetPaw
names the program and the target, says why nobody answers (host silent on-link, or nobody beyond
the gateway) and offers the profile, preset or temporary address that covers it. Ships as v0.11.0,
no new dependencies, no elevation needed for the watcher.

## Given state
- `main` at v0.10.0 plus the research doc (`docs/RESEARCH-NO-ROUTE-2026-09.md`, PR #45). 201 Core tests.
- Measured on VM 150 (2026-09-16): `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)` lists an unanswered
  connect as SYN_SENT with its PID for the whole ~21 s retry window, unelevated, one in-process call.
  Instant failures (no matching route) never enter the table. The TCPIP/Diagnostic channel is
  elevated-only and heavy. Winsock-AFD is unusable.
- `ReachResolver.Resolve(target, adapter, profiles, presets)` already answers "which profile, preset
  or temporary address covers this IP" (`ReachKind.UseProfile / UsePreset / TempAddress / AlreadyReachable`).
- `IArpProvider.ReadCache()` returns `ArpEntry(Ip, Mac, Kind, …)`. an on-link target without a reply
  has no entry or an incomplete one.
- `Advisor.Analyze` returns `Advisory(Severity, Title, Text, CanRenew) { Repair, RepairAdapter, Adapter, Value, Route }`;
  `InfoToast` renders repair links per `RepairKind`. the Advice row respects `InfoCardSettings.ShowAdvice`.
- `RepairSettings` holds the per-feature switches (`DhcpAdvisory`, `VpnNotifications`, …). policy keys
  live in `Policy.cs` (15 keys), ADMX/ADML, `Set-NetPawPolicy.ps1`, `docs/ENTERPRISE.md`, CLI `policy`.
- The check loop (`TrayApp.RunCheckCore`) runs every 30–120 s. a 2 s watcher needs its own timer.
- Balloons: `Notify(title, text, icon, force)`. incidents: `IncidentLog.Append(new Incident(...))`.
- No standing rules for scope `netpaw`. VM 150 sleeps on disk.

## Requirements
- R1 A connection in SYN_SENT on two consecutive 2 s samples counts as *stuck*. A handshake that completes within 2 s never shows.
- R2 Ignore: loopback, link-local, multicast/broadcast, NetPaw's own PID, and a target that NetPaw reported in the last 10 minutes.
- R3 Classify: target inside a directly connected subnet and no ARP reply → *host silent (down or
  another VLAN)*. else *no reply beyond the gateway*. then `ReachResolver.Resolve(target)`.
- R4 Surface as an Advice row: Warning when a profile or preset covers the target, Info otherwise;
  text names the process, target, port, seconds waited and the covering profile. links *Apply
  profile* / *Reach* (`RepairKind.Reach` with the target). One balloon per target per 10 min.
  Incident-log line `intent`.
- R5 Off switch: *Settings → Repair → Intent watcher* (default on), policy `AllowIntentWatch`
  (0 hides the setting and stops the watcher). Nothing leaves the machine. the log line is verbose level.
- R6 CLI `netpaw-cli stuck` prints the current stuck targets with the classification and the
  recommendation (one sample of 4 s).
- R7 Must not: run when checks are off?: no: the watcher is independent of probes (it sends nothing);
  it runs whenever the setting is on. Must not spawn a process per sample. Must not act by itself
  (no automatic apply). Must not read the table more often than every 2 s.

## Design details
**Table seam.** `Net/TcpTable.cs`: `record TcpEndpoint(string Remote, int Port, int Pid, string Local)`;
`interface ITcpTable { IReadOnlyList<TcpEndpoint> SynSent(); }`. `IpHelperTcpTable` (Windows) via
`GetExtendedTcpTable` P/Invoke into `MIB_TCPTABLE_OWNER_PID`, filtered to state `MIB_TCP_STATE_SYN_SENT (3)`;
`NullTcpTable` elsewhere. Process name through `Process.GetProcessById(pid).ProcessName` with a
try/catch (the process may be gone).

**Watcher.** `Connectivity/IntentWatcher.cs` (pure, testable):
`Sample(IReadOnlyList<TcpEndpoint> now, DateTimeOffset at)` keeps a dictionary keyed by
`remote:port:pid` with first-seen time. returns the endpoints seen in the previous sample too (stuck).
`Classify(target, adapter, arpCache)` → `StuckKind.OnLinkSilent | OffLinkNoReply`.
`Recommend(target, adapter, profiles, presets)` → `ReachDecision`. `Cooldown` per `remote` = 10 min.
Output: `StuckTarget(Remote, Port, Pid, Process, Kind, Decision, Since)`.

**Advisory.** `Advisor.FromStuck(StuckTarget)` → Title `"{process} cannot reach {remote}:{port}"`,
Text `"No reply for {n} s ({kind text}). Profile '{name}' covers it."` or `"… Preset {vendor model}
covers it."` or `"… Nothing saved covers it. 'reach {remote}' adds a temporary address."`
`Repair = RepairKind.Reach`, `Target = remote`. `InfoToast` renders *Apply 'name'* / *Reach {remote}*
through `TrayApp.Reach(target)` (existing).

**Loop.** `TrayApp`: `_intentTimer` (2 s, WinForms timer) → `Task.Run(table.SynSent)` → `Sample` →
new stuck targets → `_intentAdvisories` (merged into the Advice row after `_advisories`), balloon,
incident, verbose log. Starts/stops with the setting and the policy.

## Options considered
- A. Fold the watcher into the check round: rejected: the round runs every 30–120 s. the retry
  window is 21 s.
- B. ETW live session for the instant-failure case: rejected for v0.11: needs a consumer library
  or heavy P/Invoke. the research shows the slow case is the common one.
- C. Parse `netstat -ano`: rejected: a process spawn every 2 s, locale-dependent output. IP Helper
  is one call.
- D. Chosen: IP Helper table every 2 s, pure classifier, existing reach resolver, Advice row.

## Chosen approach
Reuse the reach resolver for the recommendation and the ARP cache for the reason. add only the
table seam and a small pure watcher, so every rule has a Linux test with a fake table.

## Steps
1. `src/NetPaw.Core/Net/TcpTable.cs`: `TcpEndpoint`, `ITcpTable`, `IpHelperTcpTable` (P/Invoke
   `iphlpapi!GetExtendedTcpTable`, `TCP_TABLE_OWNER_PID_ALL`, AF_INET), `NullTcpTable`.
2. `src/NetPaw.Core/Connectivity/IntentWatcher.cs`: `Sample`, `Classify`, `Recommend`, cooldown,
   `StuckTarget`, `StuckKind`, `Filter` (loopback/link-local/multicast/own PID).
3. `src/NetPaw.Core/Connectivity/Advisor.cs`: `RepairKind.Reach`, `Advisory.Target`, `FromStuck`.
4. `src/NetPaw.Core/Model/Settings.cs`: `RepairSettings.IntentWatch = true`.
5. Policy: `AllowIntentWatch` in `Policy.cs`, `Capability.IntentWatch`, `NetPawService.Allowed`,
   `deploy/gpo/NetPaw.admx`, `en-US/NetPaw.adml`, `deploy/intune/Set-NetPawPolicy.ps1`,
   `docs/ENTERPRISE.md`, CLI `policy` printer.
6. `src/NetPaw.Tray/TrayApp.cs`: `_intentTimer`, `IntentAdvisories`, balloon + incident + verbose
   log, start/stop on settings save. `InfoToast.cs`: merge `IntentAdvisories` into the Advice
   row, render `RepairKind.Reach` links. `SettingsForm.cs`: Repair tab checkbox.
7. `src/NetPaw.Cli/Program.cs`: `stuck`: two samples 2 s apart, print classification + recommendation.
8. Help: `troubleshooting.md` ("Edge says it cannot reach …"), `settings.md`, `cli.md`,
   `enterprise.md`. README table row + feature bullet.
9. Tests (`IntentWatcherTests.cs`): `HandshakeNoiseIsNotStuck`, `StuckAfterTwoSamples`,
   `IgnoresLoopbackLinkLocalMulticastAndOwnPid`, `CooldownSuppressesRepeats`,
   `ClassifiesOnLinkSilentViaArp`, `ClassifiesOffLinkNoReply`, `RecommendsProfilePresetOrTemp`,
   `AdvisoryTextNamesProcessAndProfile`, `PolicyDeniesIntentWatch`.
10. Version 0.11.0. PRs: `feat/intent-watcher` (1–6, 9), `feat/intent-cli-docs` (7–8, 10).
11. VM 150: resume, deploy, open `http://192.168.88.1/` in Edge with the profile *MikroTik lab*
    saved → Advice row within 4 s, balloon once, `netpaw-cli stuck` shows the same. screenshot.

## Verification
- `dotnet test` ≥ 210 tests, 0 failed.
- VM: Advice row `msedge cannot reach 192.168.88.1:80: no reply for 4 s (no reply beyond the
  gateway). Profile 'MikroTik lab' covers it.` with *Apply* / *Reach* links. second Edge tab to the
  same address within 10 min → no second balloon. `netpaw-cli stuck` lists it with pid and process.
- `AllowIntentWatch=0` → no timer, setting greyed, CLI `stuck` exits 4.
- Verbose log shows one `intent:` line per new stuck target and no per-sample noise.

## Risks
- Process name lookup fails for protected processes → show `pid 1234`.
- Large tables (thousands of connections) → the call stays one buffer. filter to SYN_SENT before
  allocating records.
- A VPN or proxy makes every target look like the tunnel address → the classifier reports the
  tunnel. the help topic says so.
- The advice row fills with many stuck targets on a dead network → cap at 3 rows, oldest first,
  and suppress the watcher entirely while the adapter has no address (the existing advisories
  already say why).
