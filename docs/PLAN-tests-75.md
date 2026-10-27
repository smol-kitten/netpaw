# NetPaw: test-suite improvement plan, ~75 items (2026-10-27)

## Goal
Raise the suite from "Core logic on Linux + 47 hosted-runner assertions" to a layered suite that proves
the 1.0 bar: unit, contract, security-as-code, Windows integration, disposable-VM e2e, and CI meta-tests.

## Given state (measured 2026-10-27)
- `tests/NetPaw.Core.Tests`: 269 methods, 360 cases, green, references Core only. Largest files:
  IntentHandler 35, ReachAndService 21, Connectivity 19. No coverage tooling in the csproj or CI.
- CLI (630-line Program.cs), tray (20 files) and service (5 files) have zero unit tests.
- `verify-windows` (ci.yml): 47 `throw` assertions on windows-latest: install, service, pipe ACL,
  squat, loopback, tiers, managed profiles, policy schema, upgrade, uninstall.
- Harness G11 not built yet (PLAN-G11a approved, blocked on the thin-pool cleanup).
- Known untested paths: Discovery, Arp parsing on real captures, Scan, Telemetry client, Hotkeys parser
  edge cases, Presets data integrity, ServiceFolder ACL, RegistryPolicySource, TokenInfo, PipeServer.

## Requirements
- Every item names its layer, its file, and its pass condition.
- Tests run where they can run: Linux for pure code, windows-latest for OS calls, the VM harness for
  network effects. No test needs VM150.
- No new test uploads artifacts (storage cap). No test needs a paid service.

## Options considered
- A. Coverage number first (80 %): rejected, it rewards tests on getters.
- B. Risk-first by layer (security and apply paths before UI text): chosen.
- C. Port everything to the VM harness: rejected, slow and blocked.

## Chosen approach
Six layers, each with its own runner, listed in the order they can land. Items 1–40 need no Windows.

## Steps

### A. Test infrastructure (Linux)
1. Add coverlet collector + `dotnet test --collect:"XPlat Code Coverage"`; publish the summary to the
   job summary (no artifact). Pass: a coverage number in every CI run.
2. Coverage floor on `NetPaw.Core/Ipc` and `Planning` at the current value; fail below it.
3. `tests/NetPaw.Cli.Tests` project; CLI refactored to `Run(argv, IO)` (improvement step 7).
4. `tests/NetPaw.Service.Tests` project (Windows-only tests marked `[WindowsFact]`, skipped elsewhere).
5. Shared fixtures: one `Fx` builder per model type, delete per-file copies (Fixtures.cs comment says
   they drifted). Pass: `grep -c "static .* Nic("` across tests = 1.
6. Test-name lint: every `[Fact]` name states behaviour (`Verb_subject_result`); CI greps for `Test1`.
7. Flake tracker: rerun once on failure, record the flake in the job summary; a flake twice in 7 days
   opens a task.
8. xunit analyzers at warning-as-error for the test project (3 warnings exist today).

### B. Core unit gaps (Linux)
9. ServiceProtocol: oversized line (> MaxLine) refused with a reason; exact boundary.
10. ServiceProtocol: `kind` as integer refused; unknown kind refused; v=2 refused with "protocol".
11. ServiceProtocol: every `ServiceStatus` field round-trips, null `Tier`/`LastApply` readable.
12. ProfileGuard: adapter names with each refused character; 256-byte boundary; Unicode homoglyphs.
13. ProfileGuard: 0.0.0.0/0, 255.255.255.255, multicast, loopback and APIPA addresses refused or warned
    as documented.
14. IpMath: property test (FsCheck) — Contains/Network/Broadcast agree with a reference implementation
    for 10k random pairs.
15. ApplyPlanner: every intent produces only System32 tools (SystemTool.Strict) — table-driven over all
    RequestKinds.
16. ApplyPlanner: DNS steps order (clear → primary → index n) and `validate=no` present on all.
17. Step.ChangesDns is true for every plan that touches DNS (table-driven).
18. PolicyTiers: Floors win over policy for every intent with a floor; the cache file is rewritten only
    on change (mtime unchanged on repeat).
19. PolicyTiers: a reason is logged once per change for three consecutive distinct reasons.
20. CallerPrivilege: deny-only + enabled on different SIDs; NETWORK present with any attribute.
21. IntentHandler: every RequestKind writes exactly one `tier decision` audit line (table-driven).
22. IntentHandler: Journaled writes `inflight.json` before the first step and deletes it after
    (FakeRunner asserts file presence mid-run).
23. IntentHandler: an interrupted journal is moved to `interrupted-*.json` on next start, not reapplied.
24. IntentHandler: `RevertLastApply` on a DHCP pre-state does not require `UserProfiles`.
25. IntentHandler: AppliesTo glob `*`/`?` matching table; MAC case-insensitivity.
26. MachineStore: reparse point, wrong owner, and handle-based owner check branches (Linux stand-ins
    for the predicates; the real check is item 55).
27. ProfileLint: a live address on the profile's own adapter after rename (name differs, MAC same).
28. AdapterSelector: Pick/Secondaries over a 6-adapter table (physical+Wi-Fi+vEthernet external+
    Default Switch+TAP+down) with expected order.
29. TagVSwitchUplinks: MAC shared with a non-physical adapter never tags an uplink.
30. Connectivity.Advisor: each advisory id appears at most once per snapshot; severity monotonic.
31. Reach: `/n` targets outside RFC1918 produce a warning, not a refusal; replacePrimary branches.
32. Scan: parser against three fixture outputs (empty, one host, 254 hosts) — timing and ordering.
33. Arp: parser against English and German `arp -a` fixtures (the log already shows German netsh).
34. Discovery/PktmonCapture: the LLDP group-address note is covered by a fixture with and without it.
35. Hotkeys: parser rejects modifier-only, accepts function keys, rejects reserved Win+L.
36. Presets: data test — every preset IP is valid, unique per vendor, note length bounded.
37. Repos/PackSigner: tampered pack (one byte) fails; expired key id fails; signature over the
    canonical form only.
38. Updates/MsiAuthenticode: a CMS with 1000 certificates finishes under 2 s (CVE-2026-84008 guard,
    Pkcs ≥ 10.0.13).
39. Updates/UpdateTrust: pinned-root mismatch, intermediate missing, timestamp missing — each refused
    with its own reason.
40. Telemetry client: payload has no adapter names or MACs unless opt-in; schema snapshot test.

### C. CLI and tray logic (Linux, after the refactors)
41. CLI: `service status` renders every Status field; `tier unknown` for an old service.
42. CLI: `revert` without a service fails with the documented message.
43. CLI: `-n` dry run never touches the pipe (fake pipe asserts zero connections).
44. CLI: exit codes table (0 ok, 1 advisory error, 5 pipe) over all commands.
45. CLI: `--json` output (feature #41) validates against a schema file in `docs/`.
46. Tray logic: `DetectMode` table over (service answers, elevated) → mode.
47. Tray logic: tooltip text ≤ 127 chars for the longest adapter name + tier + summary.
48. Tray logic: QuickPanel user-mode post-pass marks all 8 mutating items, none of the others.
49. Tray logic: "Revert last apply" item appears only in service mode with a LastApply.
50. Tray logic: auto-switch confirm flag resets when the fingerprint changes.

### D. Security as code (windows-latest, in `verify-windows` or Service.Tests)
51. Pipe ACL readback equals the exact allow/deny set (exists; keep as a named test, not a step).
52. Squat after start denied with UnauthorizedAccessException (exists; named).
53. SMB loopback refused by the computer-name check for user and admin (exists; named).
54. Filtered-token admin (UAC) is StandardUser: run `netpaw-cli` from a non-elevated admin shell and
    assert `tier StandardUser`.
55. Junction in `profiles.d` → MachineStore moves the folder aside and logs it.
56. Oversized JSON (70 KB) over the pipe → refused in < 1 s, service still answers Status.
57. 9 concurrent clients → all answered, the 9th waits, none refused (MaxConcurrent = 8).
58. Service token line after restart: `SeChangeNotifyPrivilege` only (exists; named).
59. A standard user cannot write `policy.cache.json` or `last-apply.json` (ACL test).
60. Unknown `PolicySchemaVersion` → defaults + Event Log reason (exists; named).
61. `sc qfailure` restart twice then stop (exists; named).
62. Impostor pipe: CLI refuses with exit 5 (exists; named).
63. Event Log source exists after install and is removed after uninstall.
64. Upgrade 0.14.0 → current MSI keeps profiles and settings (install old, install new, diff).
65. Uninstall leaves only the documented folders (G16 list) and nothing else under %ProgramData%.

### E. Disposable-VM e2e (G11 harness, after PLAN-G11a step 3)
66. Static apply + DHCP revert on the test NIC; management NIC untouched (the G2 pass as a script).
67. NETWORK deny from the peer host (t-1db04a): SMB open refused; no Status line served.
68. Reach mode onto the peer's subnet; temp address visible; ClearTemp removes it.
69. Route add/delete through the service; route table diff before/after equals the plan.
70. VLAN set on the test NIC (driver permitting); `RESULT|vlan|skipped:<reason>` when not.
71. Policy change via registry applied without restart (G9 full) within 5 s.
72. Revert after a half-done apply (kill the service mid-plan): pre-state restored, journal moved aside.
73. Auto-update from a local release mirror with a tampered MSI → refused; untampered → installed.
74. Kill -9 the harness driver after `qm clone` → next run's sweep destroys the orphan.

### F. CI meta-tests
75. A push to `release/**` uploads zero artifacts (query the run's artifacts API in the job itself).
76. A tag push builds the tag's commit (`github.sha` equals the tag target; assert in the build job).
77. Weekly scheduled run of the full suite on main with the flake tracker, no artifacts.

## Verification
- Each item lands with its test name in the PR body; CI green; the coverage number does not fall.
- Items 51–65 appear as named steps in the `verify-windows` job summary.
- Items 66–74 produce `RESULT|` lines in the nightly harness job.

## Risks
- The CLI/tray refactors (items 3, 46–50) are code changes to shipped paths: small PRs, one command or
  one function each, verify-windows covers the end-to-end.
- Hosted-runner time grows: group D runs only on main and tags, not on every PR.
- The harness slips: group E waits; A–D do not.
