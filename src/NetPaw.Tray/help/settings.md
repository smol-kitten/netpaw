# Settings

- **Work adapter** — the adapter NetPaw acts on when a profile names none. *(auto-detect)* = first physical adapter with a gateway.
- **Tray click opens** — quick panel (default) or the main window; double-click always opens the window.
- **Panel hotkey** / **Info hotkey** — global shortcuts. Need a modifier.
- **Confirm** — show the command plan (exact `netsh` lines) before every change.
- **Notifications** — balloon tips for results when the panel is not showing.
- **Reach mode** — add a secondary (default) or replace the primary; the default prefix for unknown targets.
- **Updates** — opt-in, asked once on first start. When on, once a day NetPaw asks the GitHub releases API for the newest tag and shows one balloon per newer release; a click opens the release page. It never downloads or installs anything. Off when unticked or when policy `AllowUpdateCheck` is 0 (managed fleets ship updates through Intune/GPO). *Check now* asks immediately.
- **Log level** — *errors only*, *normal*, or *verbose* (every check round, reader results, the next interval). `netpaw.log` rotates at 5 MB either way.
- **Startup** — run at logon through an elevated scheduled task (no UAC prompt each login).
- **Info card** — one mode per row: *never* / *on issue* / *always*. "Issue" is the row's own verdict (link down, weak Wi-Fi, MTU below 1500, a DNS server ✗, another adapter with a problem, a warning in Advice). Errors in Advice always show. Hidden rows take no space. *Auto-pin* uses the same three modes: bring the card up and keep it there never / while a problem exists / always. *Reset to defaults* restores the quiet defaults (core rows always, the rest on issue).
- **Intent watcher** (Repair tab) — name the program waiting on an unanswered address and offer the profile that covers it. Reads the local TCP table only.
- **Connectivity** — reachability checks, interval, intranet/internet targets, DNS check host, monitor mode, sticky alert. See *Network info & monitor*.
- **Repositories** — online packs: add, remove, sync. See *Presets & repositories*.

Greyed values are pinned by your organisation's policy (see *Enterprise*).

Files: *Open config folder* shows `profiles.json`, `settings.json`, `presets.json`, `state.json`, `netpaw.log`, `cache\`.
