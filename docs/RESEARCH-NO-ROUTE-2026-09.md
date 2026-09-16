# Spotting "the browser cannot reach it" on Windows — research (2026-09-16)

Question from the operator: can NetPaw notice that a browser (or any tool) is failing to reach an
address, and turn that into a profile recommendation ("you are trying 192.168.88.1 — profile
*MikroTik lab* covers it")? Everything below was measured on the Windows 11 test VM; scripts and
raw output are summarised, not paraphrased.

## 1. What Windows actually does with an unreachable address

| Situation | What the OS does | What the browser shows | Visible how long |
|---|---|---|---|
| Target off-link, a default gateway exists (the normal case) | sends SYN to the gateway; 3 retransmits (1 s, 2 s, 4 s, 8 s …), gives up after **~21 s** (`TimedOut`, 10060) | `ERR_CONNECTION_TIMED_OUT` after ~21 s | the socket sits in **SYN_SENT** for the whole 21 s |
| Target on-link, host silent | ARP has no reply, SYN never leaves; same retransmit schedule | `ERR_CONNECTION_TIMED_OUT` | SYN_SENT ~21 s, ARP cache holds an *incomplete* entry |
| No matching route at all (static profile without gateway, adapter without address) | `connect()` fails at once, `WSAEHOSTUNREACH`/`WSAENETUNREACH` | `ERR_ADDRESS_UNREACHABLE` at once | **never** in the TCP table — nothing to poll |
| Route to a black hole (measured: `198.51.100.0/24` via the loopback interface) | Windows still treats it as on-link: SYN_SENT + retransmits, `TimedOut` after 21 094 ms | `ERR_CONNECTION_TIMED_OUT` | SYN_SENT 21 s |

So "no route to host" in the browser sense is two different things: the slow one (SYN_SENT, 21 s,
observable) and the instant one (no route, not observable from outside the process).

## 2. Signals tested

### 2a. TCP connection table with owning PID — works, dependency-free
`Get-NetTCPConnection -State SynSent` (= IP Helper `GetExtendedTcpTable(TCP_TABLE_OWNER_PID_ALL)`)
listed both stuck targets with the owning PID on every one of 10 one-second samples:
```
SYN_SENT 192.168.88.1:80 pid=2720 samples=10
SYN_SENT 10.99.99.99:443 pid=2720 samples=10
```
One in-process call, no process spawn, no elevation, PID → process name via `Process.GetProcessById`.
A real handshake spends milliseconds in SYN_SENT, so "seen in two consecutive 2 s samples" is a clean
"nobody answers" filter. Combined with what NetPaw already has:
- `ArpTable` says whether an on-link target has an *incomplete* ARP entry (host down / wrong VLAN)
  or the traffic went to the gateway (off-link, nobody beyond the router answers);
- `ReachResolver.Resolve(target)` already says whether a saved profile or a device preset covers
  the address — that is the recommendation.

### 2b. `Microsoft-Windows-TCPIP/Diagnostic` analytic channel — works, but heavy and elevated
Enabling it through `System.Diagnostics.Eventing.Reader.EventLogConfiguration` works (elevated only;
note: `wevtutil sl` **hangs** in non-interactive sessions — use the .NET API). In a 60 s window it
recorded, per connection: 1002 *requested to connect* (local/remote), 1031 *connect proceeding*,
1186 *retransmitting connect attempt, RexmitCount = N*, 1332 *send event, State = SynSentState*, plus
1370 *RouteLookup … Status* for the route decision. The catalogue also defines 1021 *connect failed:
route lookup status* and 1034 *connect attempt failed with status* — the instant-failure events —
but neither appeared in the channel during the measured failures, and PIDs are only present in
1018/1033. Volume on an idle VM: ~1 500 events per 2 min. Verdict: usable for a 30 s on-demand
"why did that fail?" capture, not for continuous watching.

### 2c. `Microsoft-Windows-Winsock-AFD/Operational` — not usable
Events carry kernel process/endpoint pointers, no PID, no addresses, localised text
("Socketbereinigung: 1: Prozess 0xffffaf0a…"). Rejected.

### 2d. Not tested, rejected on paper
- **WFP audit** (Security events 5156/5157 via `auditpol … "Filtering Platform Connection"`): every
  connection on the machine lands in the Security log; heavy, policy-visible, and it does not say
  *why* a connect failed.
- **Browser side**: Chromium's `chrome://net-export` is manual; Edge/Chrome enterprise policies do not
  export network errors; history databases are locked while the browser runs.
- **ETW live session** (`Microsoft-Windows-TCPIP` provider with keywords): would expose 1021/1034 with
  PIDs, but needs a real-time ETW consumer (`TraceEvent` NuGet or ~400 lines of P/Invoke). Deferred.
- **DNS-Client/Operational channel**: shows the *names* a user typed (event 3006) — useful hint for
  the instant-failure case, but names rarely map to a profile; not needed for the first version.

## 3. Recommendation: the "intent watcher" (v0.11 candidate, ~1 day)

1. `Connectivity/IntentWatcher.cs` (Core): `ITcpTable` seam (`SynSentEndpoints()` → remote, port, pid)
   with an IP Helper implementation; poll every 2 s while checks are on; keep an endpoint that is
   stuck for ≥ 2 samples; drop loopback, link-local, multicast, NetPaw's own PID, and endpoints seen
   in the last 10 min.
2. Classify each stuck target: on-link + ARP incomplete → *"host does not answer (down or other
   VLAN)"*; off-link → *"no reply beyond the gateway"*; then `ReachResolver.Resolve(target)`:
   profile / preset / temp address.
3. Surface as an **Advice** row (Warning when a profile covers it, Info otherwise):
   *"msedge is waiting on 192.168.88.1:80 (no reply for 9 s) — profile 'MikroTik lab' covers it"*
   with *Apply* / *Reach* links; one balloon per target per 10 min; incident-log line.
4. Setting *Repair → Intent watcher* (on/off, default on: it reads a local table, nothing leaves the
   machine; verbose log only), policy `AllowIntentWatch`.
5. Tests with a fake table: handshake noise ignored, stuck target classified, cooldown, profile
   match, preset match, own PID ignored.
6. Later, optional: *"Why did that fail?"* button that enables `TCPIP/Diagnostic` for 30 s and lists
   the remotes with retransmits — covers the instant-failure case for elevated installs.

Limits to state in the help topic: the instant `ERR_ADDRESS_UNREACHABLE` (no default gateway at
all) is invisible to the watcher; NetPaw already flags that adapter state ("Static profile without
gateway"). Connections through a VPN or proxy show the tunnel/proxy address, not the site.
