# 🚀 Usage

<sub>Split from README.md by readme_kit; the README keeps the summary.</sub>

| do | how |
|---|---|
| open the panel | `Ctrl+Alt+N` (configurable), left-click the tray paw, or run `NetPaw.exe` again |
| apply a profile | type a few letters, `Enter` — or its hotkey, or the tray menu |
| DHCP | `Ctrl+D` in the panel, or *DHCP now* in the menu |
| reach a device | type its IP in the panel, `Enter`. `Ctrl+R` replaces the primary instead of adding a secondary |
| use a preset | type the vendor (`mikro`, `forti`, `fritz`…) or pick *Device presets* in the menu |
| save what's configured right now | *Capture current as profile…* |
| undo temporary addresses | *Remove N temporary addresses* (menu / panel) |
| see what will run | *Settings → confirm before applying*, or *Preview plan* in the editor |
| weak Wi-Fi / 802.1X refused / path MTU 1452? | info card rows *Wi-Fi*, *802.1X*, *Path MTU* with advice + one-click *Set MTU* |
| which DNS server is dead? | info card DNS row shows each server ✓/✗ (checks on); the advice row names it |
| where does the path stop? | info card → *Trace route to …* when internet/gateway is ✗, or `netpaw-cli trace 1.1.1.1` |
| browser says it cannot reach a device | wait 4 s — the advice row names the program, the target and the profile that covers it; `netpaw-cli stuck` |
| is that port open from here? | type `10.0.0.5:443` (or `nas:22`) in the panel, or `netpaw-cli check 10.0.0.5:443` |
| see what a change will run | hold **Shift** while picking it (or *Settings → Confirm* for every time), or `netpaw-cli apply <p> -n` |
| check the link | `Ctrl+Alt+I` — the info card; 📌 keeps it |
| get told when the network changes | *Settings → Connectivity → check reachability* + *monitor mode* |
| renew the lease | *Renew DHCP lease* (menu / panel / info card), or `netpaw-cli renew` |
| see what's wrong with the config | info card *Advice* rows, or `netpaw-cli advise` |
| find a profile that works on this port | *Scan for a working profile…* (menu / `scan` in the panel), or `netpaw-cli scan [--all] [--keep]` |
| helpdesk wants "everything" | tray → *Export diagnostics…* (zip: ipconfig, routes, arp, netsh, log, incidents, redacted settings), or `netpaw-cli diag` |
| when did it break? | *Settings → Incident log*, then *Open incident log* or `netpaw-cli incidents` |
| wake the box on the bench | *Network map* → right-click a neighbour → *Wake*; `netpaw-cli wake <mac>` |
| a stale route eats my traffic | *Network map → Routes* → *Delete route*; advice row *Two default routes* offers it too |
| who is on this cable? | *Network map…* (menu / `map` in the panel) → Neighbours, Re-check, Sweep, Find routers; `netpaw-cli arp`, `find-routers` |
| switch automatically on a known network | editor → Advanced → *Learn from current network* + *auto-switch* |
| Windows didn't take the change | info card → *Reset adapter*; `netpaw-cli verify <profile>`, `reset`, `release`, `prefer` |
| help | `F1` anywhere; opens on the current view |

The tray paw is **blue** on DHCP, **green** on static, **orange** while temporary addresses are
present, **grey** when the adapter is down.

### Reach mode, precisely

Typing `192.168.88.1` does, in order:

1. Adapter already has an address in that subnet → nothing to do.
2. A saved profile covers it (by address or by static route) → apply that profile.
3. It's the default address of a known device → add a host address in *that* subnet (e.g. `/8` for D-Link's `10.90.90.90`).
4. Otherwise assume a `/24` (configurable) and add a temporary host address from the **top** of the
   subnet (`.254`, `.253`, …), skipping the target and anything already in use — so it never collides
   with the `.1`/`.10` most gear ships with.

Steps 3–4 add a **secondary** address by default, so your current primary, gateway and DNS keep working.
If the adapter is on DHCP a secondary is not possible (`netsh` limitation) and NetPaw switches to a
temporary static profile instead; it tells you. `10.20.30.1/28` with an explicit prefix skips the
guessing.

### Monitor mode, precisely

Off by default — nothing is probed until you enable checks. Then every interval NetPaw pings the
default gateway, your extra intranet hosts, the internet targets (`1.1.1.1`, `9.9.9.9`) and resolves
one name. The result is one of: *link down*, *no address* (link up, DHCP silent — a 169.254 address
counts as none), *no reachable gateway*, *intranet only*, *DNS failing*, *online*. Monitor mode
notifies on **transitions only**, after the new state held for 3 s (10 s for *no address*, DHCP
territory) — no nagging. Link up/down is always announced; cable pulls are caught instantly through
Windows' network-change events, not the interval.

### VLAN

Windows only tags if the NIC driver exposes the `VlanID` advanced property (Intel, some Realtek/Marvell,
Hyper-V vNICs). NetPaw reads it through the NetAdapter CIM class — the same data
`Get-NetAdapterAdvancedProperty` shows — and the editor tells you per adapter whether tagging is
available. Setting it resets the link for a few seconds; that is the driver, not NetPaw. Multi-VLAN
trunking on one NIC needs Intel PROSet / Hyper-V switch, which is out of scope.

### CLI

```
netpaw-cli adapters                         list adapters, current config, VLAN capability
netpaw-cli list                             list profiles (* = matches the adapter right now)
netpaw-cli show <profile>                   print one profile
netpaw-cli apply <profile> [-a X] [-n]      apply a profile (-n = dry run, print the plan)
netpaw-cli dhcp [-a X]                      switch the adapter to DHCP
netpaw-cli reach <ip[/prefix]> [--replace]  make <ip> reachable
netpaw-cli check <host:port> [--timeout ms] one TCP connect: open / refused / timeout (rc 0/1/2)
netpaw-cli trace <host> [--max N]           traceroute: where does the path stop?
netpaw-cli wake <mac> [subnet]              Wake-on-LAN magic packet
netpaw-cli diag [file.zip]                  diagnostics bundle for the helpdesk
netpaw-cli stuck                            who waits on an unanswered connection, and which profile covers it
netpaw-cli routes                           route table with interface names
netpaw-cli preset [query]                   list presets
netpaw-cli preset apply <vendor/model|query>
netpaw-cli capture <name> [-a X]            save the adapter's live config as a profile
netpaw-cli temp | clear-temp                list / remove temporary addresses
netpaw-cli vlan <adapter> [id]              query / set the driver VLAN id (0 = untagged)
netpaw-cli where                            print the config directory
```

```
netpaw-cli export <file> [--managed]    write profiles as JSON (--managed = deployment file for profiles.d)
netpaw-cli import <file>                add profiles from JSON
netpaw-cli policy                       show the effective machine policy
netpaw-cli repo [add|remove|sync|search] online profile repositories
netpaw-cli arp [--check|--sweep] / find-routers [--force] / switch [--seconds N]   network map + LLDP/CDP (--force = allowed to drop a DHCP lease)
netpaw-cli advise / renew / release / reset / prefer / verify <profile>   diagnosis + repairs
netpaw-cli scan [--all] [--repos] [--keep] / incidents [-n N]
netpaw-cli pack keygen|build|sign|verify author and sign a pack
```

Read-only commands work from any prompt; changes need an elevated one. Exit codes: 0 ok, 1 a step failed, 2 usage, 3 not VLAN-capable, 4 denied by policy.

---
