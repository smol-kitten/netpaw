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
