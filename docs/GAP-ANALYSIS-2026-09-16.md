# Gap analysis — round 2 (2026-09-16)

Second pass after v0.7.0 shipped LLDP/CDP, ARP, auto-switch and scan modes. Method: read every
Core class against its callers, run the Tray on the 1080p test VM at 125 % scaling, diff the CLI
switch against the README, and run `/code-review high` across eight angles. Fixed items landed in
PR "audit round 2" (v0.7.1); the rest feed `PLAN-v0.8.md`.

## Fixed in v0.7.1

| # | Finding | Fix |
|---|---|---|
| 1 | `PktmonCapture` used sync `Process` calls: froze the UI, timeout was dead code, `etl2pcapng` is not a pktmon verb, component matched by alias (breaks on renamed adapters), concurrent captures clobbered one temp file | async runner with kill-on-timeout, converter verb discovered from `pktmon help`, component by MAC, single-flight semaphore, random temp names |
| 2 | `Borrow()` on a DHCP adapter replaced a working lease (find-routers / scan on a normal office port cost the user their connectivity) | refused unless `allowLeaseDrop`; GUI confirms, CLI needs `--force` |
| 3 | Auto-switch could race a manual apply (both hold netsh) | auto-switch takes `_busy` while deciding, re-checks after the 4 s delay |
| 4 | Profile apply reported success even when Windows silently kept the old address | `ApplyAndVerify` re-reads the adapter at +2.5 s and +4 s, reports diffs; CLI rc 1 |
| 5 | Info card "Reset" after a failed verify reset the *work* adapter, not the one that failed | `LastVerifyAdapter` |
| 6 | Captive-portal detection fired on blocked ports / 403 / corporate proxies | proxy-aware skip; only redirect or foreign body counts; errors are inconclusive |
| 7 | Dock-holder advisory offered "Release", which cannot run on a disconnected NIC | Reset |
| 8 | Pack signatures broke whenever `Profile` gained a field (hash over re-serialised model) | hash over the raw JSON node; `pack build` clears `RawEntries` so new entries are written |
| 9 | Logs (`log.txt`, `incidents.jsonl`) grew without bound | rotate at 5 MB |
| 10 | Settings dialog was 890 px tall — clipped on a 1080p laptop at 125 % | tabs (General / Connectivity / Repair / Repositories / Telemetry), 520 px |
| 11 | `ipconfig /flushdns` ran on every apply, even when DNS did not change | `ApplyPlan.ChangesDns`, flush appended once by the service |
| 12 | AutoSwitch / Scan / Capture had no policy gate | `AllowAutoSwitch`, `AllowScan`, `AllowCapture` (registry, ADMX, Intune script) |
| 13 | Fuzzy panel query "map" opened "Manage profiles" | action rows match by word |
| 14 | README lacked `show`, `release`, `reset`, `prefer`, `find-routers --force` | added |

## Open — ranked for v0.8

Scored 1–5 on *user pain* × *reach* (how many users hit it) ÷ *cost*.

| Rank | Gap | Evidence | Pain | Reach | Cost |
|---|---|---|---|---|---|
| 1 | **No IPv6 anywhere** — profiles, planner, verifier, advisor, reach all assume v4. `netsh interface ipv6` is a parallel command set; dual-stack sites get half a profile | model + planner audit; Windows default is dual-stack, RA-only sites show as "no address" | 4 | 3 | 4 |
| 2 | **Verify covers only profile apply** — DHCP, undo, repair and reset paths still report success blind | `TrayApp.Execute` call sites | 3 | 4 | 1 |
| 3 | **Single-adapter monitoring** — connectivity/advisor watch one "work adapter"; users with dock + Wi-Fi + vSwitch see only one | `Monitor` loop, `AdapterSelector.Pick` | 3 | 3 | 3 |
| 4 | **Fingerprint collisions** — auto-switch matches on gateway MAC + subnet + DHCP server; VRRP/HSRP virtual MACs (`00:00:5e:00:01:xx`) repeat across sites, `192.168.1.0/24` everywhere | `NetworkFingerprint`, review finding | 4 | 2 | 2 |
| 5 | **No update check** — users learn about releases from GitHub only; MSI users have no in-app hint | none exists | 2 | 5 | 1 |
| 6 | **LLDP/CDP not validated on real hardware** — QEMU e1000 drops the multicast; only unicast injection tested | VM 150 sessions | 3 | 3 | 1 (needs a lab port) |
| 7 | **Hard-coded English** — strings inline in Tray forms, no resource layer | grep | 2 | 2 | 3 |
| 8 | **`ListView` group headers ignore the dark theme** (white bars in Manage profiles / Network map) | screenshots | 2 | 4 | 2 |
| 9 | **Untested Core classes**: `PktmonCapture` (needs Windows), `RenewScheduler` timing, `AdapterSelector` with vSwitch + VPN + dock all present | test list | 3 | 2 | 2 |
| 10 | **Test fixture duplication** — six copies of an `AdapterInfo` builder across test files | review | 1 | 1 | 1 |
| 11 | **Route metrics / interface metric not verified** — `ApplyVerifier` compares addresses, gateway, DNS; a route that failed to add is invisible | verifier | 3 | 2 | 2 |
| 12 | **No "what changed" diff before apply** in the GUI — CLI `-n` prints the plan, the tray just applies | UX review | 2 | 3 | 1 |

Not gaps, decided: Npcap/SharpPcap stay out (licence + dependency size); WinUI 3 migration rejected
(startup time, packaging); Wi-Fi profile management out of scope (netsh wlan is a different product).
