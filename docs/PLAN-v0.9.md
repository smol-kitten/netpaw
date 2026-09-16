# Plan: NetPaw v0.9 "diagnose"

## Goal
An admin whose profile applied but "it still does not work" gets the answer from NetPaw itself:
port open or filtered, which DNS server is dead, where the path stops, path MTU, Wi-Fi signal,
802.1X state, plus Wake-on-LAN, a route tab and a diagnostics zip. Ship as v0.9.0, no new
dependencies, tests on Linux through fake probes.

## Given state
- Repo `smol-kitten/netpaw`, `main` at v0.8.2. 157 Core tests pass on Linux.
- `IProbe` (`Connectivity.cs:11`) has `Ping`, `Resolve`, `Http`. `NetworkProbe` implements it;
  `FakeProbe` in tests. `ConnectivityChecker` runs one probe set per tick.
- `ReachResolver.Resolve(target, adapter, profiles, presets)` accepts an IP or CIDR only.
- `Advisor.AnalyzeCore` produces `Advisory(Severity, Title, Text, CanRenew) { Repair, RepairAdapter, Adapter }`.
- `RouteTable.Parse` (v0.8) reads `netsh interface ipv4 show route`. only the verifier uses it.
- `Wlan.Parse` reads `netsh wlan show interfaces` for SSID/BSSID. signal, channel and rate are
  on the same output and are not parsed.
- `IArpProvider.ReadCache()` gives IP + MAC per neighbour. the network map lists them.
- `PktmonCapture.Listen` captures to a temp file and deletes it.
- `JsonStore` knows `LogFile`, `IncidentsFile`, `Directory`.
- IPv6 moves from v0.9 to v0.10 (operator decision 2026-09-16, `TOOLS-RESEARCH-2026-09.md`).
- No standing rules for scope `netpaw`. Test VM 150 sleeps on disk (suspended). the Windows checks need it.

## Requirements
- R1 `reach <host>:<port>` and CLI `check <host>:<port>` report open / refused / timeout with the
  round-trip. A DNS name resolves first. No port ranges, no scans.
- R2 The advisor names a dead DNS server when at least one other configured server answers.
- R3 Traceroute from the info card and CLI `trace <host>`: hop, address, round-trip, three probes.
- R4 MTU probe finds the largest DF ping that passes and warns when it is below 1500 on a wired
  adapter without a tunnel.
- R5 Wireless adapters show signal %, channel and rate on the info card. an advisory fires under 30 %.
- R6 802.1X: when a wired adapter is up without an address and `netsh lan show interfaces` reports
  an authentication failure, the advisor says so instead of "no address".
- R7 Wake-on-LAN from the network map row and CLI `wake <mac> [broadcast]`.
- R8 Network map gets a *Routes* tab: table from `RouteTable`, *Delete* for manual routes.
- R9 *Export diagnostics…* (tray, settings, CLI `diag`) writes one zip: `ipconfig /all`,
  `route print`, `arp -a`, `netsh interface ipv4 show config`, adapter snapshot JSON, netpaw.log,
  incidents.jsonl, settings.json with repo URLs only (no tokens).
- Must not: send probes without the connectivity setting on (R2–R4 run on demand or when checks
  are enabled). scan port ranges. store credentials in the zip. add NuGet packages. block the UI
  thread (all probes async).

## Options considered
- A. One "Diagnose" wizard that runs everything: rejected: admins want one answer fast, and the
  monitor already knows which layer failed. Each tool attaches to the finding it answers.
- B. Shell out to `Test-NetConnection`, `tracert`, `nslookup` and parse: rejected: slow (PowerShell
  start ~1 s), locale-dependent output. .NET has `TcpClient`, `Ping` with TTL and `DontFragment`,
  UDP sockets. `netsh` stays for what .NET lacks (Wi-Fi, 802.1X).
- C. Add SNMP/mDNS now: rejected: ranked below cost line in the research. revisit after v0.9.
- D. Chosen: extend `IProbe` and the advisor, one PR per slice, each with fake-probe tests.

## Chosen approach
Grow the existing probe seam (`IProbe`) with `Connect`, `Trace`, `PingDf`, `DnsQuery`. keep every
tool a pure function over probe results so Linux tests cover the rules. Surface each result where
the admin already looks: reach mode, info card rows, advisor findings, network map tabs, CLI verbs.

## Steps
1. `Connectivity/Probes.cs`: add `Task<ProbeResult> Connect(string host, int port, int timeoutMs, ct)`
   to `IProbe`. `NetworkProbe` uses `TcpClient.ConnectAsync` with a timeout. `ProbeResult.Detail`
   says "open" / "refused" / "timeout" / "unreachable". `FakeProbe` gets `OpenPorts`.
2. `Reach/ReachResolver.cs`: accept `host:port` and `name` (resolve through `IProbe.Resolve`);
   `ReachKind.PortCheck`. Tray reach flow shows the verdict. CLI `check <host>:<port>` exits 0
   open / 1 closed / 2 timeout.
3. `Connectivity/DnsProbe.cs`: minimal UDP DNS A query (id, flags, one question, parse rcode +
   first answer) over `UdpClient`. `IProbe.DnsQuery(server, name, timeoutMs, ct)`.
   `ConnectivityChecker.Check` queries each adapter DNS server when checks are on;
   `Snapshot.DnsServers` list of `ProbeResult`. Advisor: "DNS 10.0.0.53 does not answer;
   10.0.0.54 does: remove or replace 10.0.0.53" (Warning, `RepairKind.None`).
4. `Connectivity/Trace.cs`: `Trace(host, maxHops=30, probes=3)` over `IProbe.PingTtl(host, ttl,
   timeoutMs, ct)`. stops at the first hop that is the target. Info card: link "Trace" on a
   failed Internet/Intranet row → `TraceForm` (dark ListView). CLI `trace <host>`.
5. `Connectivity/Mtu.cs`: `Discover(host)` binary search 1500→1200 with `IProbe.PingDf(host, size,
   timeoutMs, ct)`. advisor finding when result < 1500 on a wired adapter and no VPN adapter is
   up: "Path MTU to 1.1.1.1 is 1452: a tunnel or PPPoE on the way. Set the adapter MTU to 1452
   (`netsh interface ipv4 set subinterface`)" with a *Set MTU* repair (`RepairKind.SetMtu`).
   Runs once per link-up when checks are on, result cached in the snapshot.
6. `Adapters/Wlan.cs`: parse `Signal`, `Channel`, `Radio type`, `Receive rate`. `WlanInfo` gains
   them. `ConnectivityChecker` attaches `WlanInfo` to the snapshot for wireless adapters (through
   the `RouteReader`-style seam `WlanReader`). Info card row "Wi-Fi: Corp · ch 36 · 72 % · 866 Mbit/s";
   advisor Warning under 30 %.
7. `Adapters/Dot1x.cs`: parse `netsh lan show interfaces` (state, 802.1X state, auth result);
   seam `Dot1xReader`. Advisor: wired adapter up, no address, 802.1X "Authentication failed" →
   Error "802.1X authentication failed on Ethernet: certificate/credentials, not DHCP"
   (replaces the no-address finding).
8. `Net/WakeOnLan.cs`: magic packet (6×FF + 16×MAC) to `255.255.255.255:9` and the subnet
   broadcast. `IWol` seam for tests. Network map row context menu *Wake*. CLI `wake <mac> [broadcast]`.
9. `NetworkMapForm`: *Routes* tab from `RouteTable.Read` (`RouteReader`), columns prefix / next hop /
   interface / metric / type. *Delete* enabled for `Manual` rows → `ApplyPlanner.PlanDeleteRoute`
   through `Execute`. Advisor: two `0.0.0.0/0` manual routes → Warning with *Delete stale* repair.
10. `Diagnostics/Bundle.cs`: `Write(zipPath, runner, store, adapters, settings)` collects the R9
    items. the bundle redacts settings (`RepoUrls` keep the URL only). Tray menu *Export diagnostics…*
    (SaveFileDialog, default `netpaw-diag-<host>-<yyyyMMdd-HHmm>.zip`). CLI `diag [path]`.
11. Help topics: `troubleshooting.md` (port check, DNS, trace, MTU, 802.1X), `network-info.md`
    (Wi-Fi row), `network-map.md` (Wake, Routes), `cli.md`. README feature bullets + table rows.
12. Tests (fake probes/readers): `PortCheckReportsOpenRefusedTimeout`, `ReachParsesHostPort`,
    `DnsQueryBuildsAndParsesPacket`, `AdvisorNamesTheDeadDnsServer`, `TraceStopsAtTarget`,
    `MtuBinarySearchFinds1452`, `AdvisorWarnsOnLowMtuOnlyWired`, `WlanParsesSignalChannelRate`,
    `AdvisorWarnsOnWeakSignal`, `Dot1xParsesAuthFailure`, `AdvisorPrefers8021xOverNoAddress`,
    `MagicPacketLayout`, `PlanDeleteRouteIsOneStep`, `BundleRedactsSettingsAndListsFiles`.
13. Version 0.9.0. PRs: `feat/port-check` (1–2), `feat/dns-trace` (3–4), `feat/mtu-wifi-dot1x`
    (5–7), `feat/wol-routes` (8–9), `feat/diag-bundle` (10–12 rest, 11, 13).
14. VM 150 (resume from disk): port check against the pve host 22/443, trace to 1.1.1.1, MTU on
    "Ethernet 2", diag zip opens. screenshots for README (trace form, routes tab, Wi-Fi row needs
    a wireless NIC: skip if the VM has none).

## Verification
- `dotnet test tests/NetPaw.Core.Tests` ≥ 171 tests, 0 failed.
- CLI on the VM: `netpaw-cli check 10.10.10.1:22` → "open 1 ms", rc 0. `check 10.10.10.1:23`
  → "refused" rc 1. `trace 1.1.1.1` prints ≥ 3 hops. `diag C:\t.zip` → zip lists 8 entries.
- Info card with checks on: DNS row names each server with ✓/✗ after pointing DNS 2 at a dead
  address (`netpaw-cli apply` a profile with `dns: [10.0.0.53, 10.0.0.99]`).
- Advisor finding text for 802.1X and MTU appear in the unit tests with exact strings.
- README shows the new screenshots (fleet rule).

## Risks
- Raw sockets for TTL/DF: .NET `Ping` supports `PingOptions(ttl, dontFragment)` without raw
  sockets → no elevation needed. ICMP blocked on the path → trace shows `*` rows, MTU returns null
  and the advisor stays silent.
- DNS over UDP truncated (TC bit) → treat as "answers" (server alive is the question, not the record).
- `netsh lan` needs the Wired AutoConfig service. when absent the reader returns null and the
  finding does not fire.
- WoL to the wrong subnet broadcast → send to both the global and the adapter's subnet broadcast;
  document that routers do not forward it.
- Diagnostics zip contains addresses by design (admin tool). the help topic says so before the save dialog.
- Time: five PRs ≈ five afternoons. each merges on its own so a stalled slice does not hold the rest.
