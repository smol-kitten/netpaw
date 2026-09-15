# NetPaw v0.7 plan — the "it just works on the next cable" release

## Goal
Close the seven lean gaps from `docs/GAP-ANALYSIS-2026-09.md` so a laptop that moves between desk,
dock, customer LAN and console cable needs no manual clicks for the common cases, and explains
itself when it does.

## Given state
- Repo `smol-kitten/netpaw`, main at v0.5.1 + branch `feat/arp-router` (network map, find-routers,
  OTLP/syslog export, VPN status; 120 tests). CI: Linux tests/zips + windows-latest MSI; security-scan
  (gitleaks/OSV/semgrep) green; dependabot auto-merge on.
- Planner is pure and tested; every change is a `Step` with a visible command line.
- Monitor loop runs every N s and on NetworkChange events; snapshot → state → advisories → incident log.
- Profiles bind to an adapter by name (or null = work adapter). AdapterInfo carries the MAC already.
- Win11 VM 150 exists for verification (currently held by the nightly vzdump).

## Requirements
R1. `ipconfig /flushdns` after every successful apply; a setting turns it off.
R2. A profile may remember its adapter's MAC; resolution order: MAC → name → work adapter. Renames and
    "Ethernet 3" docks keep working. The editor shows "bound to 00-11-…" next to the adapter.
R3. After an apply, NetPaw re-reads the adapter and compares to the profile (addresses, gateway, DHCP flag).
    A mismatch shows a warning with the differences and a *Reset adapter* action (disable → enable, two
    netsh steps) — never automatic.
R4. Advisory "address held by another adapter": when the work adapter has no lease / APIPA and another
    adapter (down or up) carries an address in the same subnet the DHCP server would offer, say so and
    offer *Release on <other>* (`ipconfig /release "<other>"`) or *Clear stale static*.
R5. Advisory "two default gateways": list both adapters with metric and which one carries the default route;
    one-click *Prefer <adapter>* sets interface metrics (10 / 50) via the planner.
R6. Auto-switch: a profile can carry a **fingerprint** `{adapterMac?, gatewayMac, dhcpServer?, subnet}`.
    When the monitor sees link-up + a gateway MAC that matches exactly one profile's fingerprint, and the
    profile is not already applied, it applies it — only if the profile has `autoSwitch: true`, only after
    the user confirmed the very first automatic switch for that profile, never while a manual apply runs,
    at most once per link-up. *Learn fingerprint from current network* is a button in the editor.
R7. Captive portal state: NCSI-style probe (`http://www.msftconnecttest.com/connecttest.txt` == "Microsoft Connect Test")
    when DNS resolves and internet pings work but HTTP content differs → state `CaptivePortal`, advice
    "open a browser". Off unless checks are on.
R8. Must NOT: no auto-switch without per-profile opt-in and first-run confirmation; no new network
    traffic unless checks are enabled (R7) or the user clicked; every new step visible in the plan preview.

## Options considered
- O1. Auto-switch by SSID/location/time like NetSetMan. Not chosen: Wi-Fi is out of scope and location
  rules are the bloat that made those tools heavy. Gateway MAC + DHCP server identifies a wired network
  better than anything else a console-cable engineer has.
- O2. Verify-after-apply by re-running netsh `show config`. Not chosen: we already have the adapter
  provider; comparing AdapterInfo to Profile is pure and testable (`ApplyPlanner.Matches` exists).
- O3. Bind profiles by adapter GUID instead of MAC. Not chosen: a dock/USB NIC gets a new GUID per port;
  the MAC is the stable thing an admin recognises.
- O4. Reset adapter via PowerShell `Restart-NetAdapter`. Chosen instead: `netsh interface set interface
  "<name>" disable/enable` — same effect, no PowerShell start-up, matches the rest of the plan.

## Chosen approach
Six small planner/advisor additions behind the existing pure interfaces, each a PR with tests, in the
ranked order. Auto-switch last, because it reuses the fingerprint code from find-routers/ARP and the
verify-after-apply feedback from R3.

## Steps
1. Planner: `Step.Ipconfig("/flushdns")` appended to static/DHCP plans when `Settings.FlushDns` (default true). Test. (R1)
2. Model: `Profile.AdapterMac`; `NetPawService.ResolveAdapter(profile)` tries MAC first. Capture/editor
   record the MAC; editor label. Tests for rename + dock scenarios. (R2)
3. `ApplyVerifier.Compare(profile, liveAdapter)` → list of differences; TrayApp runs it 2 s after a
   successful apply; warning status + *Reset adapter* action = `ApplyPlanner.PlanResetAdapter`. Tests. (R3)
4. Advisor: "held by another adapter" using all adapters; repair steps `PlanRelease(other)`. Tests. (R4)
5. Advisor: "two default gateways" + `PlanPreferAdapter(a, b)` (metrics 10/50). Card row + action. Tests. (R5)
6. Connectivity: captive-portal probe in `ConnectivityChecker` (only when checks are on and internet OK);
   state + advice. Test with a fake HTTP probe. (R7)
7. Auto-switch: `Fingerprint` record + `Learn()` from a snapshot + ARP; `AutoSwitcher.Decide(snapshot,
   gatewayMac, profiles)` pure; TrayApp confirmation dialog on first switch per profile; editor button.
   Tests: exact-match only, ambiguous → nothing, already-applied → nothing, manual-busy → nothing. (R6)
8. Help topics, README, VM verification (dock simulation = second NIC, cable pull, static-over-DHCP),
   tag v0.7.0.

## Verification
- `dotnet test`: ≥ 150 tests; each step adds its own.
- VM 150: (a) apply static then DHCP and confirm no second gateway remains; force the TechNet bug shape
  and confirm *Reset adapter* clears it; (b) give NIC A a static address, put NIC B on the same subnet,
  confirm the R4 advisory names NIC A; (c) two NICs with gateways → R5 advisory names the winner and
  *Prefer* flips the metrics (`Get-NetIPInterface`); (d) learn a fingerprint on NIC B, pull/replug the
  cable → auto-switch applies once after confirmation, logs one incident line; (e) flushdns line in the log.
- Size: framework-dependent zip stays under 1 MB.

## Risks
- Auto-switch fighting the user: mitigated by opt-in, one switch per link-up, never during a manual apply,
  and a visible "auto-switched to X — undo" status with an undo action.
- Gateway MAC collisions (same router model, same MAC? no — MACs are unique; but VRRP/HSRP virtual MACs
  repeat across sites): fingerprint also stores the DHCP server address and subnet; all present fields must match.
- Disable/enable an adapter cuts the user's own session on a remote box: the reset action is never
  automatic and warns when the target adapter carries the default route.
- Captive-portal probe adds one HTTP request per check round: only when checks are on; 3 s timeout.
