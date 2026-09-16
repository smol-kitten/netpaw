# Network tools NetPaw could absorb (research, 2026-09-16)

Question: which everyday admin tools would fit NetPaw without new dependencies? Rule from the LLDP
work: Windows built-ins and pure .NET only (no Npcap, no SNMP libraries, no GPL). Scored on pain
(1–5), reach (how many NetPaw users hit it, 1–5) and cost (1 = an afternoon, 5 = a milestone).
The last column names where it plugs into what exists.

| Rank | Tool | What it answers | How (no deps) | Pain | Reach | Cost | Plugs into |
|---|---|---|---|---|---|---|---|
| 1 | **TCP port check** | "Is 10.0.0.5:443 open from here, or is it the firewall?" | `TcpClient` connect with timeout; optional TLS handshake | 5 | 5 | 1 | Reach mode: `reach 10.0.0.5:443`; info card; CLI `check` |
| 2 | **Diagnostics bundle** | Helpdesk asks "send me ipconfig /all, route print, arp -a, the log" | zip of those outputs + `netpaw.log` + `incidents.jsonl` + adapter snapshot; addresses kept (it is for the admin) | 4 | 5 | 1 | Tray menu *Export diagnostics…*; CLI `diag` |
| 3 | **Per-server DNS check** | "Which DNS server is the broken one?" | minimal UDP DNS query (A record) to each configured server; pure .NET sockets | 4 | 4 | 2 | Advisor: "DNS 10.0.0.53 ✗, 10.0.0.54 ✓ — 0.53 is dead"; info card DNS row |
| 4 | **Traceroute** | "Internet ✗ — where does the path stop?" | `Ping` with rising TTL (pure .NET); parallel per-hop, 3 probes | 4 | 4 | 2 | Info card link on Internet/Intranet ✗; map tab *Path*; CLI `trace` |
| 5 | **MTU probe** | "VPN/PPPoE sites hang on large pages" | `Ping` with DontFragment, binary search 1500→1200 | 4 | 3 | 1 | Advisor finding "path MTU 1452 on Ethernet — set adapter MTU or fix the tunnel"; CLI `mtu` |
| 6 | **Wake-on-LAN** | "Wake the box on the bench" | UDP magic packet to broadcast + directed | 3 | 4 | 1 | Network map row → *Wake*; MACs already in the ARP table; profile-level saved targets |
| 7 | **Wi-Fi signal / channel** | "Is Wi-Fi the problem?" | `netsh wlan show interfaces` (parser exists for BSSID): signal %, channel, band, rate | 3 | 4 | 1 | Info card row for wireless adapters; advisor "signal 18 %" |
| 8 | **802.1X port state** | "Enterprise port gives no DHCP — auth failed?" | `netsh lan show interfaces` (wired) / `netsh wlan` (wireless) auth state | 4 | 2 | 1 | Advisor: "802.1X authentication failed on Ethernet" instead of "no address" |
| 9 | **DHCP lease details** | "Lease expires when? Renew failing silently?" | WMI `Win32_NetworkAdapterConfiguration` (`DHCPLeaseObtained/Expires`) or `ipconfig /all` parse | 3 | 4 | 2 | Info card; advisor "lease expires in 3 min and renew fails" |
| 10 | **Route table viewer** | "A stale route from the old VPN is eating traffic" | `RouteTable.Parse` exists since v0.8 | 3 | 3 | 1 | Map tab *Routes* with *Delete* (netsh delete route); advisor for duplicate 0.0.0.0/0 |
| 11 | **pktmon capture to file** | "Support wants a pcap" | `PktmonCapture` exists; add duration + save-as | 3 | 2 | 1 | Map *Switch* tab → *Capture 30 s to .pcapng* |
| 12 | **Reverse names in the map** | "Which of these 40 hosts is the printer?" | `Dns.GetHostEntry` reverse lookup + NetBIOS name query (UDP 137, tiny) | 2 | 4 | 1 | Network map *Name* column |
| 13 | **mDNS / SSDP discovery** | Printers, NAS, APs by name | UDP multicast queries, parse responses | 2 | 3 | 3 | Network map extra source |
| 14 | **SNMP v2c read** | Switch port descriptions, VLAN of our port, uplink | BER encoder for GET/GETNEXT is ~200 lines pure .NET; community string in settings | 3 | 2 | 3 | Map *Switch* tab when LLDP is silent |
| 15 | **NTP offset** | "Kerberos fails — clock drift?" | `w32tm /stripchart` once, or SNTP packet | 2 | 2 | 1 | Advisor when offset > 60 s |
| 16 | **Proxy state** | "Proxy set by GPO/PAC, curl fails" | `netsh winhttp show proxy` + WinINET registry | 2 | 3 | 1 | Info card row; captive probe already proxy-aware |
| 17 | **Latency sparkline** | "It is flaky, not down" | monitor already pings; keep 60 samples, draw in the card | 2 | 3 | 1 | Info card |
| 18 | **VLAN scan** | "Which VLAN has DHCP on this trunk port?" | set driver VLAN id, wait for lease, next | 3 | 1 | 3 | Scan dialog mode; slow, driver-dependent |
| — | Throughput test | needs a server (iperf) | — | — | — | — | out of scope: dependency + server |
| — | Full port scanner | nmap territory | — | — | — | — | out of scope: tone + abuse risk; single-port check only |

## Recommendation: v0.9 "diagnose" (before IPv6)

Ranks 1–7 are each an afternoon, share the probe layer that exists (`IProbe`, `NetworkProbe`),
and answer the questions an admin asks right after "the profile applied but it still does not
work". IPv6 (planned v0.9) is a milestone on its own; these seven fit in front of it as v0.9 and
IPv6 becomes v0.10.

Proposed slices (one PR each, tests on Linux via fake probes):
1. Port check + reach `host:port` + CLI `check` (rank 1).
2. Diagnostics bundle (rank 2) — zip via `System.IO.Compression`, CLI `diag`.
3. DNS per-server + advisor finding (rank 3); traceroute + info card link (rank 4).
4. MTU probe + advisor (rank 5); Wi-Fi signal row (rank 7); 802.1X state (rank 8).
5. Wake-on-LAN in the map (rank 6); route table tab with delete (rank 10).

Not now: SNMP (3 days for a niche gain over LLDP), mDNS/SSDP (map already has ARP + names once
rank 12 lands), VLAN scan (slow and driver-dependent), NTP/proxy rows (cheap but low pain — add
when someone asks).
