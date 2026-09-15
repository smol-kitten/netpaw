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
