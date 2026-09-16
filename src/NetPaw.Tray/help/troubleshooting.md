# Troubleshooting

## "changing adapter settings needs an elevated prompt"
`netsh` needs administrator rights. The tray runs elevated; for the CLI open an elevated prompt.

## The hotkey does nothing
Another program owns the combination — NetPaw tells you at start. Change it in Settings. If policy pins it, the greyed value is the one that works.

## A secondary address was refused
The adapter is on DHCP. Windows cannot keep a static secondary next to a lease. Apply a static profile first, or use *replace* (`Ctrl+R`).

## VLAN is greyed out
The NIC driver exposes no `VlanID` property. Only some drivers (Intel, some Realtek/Marvell, Hyper-V vNICs) can tag from the OS side.

## A repository shows "rejected"
Its signature or an entry hash did not match the pinned key. NetPaw keeps the last good copy. Ask the publisher for the right key or remove the pin (`url` without `|key`).

## Helpdesk wants everything
Tray menu → **Export diagnostics…** writes one zip: `ipconfig /all`, the route and interface tables, `netsh interface ipv4 show config`, `arp -a`, the DNS cache, NetPaw's adapter snapshot, `netpaw.log`, `incidents.jsonl`, `profiles.json`, `state.json` and `settings.json`. Addresses and adapter names are in there on purpose — that is what support needs. Export headers, tokens and anything password-shaped are replaced with `[redacted]`. CLI: `netpaw-cli diag`.

## Something went wrong — where do I look?
`%APPDATA%\NetPaw\netpaw.log` has every command NetPaw ran with its output. *Open log* is in the tray menu.

## "Link up, no IPv4 address" on a Hyper-V host
The physical NIC bound to an external virtual switch carries the switch and has no host address by design; the host's address lives on the matching **vEthernet (…)** adapter. NetPaw detects this (summary *Hyper-V switch uplink*), auto-detect prefers the vEthernet adapter, and the info card explains it. If you pinned the physical NIC as work adapter, pick the vEthernet one in *tray menu → Work adapter*.

## "Applied, but Windows still shows DHCP / two gateways"
A known Windows quirk after switching static over a DHCP lease. NetPaw verifies every apply 2 s later and warns with the differences; *Reset adapter* (disable → enable, in the tray menu and on the info card) clears it. The card also flags **Address held by another adapter** (dock: the unplugged NIC still owns the lease — *Release* it) and **Two default gateways** (*Prefer* pins the metrics).

## Captive portal
With checks on, NetPaw fetches the Microsoft connect-test page after DNS works; a login page instead of the expected body shows as *Captive portal — open a browser to sign in*.

## "Edge says it cannot reach 192.168.88.1"
NetPaw watches the local TCP table every two seconds (one in-process call, nothing sent). A connection that sits unanswered for two samples becomes an *Advice* row: **msedge cannot reach 192.168.88.1:80 — no reply for 4 s (no reply from beyond the gateway). Profile 'MikroTik lab' covers it** with a *Reach* link (profile, preset or temporary address — whatever the reach resolver picks). *On this subnet but no answer* means the host is down or on another VLAN. One balloon per address per ten minutes. Not visible to the watcher: connections that fail instantly because there is no route at all (a static profile without gateway — the adapter advice covers that), and targets behind a VPN or proxy (you see the tunnel address). Off switch: *Settings → Repair → Intent watcher*; policy `AllowIntentWatch`. CLI: `netpaw-cli stuck`.

## "Profile applied, but the device still does not answer"
Type `10.0.0.5:443` (or `printer:9100`) into the panel: NetPaw makes one TCP connect and says **open**, **refused** (host is there, nothing listens on that port or its firewall rejects), **timed out** (a firewall drops it or the host is off) or **not reachable** (no route — reach the address first). Nothing is changed, nothing is scanned. CLI: `netpaw-cli check 10.0.0.5:443`.

## "Internet ✗" — where does it stop?
With checks on, the info card offers **Trace route to 1.1.1.1** whenever internet or the gateway is ✗. Each hop gets three ICMP probes; the window says *path stops after hop 4 (10.0.0.1)* — that hop is the last box that still forwarded. Silence right at hop 1 means ICMP is blocked or there is no route. CLI: `netpaw-cli trace 1.1.1.1`.

## Names resolve slowly or not at all
With checks on, NetPaw asks each configured DNS server directly. The DNS row shows `10.0.0.53 ✗, 10.0.0.54 ✓ 3 ms` and the advice names the dead one — a dead *first* server delays every lookup by its timeout before Windows falls back. Fix it in the profile, or on the DHCP server.
