# Plan: NetPaw v0.14 "no admin prompt"

v0.14 moves every network change into a Windows service, so the tray runs as the user at the screen
with no UAC prompt. Organisations get per-action policy tiers, settings lockdown and a time-limited
admin unlock at the desk. Source plan (operator 2026-10-06, MAGI approved):
`PLAN-netpaw-service.md` in the orchestrator docs; task t-17ac79.

## Problem (measured)

- `src/NetPaw.Tray/app.manifest` requests `requireAdministrator`. Every start prompts for UAC.
- With an over-the-shoulder UAC prompt (admin credentials typed for a standard user) the tray runs as
  the admin. Settings, `%APPDATA%` and autorun then belong to the wrong account. v0.13.2 (#72) guards
  against this, but it cannot fix it: a standard user still needs an admin for every start.
- Windows does not start elevated entries from the Run key at logon, so autorun only works through a
  scheduled task, and that only elevates admin accounts.

## Step 0: feasibility (done 2026-10-07)

| question | result |
|---|---|
| Can the MSI install a service without custom actions? | Yes. WiX 5 `ServiceInstall` + `ServiceControl` in the exe's component; standard MSI tables, no custom action. |
| Where do changes run today? | Everything goes through `IStepRunner` (`ProcessStepRunner`) via `NetPawService.Apply`/`ApplyAndVerify` in Core. That is the cut point. |
| Can the service accept the tray's plan? | **No, not as-is.** A `Step` is `FileName + Arguments`, a raw command line. Accepting steps would let any pipe client run any program as LocalSystem. The service must take a **typed request** and build the plan itself (below). |
| Is there a policy layer to extend? | Yes. `Policy` (HKLM) + `Capability` + `NetPawService.Allowed/Require` already gate 12 capabilities as booleans. Tiers extend that. |
| Name clash? | Core already has a class `NetPawService` (the shared tray/CLI facade). The new project is `NetPaw.Service` and the exe is `netpaw-svc.exe`; the Core class keeps its name. |
| Build on Linux? | The service is a .NET 10 worker (`Microsoft.Extensions.Hosting.WindowsServices`), cross-published like the CLI. Signing and verification are unchanged; the MSI stays on `windows-latest`. |

Still to prove in PR 1, on the Windows CI runner: the service installs, starts and stops, and a
standard user reaches the pipe. **PR 1 is the feasibility gate.** No later PR starts until it is
green. If it cannot be made green, the work stops and the result goes to the orchestrator.

Alternatives considered and rejected:
- An elevated scheduled task elevates admin accounts only, not standard users.
- A per-user service runs with the user's rights, and those cannot change network configuration.
- COM elevation (the elevation moniker) shows a UAC prompt for each action, which is the problem
  being fixed.

## Design

### Components

1. **`netpaw-svc.exe`**: LocalSystem, automatic start, installed by the MSI. The only component that
   changes network state: addresses, DHCP, routes, temporary addresses, adapter reset, DNS, MTU, pktmon.
   Recovery actions: restart after 5 s on the first and second failure, then no restart. The service
   sets them itself at start (`sc failure`), because the MSI's `ServiceConfigFailureActions` fails with
   error 1939 (access denied) for restart actions, measured in PR 1. If the service is stopped or missing, the tray falls back
   to user mode and the info card says "NetPaw service not running". It does not elevate itself.
2. **Tray**: `asInvoker`. Reads state and diagnoses as the user. Every change becomes a request to
   the service. Without the service (portable zip) it runs in **user mode**: info card, map and
   diagnostics work; apply buttons are disabled and say why.
3. **CLI**: uses the service when it runs. Otherwise, from an elevated prompt, it applies directly as
   today (portable and scripting use).

### IPC

- The service creates the pipe with `FirstPipeInstance`: if another process already holds the name
  (a squatter while the service was stopped), the service stops with an error instead of joining it.
  Clients talk only to the process that the Service Control Manager reports as the running NetPaw
  service (`GetNamedPipeServerProcessId` vs. `QueryServiceStatusEx`), so a squatter can neither read
  requests nor fake answers. At most 8 requests are served at once. (Review of #75.)
- One change runs at a time (a single lock across the apply and its settle). DeleteRoute, ResetAdapter
  and ClearTemp need an administrator or Network Configuration Operator caller until PR 4 adds the
  per-action tiers, so v0.14 never grants a standard user more than the elevated 0.13 tray did.
  Managed profiles (applied by id) are validated the same as sent profiles; a file planted under
  0.13.x survives the upgrade and is still owned by the user who wrote it. (Review of #76.)

- Named pipe `\\.\pipe\NetPaw`. ACL: SYSTEM + Administrators + Network Configuration Operators +
  INTERACTIVE. Policy `PipeUsers` can remove INTERACTIVE and add named groups instead.
- The service identifies the caller of **every** request from the pipe: session from
  `GetNamedPipeClientSessionId` (not PID → session, since a PID can be reused), user from the client
  token at impersonation level Identification, PID for the log only. It never
  impersonates for a change. Each request is judged against that caller: user, session, admin or
  not, and any unlock held by that session. An unlock in one session never applies to another.
- Several sessions (RDP, fast user switching) can talk to the service at the same time. A network
  change is machine-wide, so the service announces each applied change to every connected tray, and
  the event log names the session that made it.
- Messages: one JSON object per line, length-capped (64 KB), versioned (`"v": 1`).
- **Requests are intents, not steps:** `ApplyProfile{profileId | inline profile, adapter}`,
  `Dhcp{adapter}`, `Reach{target, adapter, replacePrimary}`, `ClearTemp`, `DeleteRoute{prefix, adapter}`,
  `ResetAdapter{adapter}`, `SetMtu{adapter, mtu}`, `Capture{adapter, seconds}`, `Unlock{minutes}`,
  `Status`. The service resolves the adapter, builds the plan with the same `ApplyPlanner`, checks
  policy, runs and verifies it, and returns the outcome (steps, results, verify diffs).
- An inline profile (user-made) is validated the same way as one loaded from disk: IP math, prefix
  range, adapter exists, no command characters in names.
- **Preview** stays local: the tray plans with the same code to show the commands. The service plans
  again from the request, so a tampered preview changes nothing.

### Policy tiers

- Each action gets a tier `Allow | RequireAdmin | Deny` in `HKLM\SOFTWARE\Policies\NetPaw\Actions`:
  SetStaticIp, Dhcp, Routes, TemporaryAddresses, AdapterReset, Dns, Mtu, Capture, FindRouters,
  ProfileEdit, ProfileImport, CommunityPacks.
- The existing boolean `Allow*` values map onto the tiers (`0` = Deny). Old policies keep working.
- The service enforces the tiers. The tray only reads them to grey out buttons; that copy is advisory.
- `netpaw-cli policy` prints the effective tier per action and where it came from.
- ADMX gets the new settings.

### Company profiles (`ManagedProfilesOnly`)

- "Company profile" = a profile from the managed source that exists today:
  `%ProgramData%\NetPaw\profiles.d\*.json`, writable only by admins (ENTERPRISE.md section 2).
- With `ManagedProfilesOnly=1` the service applies only profiles it loads itself from that folder,
  by id. Inline profiles, presets, reach and repository profiles are refused. The tray hides user
  profiles and shows managed ones only.
- A signed HTTPS profile feed (operator pick 5) is a later release and plugs into the same source.

### Settings lockdown (`LockSettings`)

- `LockSettings=1` makes the settings UI read-only, including autorun and repositories.
  `LockSettingsExempt` lists the keys that stay editable (for example `Hotkey`).

### Admin unlock at the desk

- "Unlock (admin)…" in the tray starts `netpaw-unlock.exe`: a separate small signed exe with
  `requireAdministrator`. UAC asks for admin credentials.
- The helper sends only `Unlock{minutes}` over the pipe. The service accepts it only when the caller
  is **an elevated admin token** **in the same session** as the tray that asked.
- The service records an unlock for that session ID until the time runs out (policy `UnlockMinutes`,
  default 15), or the session locks or logs off (`SessionChange` events). Under an active unlock,
  `RequireAdmin` actions are allowed for that session. No admin token or password is stored.
- Every unlock, use and expiry goes to the event log.

### Audit

- Event log source `NetPaw`, registered by the service itself at its first start (it runs as
  LocalSystem; this keeps the MSI free of the WiX util extension). Logged: every applied plan (user, session,
  action, adapter, result), every refusal (and its reason), and unlocks.
- The incident log also gets the applied plans.

### Autorun and migration

- Autorun: `HKCU\...\Run` of the real user, so the tray starts at logon without elevation. The #72
  scheduled task is removed on first start if it points at NetPaw.
- MSI upgrade: install and start the service, ship the tray `asInvoker`. The old per-user task is
  cleaned up by the tray (the MSI cannot see other users' tasks). On first start after the upgrade,
  one notice: "NetPaw no longer needs admin rights; autorun was moved to your account".
- Portable zips: the self-contained standalone zip ships **without** `netpaw-svc`, on purpose: a
  portable copy has no installer to register a service, so it runs in user mode or uses the elevated
  CLI. The framework-dependent zip carries `netpaw-svc.exe` only because it shares the signed folder
  with the MSI; it is not registered from the zip either.
- Rollback: uninstall v0.14, then install the v0.13.2 MSI. The MSI refuses a direct downgrade
  (`MajorUpgrade`), so the uninstall comes first. This goes into `signed-release.md` → Rollback.
- The tray checks the service with `Status` at start, before every change and every 60 s. If the
  service stops while the tray runs, the tray switches to user mode and shows a balloon once.

## PR sequence

Each PR is green on CI, including the Windows job, before the next starts. PRs 1, 2, 4 and 5 touch
the security boundary (MSI service install, pipe and IPC, policy enforcement, unlock). Each of them
gets an independent review through the orchestrator before merge (orchestrator m-4055). Each step
is reported on the bus when it merges.

1. **Service skeleton + MSI**: `NetPaw.Service` project, pipe server with the ACL, `Status` request
   only, `ServiceInstall`/`ServiceControl`, event-log source. Windows CI: the service installs and
   starts, `Status` answers, a non-member is denied.
2. **Intents + client**: request/response types in Core, `ServiceClient`, the service runs
   `ApplyProfile`/`Dhcp`/`Reach`/`ClearTemp`/routes/reset/MTU/capture through the existing planner.
   Unit tests: tampered or invalid requests are refused.
3. **Tray `asInvoker` + user mode + HKCU autorun + migration notice.** CLI uses the client.
   Windows CI: a standard user applies a profile through the service; autorun is in the user's hive.
4. **Policy tiers + ManagedProfilesOnly + LockSettings + `netpaw-cli policy` + ADMX.** Tests for
   each tier and action.
5. **Admin unlock helper + session expiry + audit.** Tests for expiry, wrong session and a
   non-elevated caller.
6. **Docs (ENTERPRISE.md, README, help) + release v0.14.0** through the signed pipeline.

## Not in v0.14 (own releases later)

NextFree/AnyFree address modes (t-1d8877), finding mute + NotifyMode (t-081280), SNMPv3 agent
(t-be0066), installer count (t-d931d7), helpdesk upload (pick 4), signed profile feed (pick 5), DHCP
option 125/43 discovery (pick 8). No LLDP sender (operator decision 2026-10-07).

All of these build on the v0.14 pipe and policy tiers.

## Review reconciliation

Second-model critique (three rounds) and MAGI (APPROVE, unanimous, 68), 2026-10-07:

- **Orchestrator gates (rebut).** This is desktop software; there is no fleet heartbeat or feature
  flag to wire in. The gates are on the bus: scope approved (m-4055), step 0 approved (m-4057),
  an independent review of PRs 1, 2, 4 and 5 before merge, and no release tag without the
  orchestrator's OK. A policy kill switch already exists in effect: `Deny` on every action tier.
- **Caller vs. tray session (rebut, clarified).** There is no separate "tray session" to match. The
  pipe caller is the requester. Its user, session and elevation are read from the pipe for every
  request (IPC section). On a shared machine, any interactive user that policy allows may change the
  machine-wide network state. This is the same model as Network Configuration Operators, and
  `PipeUsers` narrows it.
- **Fallback if PR 1 fails (rebut).** The rejected alternatives above do not solve the problem.
  Stopping and reporting is the honest outcome, and v0.13.2 stays the current release.
- **Service crash during a change (adopt).** Before it runs a plan, the service writes it to
  `%ProgramData%\NetPaw\service\inflight.json`, and deletes the file when the plan is done. At start,
  if the file exists, the service logs "interrupted apply" and keeps the plan. The tray then offers
  *Reset adapter* or *Apply again*. No change is retried automatically.
- **Tray falls back to an elevated direct apply (rebut).** That brings back the UAC prompt and the
  wrong-account problem.

## PR 3 design: tray asInvoker + user mode + autorun migration

The tray stops being `requireAdministrator`. Every change then goes through the service, because an
`asInvoker` process cannot run netsh/route changes itself. This must land as one change: a manifest
flip without the rerouting would leave the tray unable to apply anything.

- **Change channel.** The tray talks to the network through one seam with three modes:
  - *service* — the service is running: the tray sends intents (`ServiceChange`) and shows the apply
    report (steps, warnings, verify diffs) exactly as today.
  - *elevated* — no service but the process is elevated (portable zip from an admin prompt): the
    current direct `NetPawService` path.
  - *user* — neither: diagnostics, info card and map work; every apply button is disabled with
    "needs the NetPaw service (install the MSI) or an elevated run".
  Mode is chosen at start and re-checked with `Status` before each change and every 60 s.
- **Intents cover every change** the tray makes (ApplyProfile, Dhcp, Reach, ClearTemp, DeleteRoute,
  ResetAdapter, Renew, Release, Prefer, SetMtu). Capture stays local (it only reads and writes the
  user's own profile file). The destructive/reconfiguring intents need an admin/NCO caller until the
  PR 4 tiers, so in user mode a standard user sees those disabled.
- **Autorun → HKCU Run.** `asInvoker` means the Run key starts the tray at logon with no prompt, so
  the scheduled task is gone. Migration at first start: delete the old `NetPaw` scheduled task if it
  points at NetPaw, and if `StartOnLogon` is set, write the HKCU Run value for the real user. One
  notice: "NetPaw no longer needs admin rights; autorun now runs in your account."
- **CLI** uses the service when present, else the elevated-direct path (portable/scripting).
- **verify-windows:** a standard user applies a managed profile through the service via the tray's
  code path, and the autorun entry lands in that user's hive, not an admin's.

## Note for the PR 3 body (merge history)

PR 3 carries a correctness fix for a latent defect introduced in #76: the privileged-intent gate used
`CheckTokenMembership`, which needs an impersonation-level token, so it marked a real elevated admin as a
non-member and refused DeleteRoute/ResetAdapter/ClearTemp (and the PR 3 Release/Prefer/SetMtu) for
everyone. It was latent because nothing drove those intents over the pipe until PR 3 routes the tray
through the service. Fixed by reading TOKEN_GROUPS and requiring the Administrators/NCO SID enabled and
not deny-only (`CallerPrivilege`), found on the pawsktop Win11 VM.

## Review reconciliation — recurring points (rounds 2–10)

The second-model review raises the same four points each round. They are settled here; no further change.

- **Orchestrator abort/rollback gates (rebut).** This is desktop software, not a fleet service: there is
  no runtime the orchestrator can gate. The real, enforceable gates are: each security-relevant PR merges
  only after the orchestrator's independent review returns GO (done for #75, #76; PR 3 pending); no `v*`
  tag without the orchestrator's OK, and the signed pipeline refuses to release if any verify job fails;
  the policy kill switch is `Deny` on every action tier; rollback is uninstall + install the prior MSI
  (`MajorUpgrade` blocks a direct downgrade). Nothing more to invent.
- **Service-crash recovery (adopt — already specified, made explicit).** The tray calls `Status` at start,
  before each change, and every 60 s; a missed answer switches it to user mode and shows one balloon. An
  apply cut off by a crash is recorded in the inflight journal and surfaced **once** at the next service
  start (not re-prompted), with *Reset adapter* / *Apply again*. No automatic retry.
- **Caller mapping for RDP / fast user switching / impersonation failure (adopt — clarified).**
  `GetNamedPipeClientSessionId` returns the Windows session for every logon kind, including RDP and fast
  user switching; the user comes from the client token read inside `RunAsClient`. If either read fails,
  `Caller.Of` throws and the request is refused with a reason — it never falls back to the service's own
  identity. An unlock is keyed to the session, so it never crosses sessions.
- **The #76 `CheckTokenMembership` defect is unverified/untested (rebut — it is both).** It was reproduced
  on the pawsktop Win11 VM 150: an interactive elevated admin's `DeleteRoute` was refused as non-privileged.
  The fix (`CallerPrivilege`, reading TOKEN_GROUPS and requiring the SID enabled and not deny-only) is
  covered by 7 unit tests (enabled / deny-only / enabled+deny-only / present-not-enabled / standard user /
  none / NCO), and the end-to-end path was re-run on the VM (admin `DeleteRoute` executes). The token read
  itself is Windows-only and stays VM-validated, not unit-tested, by design.
