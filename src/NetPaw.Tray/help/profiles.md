# Profiles

Open the editor with *Manage profiles…* or `Ctrl+E` in the panel. Left: the list. Right: three sections.

## Identity
- **Name** — what you type in the panel.
- **Adapter** — a specific adapter, or *(work adapter)* = the one selected in the tray menu.
- **Hotkey** — global, e.g. `Ctrl+Alt+1`. Needs a modifier.

## IPv4
- **Mode** — DHCP or static.
- **Addresses** — one per line, `ip/prefix` or `ip mask`. The **first line is the primary**; the rest are added as secondaries, so one profile can sit in several subnets on the same VLAN.
- **Gateway** and metric, **DNS** servers (space separated), **DNS suffix**.

## Advanced (collapsed unless used)
- **Interface metric** — 0 leaves Windows' automatic metric.
- **VLAN** — only offered when the driver exposes the `VlanID` property. Setting it resets the link for a few seconds (driver behaviour).
- **Routes** — `10.0.0.0/8 via 192.168.1.1 metric 10`, one per line, persistent.

## Buttons
- **Save** (`Ctrl+S`), **Preview plan** (`Ctrl+P`) shows the exact `netsh` commands, **Save & apply** (`Ctrl+Enter`).
- **Capture** saves the adapter's live configuration as a new profile. **Copy** duplicates (also the way to make a managed profile your own).

Errors appear under the field they belong to. Managed profiles are read-only.

## Auto-switch
In *Advanced*, tick **auto-switch** and click **Learn from current network** while the profile's network is on the cable. NetPaw stores a fingerprint — gateway MAC, subnet, DHCP server — and from then on applies the profile by itself when a link comes up and exactly one fingerprint matches. The first automatic switch asks once; *Undo last auto-switch* is in the tray menu. Nothing happens on ambiguous matches, while another change runs, or if the profile is already active. Static networks without DHCP are recognised by briefly borrowing the profile's own address for one ARP. Limit: a gateway behind VRRP/HSRP/CARP shows a shared virtual MAC that repeats across sites, so such a fingerprint only matches when the DHCP server matches too — static-only sites behind a virtual router never auto-switch.
