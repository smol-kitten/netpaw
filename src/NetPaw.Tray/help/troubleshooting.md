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

## Something went wrong — where do I look?
`%APPDATA%\NetPaw\netpaw.log` has every command NetPaw ran with its output. *Open log* is in the tray menu.
