# Settings

- **Work adapter** — the adapter NetPaw acts on when a profile names none. *(auto-detect)* = first physical adapter with a gateway.
- **Panel hotkey** / **Info hotkey** — global shortcuts. Need a modifier.
- **Confirm** — show the command plan (exact `netsh` lines) before every change.
- **Notifications** — balloon tips for results when the panel is not showing.
- **Reach mode** — add a secondary (default) or replace the primary; the default prefix for unknown targets.
- **Updates** — once a day NetPaw asks the GitHub releases API for the newest tag and shows one balloon per newer release; a click opens the release page. It never downloads or installs anything. Off when unticked or when policy `AllowUpdateCheck` is 0 (managed fleets ship updates through Intune/GPO). *Check now* asks immediately.
- **Startup** — run at logon through an elevated scheduled task (no UAC prompt each login).
- **Connectivity** — reachability checks, interval, intranet/internet targets, DNS check host, monitor mode, sticky alert. See *Network info & monitor*.
- **Repositories** — online packs: add, remove, sync. See *Presets & repositories*.

Greyed values are pinned by your organisation's policy (see *Enterprise*).

Files: *Open config folder* shows `profiles.json`, `settings.json`, `presets.json`, `state.json`, `netpaw.log`, `cache\`.
