# Plan: NetPaw v0.10 "quiet and solid"

## Goal
v0.10 makes NetPaw smaller, faster to start, quieter on screen and harder to break: a configurable
info card (never / on issue / always per row), a self-watching check loop with adaptive interval,
bounded external calls everywhere, error reports with context in the telemetry build, and a
standalone build under 60 MB. No new features that add rows.

## Given state
- `main` at v0.9.0, 191 Core tests. The operator deferred IPv6 (2026-09-16). a later step may add
  IPv6 *diagnostics* only (address assigned, DNS, internet), not profiles.
- Standalone `NetPaw.exe` is 107 MB (`PublishSingleFile`, no compression, no trimming. WinForms
  cannot trim). Portable build 1.2 MB + runtime. No `ReadyToRun`.
- Every check tick (30 s) spawns processes: `netsh interface ipv4 show route` (route table, v0.9),
  `netsh wlan show interfaces` for wireless adapters, `netsh lan` for wired ports without address,
  plus `GetAdapters()` five times per tick path in `TrayApp`. `StepRunner` kills a step after 30 s.
- The info card has 15 row kinds (`InfoToast.cs`). each one renders whenever it has data. Sticky alert and
  pin are global switches. Secondary adapters, VPN, switch, MTU, Wi-Fi rows have no off switch.
- Telemetry build reports crashes from `Application.ThreadException`, `UnhandledException` and the
  apply continuation. Background `Task.Run` continuations in `RunCheck`, `DiscoverSwitch`,
  `AutoSwitch`, `CheckForUpdates`, `ExportDiagnostics` log to `netpaw.log` only. no context (last
  log lines, snapshot state) travels with a report.
- `netpaw.log` rotates at 5 MB. the Tray writes 18 kinds of lines, the service 3.
- Operator-authorized README directive m-2143/m-2145 (pawkit readme_kit / readmecheck) is open and
  independent of code.
- No standing rules for scope `netpaw`.

## Requirements
- R1 Info card rows have a visibility mode: **Never / On issue / Always**, per row kind, in
  *Settings → Info card*. Defaults keep today's look for the core rows (Link, Address, Gateway, DNS,
  Internet: Always) and quiet the rest (Wi-Fi, Path MTU, Also, VPN, Switch, Temporary, 802.1X,
  Intranet: On issue. Advice: On issue with Info-level hidden unless Always).
- R2 Pin behaviour is a mode too: card auto-pins **Never / On issue / Always**. today's
  `StickyAlerts` maps to *On issue*.
- R3 The check loop watches itself: a tick that overruns two intervals gets a log line, a cancel and a loop restart. the info card shows the tick age when it is older than 3 intervals.
- R4 Adaptive interval: 30 s while a problem is present or for 5 min after a change, else backs
  off to the configured maximum (default 120 s). link events still trigger an immediate tick.
- R5 Per tick at most one `netsh` spawn on a stable network: route table and Wi-Fi/802.1X readers
  run on link-up, address change and every 5th tick, not every tick. `GetAdapters()` is read once per tick.
- R6 Every external call has a bound: `StepRunner` 30 s stays. probes ≤ `TimeoutMs`. pktmon 120 s;
  DNS/HTTP/TCP already bounded. A bound that fires writes the command name to the log.
- R7 Telemetry build error reports carry context: the last 40 log lines (ring buffer), the current
  `NetState`, adapter name, and the operation name. Every background task goes through one
  `Guarded(name, work)` helper that logs, reports and never crashes the loop.
- R8 Standalone build ≤ 60 MB (`EnableCompressionInSingleFile`), startup unchanged or faster
  (`PublishReadyToRun` measured, kept only when the cold start gets faster).
- R9 Log lines carry a category prefix (`check`, `apply`, `switch`, `update`, …) and a
  *Settings → Log level* (errors / normal / verbose) gates the verbose ones.
- R10 README rebuilt per m-2143/m-2145 with the pawkit tooling: hub + docs sub-pages, verified
  claims (`readme-verify.py` + CI), ACCESS.md moved under docs/, stelint under 3/100 w.
- Must not: hide Error-severity advice under any mode. change profile/settings JSON shape for
  existing fields (new fields only). add dependencies. run probes when checks are off. grow the
  portable build past 2 MB.

## Options considered
- A. One global "compact / normal / verbose" switch: rejected: admins differ on which rows matter
  (dock users want *Also*, Wi-Fi users want signal). per-row modes cost the same to build.
- B. Trim the standalone with `PublishTrimmed`: rejected: WinForms does not support trimming. the
  compression flag gives ~50 % without risk.
- C. Move readers to a background service process: rejected: a second process to install and
  watch. the adaptive interval and the every-5th-tick rule remove the spawn cost without it.
- D. Chosen: per-row visibility modes + adaptive self-watching loop + guarded background tasks with
  context + compressed single file + log categories.

## Chosen approach
Add one `Visibility` enum and one settings map. the card asks `Show(kind, isIssue)` before every row,
so no row code changes shape. Wrap the loop and every background task in a guard that owns logging,
telemetry context and the watchdog. Cut process spawns by moving the netsh readers behind a
"stale?" rule. Compress the single file. Ship as v0.10.0 with the README rebuild as its own PR first.

## Steps
0. README directive (PR `docs/readme-kit`): `pawkit readme_kit build --template cli --write --fancy`,
   `pawkit readme_kit split --write`, `pawkit readmecheck extract --write --model nano`, review
   claims (drop c23, fix c5/c6, set the test count from `dotnet test` output), add
   `.github/workflows/readme-verify.yml` with a publish step (PR `ci/readme-verify`), move
   `ACCESS.md` to `docs/ACCESS.md`, `pawkit stelint README.md` < 3/100 w.
1. `Model/Settings.cs`: `enum Visibility { Never, OnIssue, Always }`, `InfoCardSettings`
   (`Dictionary<string, Visibility> Rows`, `Visibility AutoPin`), migration: `Checks.StickyAlerts`
   → `AutoPin = OnIssue` when the new block is absent.
2. `InfoToast.cs`: `bool Show(string kind, bool? ok, AdvisorySeverity? sev = null)`. every `Row(...)`
   call passes its kind. Error advice always shows. `AutoPin(problem)` reads the mode.
3. `SettingsForm.cs`: tab *Info card* with one row per kind (label + three radio buttons) and the
   auto-pin mode. *Reset to defaults*.
4. `TrayApp.cs`: `Guarded(string name, Func<Task> work)`: try/catch, `Store.Log($"{name}: …")`,
   `TelemetryHost.Error(ex, name, context)`. use it for `RunCheck`, `DiscoverSwitch`, `AutoSwitch`,
   `CheckForUpdates`, `ExportDiagnostics`, `Wake`, `CheckPort`, MTU probe.
5. `TelemetryHost.cs`: `LogRing` (last 40 lines from `Store.Log`), `Error(ex, op, context)` adds
   ring, `NetState`, adapter, version, uptime. Default build: ring only (no send).
6. `TrayApp.cs` loop: `_tickStarted`, `_tickCts`. a tick older than `2 × interval` is cancelled and
   logged (`check: watchdog cancelled tick after N s`). `Snapshot.Age` shown as a *Checks* row
   suffix when > 3 intervals. Adaptive interval: `NextInterval(state, lastChange, settings)` pure
   function in Core (`Connectivity/Cadence.cs`) with tests.
7. `TrayApp.cs` readers: `RouteTable.ReadAll`, `Wlan.Read`, `Dot1x.Read` only when `linkUp`,
   address changed (compare `Adapter.Addresses`), or `tick % 5 == 0`. cache the last result on the
   snapshot. `_adapters` read once at tick start and passed down (`Work` uses the cached list).
8. `Store/JsonStore.cs`: `Log(string category, string text, LogLevel level = Normal)`. keep the
   old `Log(text)` as `Log("app", text)`. `Settings.LogLevel`. Verbose lines: per-tick states,
   probe timings, reader results.
9. `NetPaw.Tray.csproj` / `NetPaw.Cli.csproj`: `EnableCompressionInSingleFile=true`. measure the
   standalone size and cold start on VM 150 with and without `PublishReadyToRun`. keep R2R only
   if start is faster.
10. `StepRunner.cs`: log the command name when the 30 s bound fires. `PktmonCapture` bound stays
    120 s. add the bound to `Dot1x.Read` / `Wlan.Read` (they use the runner: covered).
11. Help topics: `network-info.md` (visibility modes, tick age), `settings.md` (Info card tab, log
    level), `telemetry.md`/`docs/TELEMETRY.md` (what an error report contains). README bullets.
12. Tests: `VisibilityRulesHideAndShow`, `ErrorAdviceAlwaysShows`, `StickyAlertsMigratesToAutoPin`,
    `CadenceBacksOffWhenStable`, `CadenceReturnsToFastOnProblem`, `LogRingKeepsLast40`,
    `ErrorReportCarriesContext`, `ReadersRunEveryFifthTickOrOnChange`, `LogLevelGatesVerbose`,
    `GuardedNeverThrows`.
13. Version 0.10.0. PRs: `docs/readme-kit` + `ci/readme-verify` (step 0), `feat/card-visibility`
    (1–3), `feat/guarded-loop` (4–6), `perf/tick-spawns` (7–8), `build/slim` (9–10), `docs/v0.10`
    (11, 13).
14. VM 150: cold-start time before/after (Stopwatch in the log at first tick), process count per
    tick (`Get-Process netsh` sampled), card with *Never* on Also/VPN/Switch, sticky mode *Never*.

## Verification
- `dotnet test` ≥ 205 tests, 0 failed.
- Standalone zip ≤ 60 MB in the release assets. portable ≤ 2 MB.
- VM 150: `netpaw.log` shows one `netsh` per 5 ticks on a stable network (verbose level). watchdog
  line appears when `Ping` is stalled (simulate by pointing an intranet target at a black-hole
  address with a 30 s timeout).
- Info card with every optional row on *Never* shows exactly Link, Address, Gateway, DNS, Internet.
- Telemetry build (`-p:Telemetry=true`): a forced error (`netpaw-cli` cannot. use the Tray's
  hidden `--crash-test` flag added in step 4) produces a report with 40 log lines and the state.
- README: `python3 scripts/readme-verify.py` passes locally and in CI.

## Risks
- Visibility defaults hide something a user relied on → defaults keep all rows that existed before
  v0.8 on *Always*. the help topic lists the mapping. *Reset to defaults* is one click.
- Adaptive backoff delays noticing a problem → link/address events still fire immediately. the
  maximum is a setting. monitor mode forces 30 s.
- `EnableCompressionInSingleFile` slows first start (decompress to memory) → measured on the VM;
  drop it if cold start grows by more than 300 ms.
- The watchdog cancels a slow but healthy tick (20 DNS servers) → the bound is 2 × interval and the
  cancel goes to the log. the next tick runs normally.
- README tooling (pawkit) output needs review → dry-run first, review the claims by hand, review the PR
  before merge.
