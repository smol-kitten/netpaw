# NetPaw

Static-IP profiles for network admins, in the tray. One hotkey opens the **quick panel**; type a profile, a device vendor, or an IP address and press Enter.

## The three things you do most
- **Switch profile** — `Ctrl+Alt+N`, type a few letters, Enter. The tray paw turns green (static) or blue (DHCP).
- **Reach a device** — type its IP (`192.168.88.1`). NetPaw finds a profile that covers it, knows the vendor default, or adds a temporary secondary address. See *Reach mode*.
- **Check the link** — `Ctrl+Alt+I` shows the network info card: link, address, gateway, DNS, intranet/internet as ✓ / ✗. Pin it with 📌.

## Colours
- Green paw: static profile active. Blue: DHCP. Orange: temporary addresses present. Grey: adapter down.
- A red dot on the paw means monitor mode found a problem (link down, no gateway, internet lost).

## Where things live
- Profiles: `%APPDATA%\NetPaw\profiles.json` — plain JSON, copy it to your next laptop.
- Managed profiles (from IT): `%ProgramData%\NetPaw\profiles.d\`.
- Log of every command NetPaw ran: `%APPDATA%\NetPaw\netpaw.log`.

Press **F1** anywhere in NetPaw to open this help on the current view. Alt+Left goes back.

## Main window
Prefer a window over the tray? **Open NetPaw window…** in the tray menu (or double-click the icon, or `--main`): Overview, Profiles, Tools, Map and Log with a left navigation. See *Main window*.

## Tools
Tray menu → **Tools** (or type the name in the panel): *Which switch port am I on? (LLDP/CDP)*, *Route table*, *Find routers*, *Trace route*, *Check a TCP port*, *Wake-on-LAN*, *Export diagnostics*. Wi-Fi signal, 802.1X, path MTU, per-server DNS and "X cannot reach Y" advice live on the info card (`Ctrl+Alt+I`).
