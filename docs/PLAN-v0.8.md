# Plan: NetPaw v0.8 "trust what you see" (with the v0.9 outline)

## Goal
NetPaw verifies every action against the live adapter, auto-switch does not misfire on
shared-looking networks, and the monitor watches every host-facing adapter. Ship it as v0.8.0 with more tests and no new dependencies.

## Given state
- Repo `smol-kitten/netpaw`, branch `main` after PR #21 (v0.7.1). 137 Core tests pass on Linux.
- `NetPawService.ApplyAndVerify` exists. Only the profile-apply path in `TrayApp.cs:498` uses it.
  DHCP (`TrayApp.cs:440`), undo (`:275`), repair (`:526`), reset (`:534`) and reach (`:451`) call
  `Execute` without verification.
- `ApplyVerifier.Compare` checks addresses, gateway, DNS. It ignores routes and metrics.
- `NetworkFingerprint` (`Model/Profile.cs:8`) is gateway MAC + subnet + optional DHCP server, all
  must match. VRRP/HSRP virtual MACs and `192.168.1.0/24` repeat across sites.
- The monitor loop and `Advisor` use one adapter. `AdapterInfo.Pick` chooses it.
- `LldpDecoder` / `CdpDecoder` return chassis id and port id; auto-switch does not use them.
- No update check exists. Group headers in `ListView` render white on the dark theme.
- Six test files define their own `AdapterInfo` builder.
- No standing rules for scope `netpaw`. Open task t-2d587c (runner2 lacks `zip`) does not touch this work.
- Ranked gap list: `docs/GAP-ANALYSIS-2026-09-16.md`.
- Auto-switch is off by default and the first automatic switch asks once. The fingerprint
  collision therefore only reaches users who opted in and confirmed.
- NetPaw collects no SSID/BSSID today (`NetworkInterfaceAdapterProvider.cs` only flags
  `Wireless80211`). `netsh wlan show interfaces` provides both.
- Git history keeps the old `ACCESS.md` (commit 110b5a0) with internal hosts. The operator
  accepted this on 2026-09-16 ("not that critical"). No history rewrite.

## Requirements
- R1 Every plan the Tray or CLI executes reports the diff when Windows keeps the old config.
- R2 The verifier compares static routes and the interface metric.
- R3 Auto-switch scores several signals and applies only with a clear margin. Virtual-router MACs
  alone never match.
- R4 The monitor watches all host-facing adapters. The info card lists one row per adapter.
- R5 An opt-in update check reads the GitHub latest release and shows one toast. It never downloads.
  Policy key `AllowUpdateCheck` disables it.
- R6 `ListView` group headers use the dark theme.
- R7 The Tray shows the plan before it applies a profile when the user holds Shift or clicks "Show plan".
- R8 One shared test fixture builds `AdapterInfo`. `Pick` has tests for dock + Wi-Fi + vSwitch + VPN.
- Must not: add NuGet packages to Core or Tray; change the profile JSON shape for existing fields;
  auto-apply a profile on a fingerprint score below the margin; contact GitHub when the policy
  or the setting says no.

## Options considered
- A. Full IPv6 dual-stack in v0.8. Rejected for v0.8: it touches model, planner, verifier, advisor,
  reach and CLI at once, cost 4, with no verification base yet. It becomes v0.9 (outline below).
- B. Fingerprint by LLDP only. Rejected: the QEMU lab drops multicast and many access ports do not
  send LLDP. LLDP becomes one weighted signal instead.
- C. One monitor per adapter. Rejected: N pings per tick and N toasts. One loop over a list is enough.
- D. Update check through the MSI (Windows Installer auto-update). Rejected: portable users get
  nothing. The GitHub releases API serves both.
- E. Chosen: verify everywhere + weighted fingerprint + multi-adapter monitor + small UX items.

## Chosen approach
Reuse `ApplyAndVerify` for every plan and extend the verifier, because the mechanism exists and
only call sites change. Replace the all-must-match fingerprint with a score so real signals
(LLDP chassis/port, DHCP server, DNS suffix) outweigh weak ones (virtual MAC, common subnet).
Move the monitor from one adapter to a list with the work adapter first.

## Steps
0. Hotfix for v0.7.x (`fix/fingerprint-vrrp`, ships as v0.7.2): `NetworkFingerprint.Matches`
   returns false when `GatewayMac` is a virtual-router MAC (VRRP `00:00:5e:00:01:xx`, HSRP
   `00:00:0c:07:ac:xx`, `00:00:0c:9f:fx:xx`) and the DHCP server is missing on either side.
   Test `VirtualRouterMacAloneNeverMatches`. Help topic `auto-switch.md` names the limit.
1. Add `RouteDiff` and `MetricDiff` to `ApplyVerifier.Compare` (`Planning/ApplyVerifier.cs`);
   routes compare destination and next hop. The metric compares only when the profile sets one.
2. Route `Execute` in `TrayApp.cs` through `_svc.ApplyAndVerify` for every plan. Keep `custom`
   for reach. Post diffs through the existing `LastVerifyDiffs` path.
3. Make the CLI `dhcp`, `reset`, `release`, `prefer`, `preset apply`, `reach` commands use
   `ApplyAndVerify` (`Cli/Program.cs`). Return 1 with diffs.
4. Replace `NetworkFingerprint` with a scored record: fields `GatewayMac`, `Subnet`, `DhcpServer`,
   `DnsSuffix`, `SwitchChassis`, `SwitchPort`, `NeighbourMacs` (top 3), `Ssid`, `Bssid`
   (wireless adapters only, read with `netsh wlan show interfaces`). Old JSON loads unchanged
   (new fields null). Add `Score(other)` with weights: chassis+port 5, DHCP server 3, DNS suffix 2,
   gateway MAC 3 (1 when the MAC is in a virtual-router range, see step 0), BSSID 4, SSID 2,
   subnet 1, neighbour overlap 1 each.
5. `AutoSwitcher.Decide` applies when the best score ≥ 5 and beats the runner-up by ≥ 3. `Learn`
   records LLDP data when the incident log holds a capture newer than 24 h.
6. Add `AdapterInfo.HostFacingAll(adapters)` and make the monitor loop
   (`Connectivity/Connectivity.cs` + `TrayApp` monitor tick) iterate it, work adapter first.
   `Advisor.Advise` takes the adapter as a parameter. Findings carry `AdapterName`.
7. `InfoToast.cs`: one row per adapter (name, address, state, advice count); the existing detail
   rows show the selected adapter only.
8. Add `Updates/UpdateChecker.cs` in Core: `GET https://api.github.com/repos/smol-kitten/netpaw/releases/latest`,
   compare `tag_name` with the assembly version, cache result 24 h in settings, honour
   `AllowUpdateCheck` policy and `Settings.UpdateCheck` (default on). Tray: one toast with a link.
   ADMX/ADML/Intune/ENTERPRISE.md get the key.
9. Owner-draw `ListView` group headers in `Theme.cs` (`LVM_SETGROUPINFO` with dark colours, fall
   back to flat lists when the API fails).
10. `PlanPreviewForm` opens before apply when Shift is held or from a "Show plan" link in the
    confirmation toast (`TrayApp.ApplyProfileVerified`).
11. Move the `AdapterInfo` builders into `tests/NetPaw.Core.Tests/Fixtures/Adapters.cs`. Add
    `Pick` tests: dock + Wi-Fi, vSwitch uplink + vEthernet, VPN up, all down.
12. Add tests: verifier routes/metric, fingerprint scores (VRRP collision, LLDP tie-break,
    old-JSON load), multi-adapter advisor, update-checker parsing and policy gate.
13. Bump `NetPaw.Tray.csproj` to 0.8.0, update README (info card rows, update check, Shift-to-preview),
    help topics `auto-switch.md` and `settings.md`.
14. PR per milestone, each with its acceptance criteria:
    - `fix/fingerprint-vrrp` (step 0): `VirtualRouterMacAloneNeverMatches` passes, v0.7.2 tagged.
    - `feat/verify-everywhere` (1–3, 11): tests `VerifierReportsMissingRoute`,
      `VerifierReportsInterfaceMetric`, `PickPrefersDockOverWifi`, `PickSkipsVSwitchUplink`,
      `PickIgnoresVpnTunnel`; CLI `dhcp` returns 1 with a diff when the fake adapter keeps static.
    - `feat/fingerprint-score` (4–5): tests `ScoreVrrpCollisionStaysBelowMargin`,
      `ScoreLldpBreaksTie`, `ScoreLoadsV07Json`, `DecideNeedsMarginOfThree`.
    - `feat/multi-adapter` (6–7): test `AdviseNamesTheAdapterWithoutGateway`; info card screenshot.
    - `feat/update-check` (8): tests `UpdateCheckerParsesTag`, `UpdateCheckerHonoursPolicy`,
      `UpdateCheckerCachesForADay`; ADMX validates in `gpmc`-style schema check in CI.
    - `feat/ux-0.8` (9–10, 13): screenshots of dark headers + plan preview in README.

## Verification
- `dotnet test tests/NetPaw.Core.Tests` ≥ 160 tests, 0 failed, on Linux CI. The named tests above exist.
- Windows test VM: apply a profile with a route to an unreachable next hop → the info card
  shows the route diff; `netpaw-cli dhcp` on "Ethernet 2" then `verify` returns 0.
- Fingerprint: two saved profiles, both learned on `192.168.1.0/24` with gateway `00:00:5e:00:01:01`
  and different DHCP servers. Link-up does not auto-apply. Inject LLDP frames
  (the `lldp-inject2` helper on the hypervisor) for one profile. That profile applies.
- Multi-adapter: enable "Ethernet 2" and "Ethernet" in the test VM → info card shows two rows, advice on
  the NIC without gateway names that NIC.
- Update check: set the assembly version to 0.0.1 in a test build. The toast appears once. With
  `AllowUpdateCheck=0` the debug log shows no request.
- Screenshots of the new info card and dark group headers in README (fleet rule).

## Risks
- Some Windows builds ignore `LVM_SETGROUPINFO` colours. Then use flat lists (step 9).
- LLDP data in the fingerprint goes stale after a switch replacement. An LLDP mismatch lowers the
  score but never blocks. The score margin decides.
- Multi-adapter monitoring doubles ping traffic on dual-homed hosts. Keep one probe set per tick,
  keep the 30 s default interval, skip adapters without a gateway.
- GitHub API rate limit (60/h unauthenticated). Make one request per 24 h, cache it, keep failures silent.
- v0.7.x profiles carry the old fingerprint shape. New fields load as null. The score still works on
  the old three signals. A test loads a v0.7 JSON sample.

## v0.9 outline: IPv6 (not planned in detail yet)
- Model: `Addresses6`, `Gateway6`, `Dns6` on `Profile`; `IpAddr` gains a family field.
- Planner: `netsh interface ipv6 set/add address|route|dnsservers`; reset semantics differ
  (no `source=static` wipe, so delete addresses explicitly).
- Verifier, advisor ("RA-only, no v4 lease" becomes a finding instead of "no address"), info card, CLI.
- Verify on the Windows test VM with radvd on the hypervisor bridge before touching reach-mode for v6.
