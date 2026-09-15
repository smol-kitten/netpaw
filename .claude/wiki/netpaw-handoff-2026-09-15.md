---
title: NetPaw — build-out and handoff (2026-09-15)
tags: [netpaw, handoff, release, windows, telemetry]
summary: 'One-day build of NetPaw v0.1.0→v0.5.0: what shipped, how it was verified (Win11 VM 150), the cross-surface telemetry registration, the traps worth knowing, and the open items handed to the orchestrator.'
---
# NetPaw — build-out and handoff (2026-09-15)

Deliverable for the orchestrator (m-2084). Repo `smol-kitten/netpaw`, MIT, .NET 10 (Core lib + WinForms tray + CLI).

## Shipped (all on main, all released)
| version | content |
|---|---|
| 0.1.0 | tray quick panel + hotkeys, profiles (primary + secondaries, gateway/DNS/routes/VLAN detection), reach-IP mode, 58 device presets, CLI twin |
| 0.2.0 | sectioned editor with inline errors, badges, managed profiles (`profiles.d`), registry policy + ADMX + six `Allow*` lockdowns, WiX MSI (built + install-tested on windows-latest), signed online packs + community pack, repositories UI |
| 0.3.0 | F1 help, network info card (pinnable), opt-in reachability checks, monitor mode (transition-only alerts with grace), sticky alert |
| 0.4.0 | DHCP/static advisory, bounded `ipconfig /renew` auto-repair, opt-in telemetry build variant (`-telemetry.msi`), `AllowTelemetry` policy |
| 0.5.0 | incident log (jsonl), scan-for-a-working-profile (first-working / rank-all, restore unless kept), planner fixes (stale-DHCP restore, route re-apply) |

104 unit tests (Core is OS-neutral). Every `netsh`/`ipconfig`/GUI path verified on VM 150 with real link_down/link_up on the spare NIC.

## Cross-surface action
Operator-authorized: `ProjectRegistry::register('netpaw', 'smol-kitten/netpaw', …)` via `docker exec` in the prod workflow-manager container. Token → GitHub secret `NETPAW_TELEMETRY_TOKEN` + local key store. Smoke: heartbeat/events/errors → 200. Workflow-manager owner informed (m-2074). See `ACCESS.md`.

## Traps found (each cost real time)
- `LibraryImport` needs explicit `…W` entry points; the failure surfaces lazily inside `WndProc` as a type-initializer exception.
- `NativeWindow.CreateHandle` failed in that session; a hidden `Form` is the robust message window.
- A launcher opened from a timer/tray click has no foreground rights → `AttachThreadInput` force-foreground.
- `netsh set address source=static` wipes every address (used deliberately as the reset); a static secondary cannot sit next to a DHCP lease.
- NDIS filter pseudo-NICs end in `-0000`; filter them.
- A DHCP plan must not trust a stale "already DHCP" snapshot (restore after a scan left a static config behind).
- `netsh add route` refuses an existing route → delete-then-add (`ExpectFailure` step).
- WiX 7 needs a paid OSMF EULA and Windows; WiX 5.0.2 on `windows-latest` is free. .NET BCL has no Ed25519 → packs use ECDSA P-256.
- Runner `/opt/runner2` has no `zip`.
- VM 150: Windows 42-day password expiry killed SSH + autologon; fixed (`PasswordNeverExpires`, Winlogon `DefaultPassword`). The QWERTZ key-injection helper swaps y/z.

## Open items (tasks filed)
- t-1f8cee — 150 % DPI check of the WinForms UI.
- t-2d587c — runner2 `zip` gap fleet-wide.
- t-356ded — handoff record. PR #1 (docs-refresh workflow) closed per directive: not docs-only, repo has no committed discovery copy.
- Community pack accuracy is best-effort; corrections by PR + re-sign (`ACCESS.md`).
