# NetPaw — gap analysis (2026-09-16)

Scope: what Windows network admins complain about when they switch networks, measured against
NetPaw 0.6 (this branch). Sources: NetSetMan support forum, ElevenForum/TenForums threads, Microsoft
Q&A and the archived TechNet static-IP bug page, r/sysadmin-style tool roundups. Each row says
whether the pain is real for *our* user (field engineer with a laptop and a bag of gear), what we
have, and whether closing it is lean or bloat.

| # | pain point (evidence) | NetPaw today | verdict |
|---|---|---|---|
| 1 | **Auto-switch profile when the network is recognised** (NetSetMan's headline feature; users ask for "connected to SSID A → profile A", gateway-MAC and ping recognition) | Manual only. We now have the building blocks: ARP cache, gateway MAC, DHCP server, link events. | **Lean win.** Opt-in per profile, fingerprint = adapter + gateway MAC (+ DHCP-offered subnet). No SSID/time/location rules. |
| 2 | **Docking station: old adapter still holds the address → new adapter gets APIPA** (MS Q&A: Windows discards the DHCP offer because the disconnected NIC has the same deprecated IP) | Advisory says "No DHCP lease"; does not see the cause. | **Lean win.** Advisory: "another adapter (X, disconnected) holds 10.0.0.5 — release it". One-click fix: release/renew or clear the stale static. |
| 3 | **Static apply "took" but Windows shows DHCP / two gateways** (TechNet 31167: stale gateway after static over DHCP; fix = set static, set DHCP, disable/enable adapter) | We run netsh and trust exit codes. | **Lean win.** Verify-after-apply: re-read the adapter 2 s later, compare to the profile; on mismatch offer *Reset adapter* (disable/enable) as a repair step. |
| 4 | **Interface names differ (Win10 "WiFi" vs Win11 "Wi-Fi"), renamed adapters, docks with "Ethernet 3"** — scripts and profiles break | Profiles bind by adapter *name* or use the work adapter. | **Lean win.** Store the adapter MAC with the profile; resolve by MAC first, name second; editor shows both. |
| 5 | **Wi-Fi and Ethernet both up, traffic takes the wrong one** (metric articles everywhere) | Profile can set an interface metric; no diagnosis. | **Lean win.** Advisory when two adapters have default gateways: which one wins and why (metric); one-click "prefer this adapter". |
| 6 | **Stale DNS cache after switching networks** | Nothing. | **Trivial.** `ipconfig /flushdns` as a non-critical step after every apply (setting, default on). |
| 7 | "Object already exists" / ghost adapters after driver changes | Routes already delete-then-add; addresses are replaced by `set address`. | Covered enough. Show ghost adapters? **Bloat** for now. |
| 8 | Per-profile printers, drive mappings, hostname, proxy (NetSetMan Pro) | Nothing. | **Bloat** for a network tool — except **proxy**: admins on customer sites hit it. Defer to a later cycle; needs WinHTTP + user proxy semantics. |
| 9 | Wi-Fi profile management / SSID switching | Nothing. | **Out of scope** (Windows does it; netsh wlan is its own world). |
| 10 | Captive-portal detection ("online" but a login page eats everything) | Monitor says *DNS failing* or *online*. | **Small win**: NCSI probe (`msftconnecttest.com/connecttest.txt` must return the exact body) — one state `CaptivePortal`. |
| 11 | Timestamps of failures for tickets; who was on the wire | Incident log, ARP map, find-routers (this cycle). | Done. |
| 12 | "Which network is on this port?" before configuring | Find-routers (this cycle), scan. | Done. |

Not on any list but worth stating: everything NetPaw does must stay **explainable** (every change is
a visible netsh line) and **explicit** (no probing or switching without a clear opt-in). Auto-switch
(#1) is the first feature that acts without a click; it therefore ships opt-in per profile, with a
first-time confirmation, and never while a manual apply is pending.

## Ranked backlog (value ÷ size)
1. #6 flushdns step (1 h) · 2. #4 MAC-bound adapters (½ day) · 3. #3 verify-after-apply + reset (½ day)
4. #2 docking APIPA cause + release (½ day) · 5. #5 dual-gateway advisory (½ day) · 6. #1 auto-switch (1–2 days)
7. #10 captive portal (½ day) · deferred: proxy per profile.
