# NetPaw 🐾

**Static-IP profiles for Windows network admins — in the tray, one hotkey away.**

You plug into a switch that lives on `192.168.88.0/24`, then a customer LAN on `10.20.0.0/16`,
then you need DHCP again, then the Fortigate at `192.168.1.99`… NetPaw makes each of those one
keystroke instead of a trip through *Control Panel → Network → Adapter → Properties → IPv4*.

- **Profiles** — IP(s), prefix, gateway, DNS, suffix, metric, routes, VLAN id, per adapter.
- **Multiple subnets at once** — the first address is the primary, every further line is added as a
  secondary, so one profile can sit in several subnets on the same VLAN.
- **DHCP in one click** (or `Ctrl+D` in the panel).
- **Reach mode** — type an IP. NetPaw picks a profile that covers it, or knows the device's
  factory subnet from the preset list, or adds a temporary address next to it. Nothing to configure.
- **Device presets** — factory default addresses of ~60 common routers, switches, firewalls,
  cameras (MikroTik, Ubiquiti, Cisco, Fortinet, FRITZ!Box, …). Pick one, you're on its subnet.
- **VLAN aware** — detects whether the NIC driver exposes 802.1Q tagging (`VlanID`) and only then
  offers to set it. No pretending on drivers that can't.
- **Static routes** per profile (`10.0.0.0/8 via 192.168.1.1 metric 10`).
- **Global hotkeys** — one to open the panel, one per profile if you like.
- **Transparent** — every change is a plain `netsh` command you can preview and copy. Nothing hidden
  in WMI calls; the log shows exactly what ran.
- **Tiny and fast** — two executables, ~1.5 MB together, no installer, no service, no telemetry.
- **CLI twin** (`netpaw-cli.exe`) for scripts and remote sessions. Same profiles, same logic.
- **Enterprise-ready** — MSI for Intune/GPO, read-only managed profiles from `%ProgramData%`, registry policy with ADMX (pin the adapter/hotkey, deny reach/DHCP/user profiles). See [docs/ENTERPRISE.md](docs/ENTERPRISE.md).
- **Online profile packs** — subscribe to signed `index.json` repositories (the community pack in this repo, or your company's). Fetched only when you click *Sync*, cached, never auto-applied. See [docs/PACK-FORMAT.md](docs/PACK-FORMAT.md).

MIT licensed. Windows 10/11, .NET 10.

![quick panel — type a vendor, a profile, or an IP](docs/panel.png)

| profile editor | plan preview | settings |
|---|---|---|
| ![editor](docs/editor.png) | ![preview](docs/preview.png) | ![settings](docs/settings.png) |

*Screenshots from the Windows 11 test VM; every `netsh` path in this README was verified there.*

## Install

Grab a zip from [Releases](../../releases):

| zip | needs | size |
|---|---|---|
| `netpaw-win-x64.zip` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | ~1.5 MB |
| `netpaw-win-x64-standalone.zip` | nothing | ~110 MB |
| `NetPaw-<version>.msi` | [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | ~1 MB, per-machine, for Intune/GPO |

Unzip anywhere, run `NetPaw.exe`. It asks for elevation once (adapter changes need it) and lives in the
tray. *Settings → start with Windows* creates an elevated scheduled task so there is no UAC prompt at
logon.

## Use

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
netpaw-cli apply <profile> [-a X] [-n]      apply a profile (-n = dry run, print the plan)
netpaw-cli dhcp [-a X]                      switch the adapter to DHCP
netpaw-cli reach <ip[/prefix]> [--replace]  make <ip> reachable
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
netpaw-cli pack keygen|build|sign|verify author and sign a pack
```

Read-only commands work from any prompt; changes need an elevated one. Exit codes: 0 ok, 1 a step failed, 2 usage, 3 not VLAN-capable, 4 denied by policy.

## Files

Everything is JSON in `%APPDATA%\NetPaw\` (override with `NETPAW_HOME`):

- `profiles.json` — your profiles. Hand-editable, git-able, copy to the next laptop.
- `presets.json` — your own device presets; same schema as the bundled list, same Vendor/Model overrides.
- `settings.json`, `state.json` (temporary addresses), `netpaw.log`, `cache\` (synced packs).
- `%ProgramData%\NetPaw\profiles.d\*.json` — managed profiles deployed by your organisation (read-only, *managed* badge).
- `HKLM\SOFTWARE\Policies\NetPaw` — machine policy (see [docs/ENTERPRISE.md](docs/ENTERPRISE.md)).

A profile:

```json
{
  "name": "Lab core",
  "adapter": null,
  "dhcp": false,
  "addresses": [ { "address": "10.20.0.5", "prefixLength": 16 }, { "address": "192.168.88.250", "prefixLength": 24 } ],
  "gateway": "10.20.0.1",
  "dns": [ "10.20.0.53" ],
  "routes": [ { "destination": "172.16.0.0", "prefixLength": 12, "gateway": "10.20.0.254", "metric": 10 } ],
  "vlanId": 20,
  "hotkey": "Ctrl+Alt+1"
}
```

`adapter: null` means "the work adapter" (auto-detected: first physical NIC with a gateway; or pick one
in the tray menu).

## What it runs

A static profile becomes, verbatim:

```
netsh interface ipv4 set address name="Ethernet" source=static address=10.20.0.5 mask=255.255.0.0 gateway=10.20.0.1 gwmetric=1
netsh interface ipv4 add address name="Ethernet" address=192.168.88.250 mask=255.255.255.0
netsh interface ipv4 set dnsservers name="Ethernet" source=static address=10.20.0.53 register=primary validate=no
netsh interface ipv4 add route prefix=172.16.0.0/12 interface="Ethernet" nexthop=10.20.0.254 metric=10 store=persistent
powershell ... Set-NetAdapterAdvancedProperty -Name 'Ethernet' -RegistryKeyword 'VlanID' -RegistryValue 20
```

`set address source=static` replaces every address on the adapter, which is what makes profile
switches deterministic: leftovers from the previous profile or from reach mode are gone in step 1.
The first step is *critical* (a failure aborts the rest); the others are best-effort and reported.

## Build

```
dotnet test                                   # core logic, runs on any OS
dotnet publish src/NetPaw.Tray -c Release -r win-x64 --self-contained false -o out
dotnet publish src/NetPaw.Cli  -c Release -r win-x64 --self-contained false -o out
```

Layout: `src/NetPaw.Core` (models, IP math, planner, reach resolver, presets, policy, repos — no
Windows dependencies, fully unit-tested), `src/NetPaw.Tray` (WinForms, dark, no designer files),
`src/NetPaw.Cli`, `deploy/` (MSI, ADMX, Intune scripts), `packs/` (online packs, not built in).
The MSI is built and install-tested on a hosted Windows runner; everything else runs on Linux.

## Not (yet) in scope

IPv6, Wi-Fi profile switching, multi-VLAN trunk NICs, per-profile firewall rules. PRs welcome — the
planner is pure and tested, so adding a step is a small change with a test next to it.
