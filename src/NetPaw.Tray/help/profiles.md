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
