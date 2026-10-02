# ✨ Features

The full feature list. The [README](../README.md) keeps a one-line summary per group; how to *use* each
feature is in [usage.md](usage.md).

**Contents:** [Switch networks](#switch-networks) · [See what is wrong](#see-what-is-wrong) ·
[Fix it](#fix-it) · [Find things on the wire](#find-things-on-the-wire) ·
[Interface](#interface) · [Scripts and enterprise](#scripts-and-enterprise) ·
[Updates and builds](#updates-and-builds)

## Switch networks

- **Profiles** — IP(s), prefix, gateway, DNS, suffix, metric, routes, VLAN id, per adapter.
- **Multiple subnets at once** — the first address is the primary, every further line is added as a
  secondary, so one profile can sit in several subnets on the same VLAN.
- **DHCP in one click** (or `Ctrl+D` in the panel).
- **Reach mode** — type an IP. NetPaw picks a profile that covers it, or knows the device's factory
  subnet from the preset list, or adds a temporary address next to it. Nothing to configure. Exact
  order: [usage.md → Reach mode](usage.md#reach-mode-precisely).
- **Device presets** — factory default addresses of ~60 common routers, switches, firewalls, cameras
  (MikroTik, Ubiquiti, Cisco, Fortinet, FRITZ!Box, …). Pick one, you're on its subnet.
- **VLAN aware** — detects whether the NIC driver exposes 802.1Q tagging (`VlanID`) and only then offers
  to set it. No pretending on drivers that can't. Details: [usage.md → VLAN](usage.md#vlan).
- **Static routes** per profile (`10.0.0.0/8 via 192.168.1.1 metric 10`).
- **Global hotkeys** — one to open the panel, one per profile if you like.
- **Auto-switch** (opt-in per profile) — learn a network's fingerprint (gateway MAC, subnet, DHCP
  server, DNS suffix, ARP neighbours, plus the LLDP/CDP switch port and Wi-Fi BSSID when known —
  weighted, so a shared VRRP MAC or a common subnet alone never triggers); on link-up NetPaw applies the
  profile that scores best by a clear margin. Asks once, undo in the menu, never on ambiguity.
- **Scan for a working profile** — tries DHCP then your profiles on the port, measures each, stops at
  the first fully working one or ranks them all; restores your config unless you keep a winner.
- **Verify after apply** — every change (profile, DHCP, undo, repair, CLI) re-reads the adapter 2 s
  later, including static routes and the interface metric; the Windows "static applied but still shows
  DHCP / two gateways" state and a route Windows refused are caught, and *Reset adapter* clears it. The
  CLI returns 1 with the diff.

## See what is wrong

- **Network info card** — `Ctrl+Alt+I`: link, address, gateway, DNS, intranet, internet as ✓/✗ with
  round-trips. Every other host-facing adapter (dock while on Wi-Fi, second NIC, vEthernet) gets an
  *Also* row with its address and a gateway ping, and its own advice lines — the work adapter stays the
  target of repairs. Pin it with 📌.
- **Quiet by default** — every info-card row has a mode (*never / on issue / always*), so a healthy
  network shows five lines and a dock, Wi-Fi, VPN or switch row appears only when it has something to
  say. Errors always show. Auto-pin uses the same modes.
- **Advisory** — the card says *what* is wrong: "lease without gateway (option 3)", "no DHCP lease
  (169.254)", "gateway from lease does not answer — stale after a port move?", "DNS servers not
  answering". Static profiles get the matching hints.
- **Monitor mode** (opt-in) — probes gateway/intranet/internet/DNS on an interval and tells you *what*
  changed: "Internet lost — intranet still reachable", "Link down", "Back online". A red dot on the paw
  while there is a problem; the card pins itself until things recover. Details:
  [usage.md → Monitor mode](usage.md#monitor-mode-precisely).
- **Intent watcher** — "msedge cannot reach 192.168.88.1:80 — no reply for 4 s. Profile *MikroTik lab*
  covers it" with a one-click *Reach*. Reads the local TCP table every 2 s (one call, nothing sent),
  tells host-silent from nobody-beyond-the-gateway, never acts by itself.
- **VPN status** — WireGuard, OpenVPN, Windows native, Tailscale, ZeroTier: up/down, full vs split
  tunnel, change notifications.
- **Captive-portal detection** with checks on.
- **Incident log** (opt-in) — timestamps of every state change and configuration finding in
  `incidents.jsonl`, for "when did the WAN drop?" and tickets.
- **Self-watching** — the check loop backs off on a stable network (30 → 120 s), returns to the fast
  rate on any change, cancels a hung round after twice the interval, and tells you when its last result
  is stale. Every background task is guarded: a failure is logged with its name (and, in the telemetry
  build, reported with the last 40 log lines and the network state).

## Fix it

- **Auto-repair** (opt-in) — when the advisory says a fresh lease could help, NetPaw runs
  `ipconfig /renew` itself: bounded attempts per incident, spaced, DHCP adapters only, every attempt
  logged.
- **One-click fixes** — dock problems ("old NIC still holds the lease") and "two default gateways" get
  *Release* / *Prefer*; path MTU gets *Set MTU*; a stale route gets *Delete route*; a Windows that
  didn't take the change gets *Reset adapter*.
- **Transparent** — every change is a plain `netsh` command you can preview and copy. Nothing hidden in
  WMI calls; the log shows exactly what ran. See [What it runs](../README.md#what-it-runs).

## Find things on the wire

- **Network map** — ARP neighbours bundled per subnet (passive), active re-check or subnet sweep, and
  *find routers*: borrow an address in each common/vendor subnet, ARP the usual gateways, report who
  answers with vendor hints. Right-click a neighbour → *Wake* (Wake-on-LAN).
- **Which switch port am I on?** — LLDP/CDP via Windows' built-in `pktmon` (no driver, nothing sent):
  switch name, port, VLAN, management address. Optional after every link-up.
- **Port check and trace** — type `10.0.0.5:443` in the panel; *Trace route to …* from the info card.

## Interface

- **Quick panel** — type a vendor, a profile name or an IP; `Enter` applies.
- **Main window** (optional) — Overview · Profiles · Tools · Map · Log in one resizable dark window; tray
  menu, double-click, `--main`, or make the tray click open it. The tray, hotkeys and quick panel stay as
  they are.
- **F1 help** on every view.

## Scripts and enterprise

- **CLI twin** (`netpaw-cli.exe`) for scripts and remote sessions. Same profiles, same logic. Command
  list: [usage.md → CLI](usage.md#cli).
- **Enterprise-ready** — MSI for Intune/GPO, read-only managed profiles from `%ProgramData%`, registry
  policy with ADMX (pin the adapter/hotkey, deny reach/DHCP/user profiles). See
  [ENTERPRISE.md](ENTERPRISE.md).
- **Online profile packs** — subscribe to signed `index.json` repositories (the community pack in this
  repo, or your company's). Fetched only when you click *Sync*, cached, never auto-applied. See
  [PACK-FORMAT.md](PACK-FORMAT.md).

## Updates and builds

- **Signed releases** — from v0.13.1 every exe, MSI and zip is signed by the catboy.systems PKI. See
  [signed-release.md](signed-release.md).
- **Update check** (opt-in — asked once on first start; one GitHub API call a day) — for a newer release
  NetPaw downloads the installer and checks it first: its sha256 must be in the release's
  `SIGNATURES.md` and its signature must chain to the root built into NetPaw. Only then does the balloon
  offer *Install*; otherwise it says **NOT verified** and opens the release page. *Settings → Updates* or
  policy `AllowUpdateCheck=0` turns it off.
- **Small** — two executables, ~1.5 MB together (framework-dependent), no service, no driver.
- **Telemetry only if you pick it** — the normal build contains no telemetry code. The `-telemetry` MSI
  adds crash reports, anonymous usage counts, and OTLP/HTTP logs and RFC 5424 syslog to your own
  collector. See [TELEMETRY.md](TELEMETRY.md).
