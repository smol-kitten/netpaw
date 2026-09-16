# Network info & monitor

## The info card
`Ctrl+Alt+I` (or *Network info* in the tray menu / panel) shows the work adapter at a glance:
- **Link** up/down, **Address** (and whether it is DHCP), **Gateway**, **DNS** — each with ✓ or ✗.
- With checks enabled: gateway round-trip, **Intranet** hosts, **Internet** targets.
- The header line names the state: *Online*, *Intranet only — internet unreachable*, *Cable unplugged*, …

The card fades after a few seconds. Click 📌 to keep it; ✕ closes it.

## Reachability checks (off by default)
*Settings → Connectivity → check reachability*. Every interval NetPaw pings the default gateway, your extra intranet hosts, the internet targets (`1.1.1.1`, `9.9.9.9`), and resolves one name to prove DNS. Nothing is probed until you switch it on.

## Monitor mode
With checks on, *monitor mode* tells you when the state **changes**: internet lost while the intranet still answers, gateway gone, link down, back online. One notification per change; no nagging while nothing changes. A red dot appears on the tray paw while there is a problem.

## Sticky alert
*keep the info card on screen while there is a problem*: the card pins itself when the link drops (or any problem is detected) and releases itself when things recover. Pin it yourself and it stays regardless.

## States
- **Link down** — cable/port. **No address** — link up, DHCP not answering.
- **No reachable gateway** — switch/router side. **Intranet only** — WAN or upstream. **DNS failing** — pings work, names do not.

## Advisory
The info card and alerts explain *what* is wrong with the configuration, not just that something is:
- DHCP: no lease (169.254 self-assigned), lease without gateway (option 3) or without DNS (option 6), gateway from the lease not answering (stale lease after a port move), DNS servers not answering while the internet is reachable.
- Static: no gateway (fine for a setup subnet), gateway not answering (wrong subnet/VLAN for this port), no DNS.
Purely local analysis; probes only when checks are enabled. *Settings → Advisory* turns it off.

## Auto-repair (off by default)
When an advisory says a fresh lease could help, NetPaw can run `ipconfig /renew` on the adapter by itself: at most *N attempts per incident*, spaced by the interval, and only while the adapter is on DHCP. The counter resets once the state is healthy. Every attempt is in the log. *Renew DHCP lease* is also in the tray menu, the panel, and on the info card.

## Incident log (off by default)
*Settings → Incident log* writes every state change and every configuration finding with a timestamp to `%APPDATA%\NetPaw\incidents.jsonl` (one JSON object per line). Use it to answer "when exactly did the WAN drop?" or to attach to a ticket. `netpaw-cli incidents -n 50` prints the last entries; *Open incident log* is in the tray menu.

## Scan for a working profile
*Scan for a working profile…* (tray menu, or type `scan` in the panel) tries **DHCP first, then your profiles** on the work adapter, measures each (address, gateway, internet, DNS) and either stops at the first fully working one or tries all and **ranks by usability**. Your current configuration is captured first and restored unless you click *Keep selected*. Cancel restores too. `netpaw-cli scan [--all] [--keep]` does the same from a prompt.

## Network map (ARP)
*Network map…* (tray menu, or `arp`/`map` in the panel) shows the neighbours Windows already knows (the ARP cache), bundled per subnet of your adapters — passive, no packets. *Re-check* ARPs each of them; *Sweep subnet* asks every address of the adapter's subnet (a /24 takes ~10 s). Vendor defaults and your gateway are annotated. CLI: `netpaw-cli arp [--check|--sweep]`.

## Find routers
Unknown port? *Find routers* borrows a temporary address in each common subnet (192.168.0/1/2/178/88…, 10.0.0, 172.16.x and every vendor default from the presets), ARPs the usual gateway addresses (.1, .254, the vendor default) and removes the address again — ARP only, ~1-2 s per subnet. Responders are listed with MAC and the vendors whose default that address is; type one in the panel to reach it. CLI: `netpaw-cli find-routers`. Needs temporary addresses to be allowed by policy.

## VPN status
The info card lists VPN adapters NetPaw recognises (WireGuard/wintun, OpenVPN TAP/DCO, Windows native IKEv2/L2TP/SSTP, Tailscale, ZeroTier, common enterprise clients) with *full tunnel* (the VPN owns the default route) or *split tunnel*. Up/down and full↔split changes are announced (*Settings → VPN*) and logged to the incident log. Detection is passive — no vendor APIs, nothing sent.

## Which switch port am I on? (LLDP/CDP)
*Network map → Switch (LLDP/CDP)* listens for the switch's own announcements with Windows' built-in `pktmon` — no driver, nothing sent. LLDP comes every 30 s, Cisco CDP every 60 s, so listen 35 or 65 s. You get the switch name, port (e.g. Gi1/0/24), port description, VLAN, management address and platform. *Settings → Switch* can run it after every link-up so the info card shows "Connected to sw-core-01 port Gi1/0/24 VLAN 20". CLI: `netpaw-cli switch`. Needs Windows 10 2004+ / 11; access ports on some switches do not announce.

Limitation, stated plainly: `pktmon` sees only what the NIC driver hands to Windows. Most physical NICs deliver the LLDP multicast group; some drivers (and emulated NICs such as QEMU's e1000) drop it until an LLDP agent registers the group. NetPaw enables Windows' own agent when the Hyper-V module is present (`Enable-NetLldpAgent`); without it, a silent 65 s may be the driver, not the switch. Wireshark sees those frames only because Npcap switches the NIC to promiscuous mode — a driver NetPaw deliberately does not ship.
