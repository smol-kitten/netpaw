# Reach mode

Type an IP address in the quick panel. NetPaw decides, in this order:

- **Already reachable** — an address on the adapter covers it. Nothing to do.
- **Profile** — a saved profile covers it by address or by static route. The profile is applied.
- **Preset** — the address is a known device default (e.g. D-Link `10.90.90.90/8`). A host address in *that* subnet is added.
- **Temporary address** — otherwise NetPaw assumes a `/24` (see *Settings → reach default prefix*) and adds a host from the top of the subnet (`.254`, `.253`, …), skipping anything in use.

## Secondary or replace
By default the new address is added as a **secondary**, so your primary, gateway and DNS keep working. `Ctrl+R` replaces the primary instead. If the adapter is on DHCP, Windows cannot hold a static secondary next to the lease, so NetPaw switches to a temporary static profile and tells you.

## Cleaning up
Temporary addresses are tracked. *Remove N temporary addresses* (panel or tray menu) deletes them; applying any full profile also wipes them, because `set address` replaces every address on the adapter.

## Explicit prefix
`10.20.30.1/28` skips the guessing and uses exactly that subnet.
