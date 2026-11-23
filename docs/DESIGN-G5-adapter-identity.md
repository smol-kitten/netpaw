# G5 design: adapters by identity, aliases for display (protocol v2)

Design page for PLAN-improvements-25 step 17. Reviewed by the orchestrator (m-10073, GO-WITH-CHANGES);
the three review changes are folded in below. Code (18a) may start.

## Goal
A change request names the adapter by a stable identity. A renamed or swapped alias can no longer
redirect an apply to a different NIC.

## Given state (measured on main, 2026-11-03)
- `ServiceRequest` (`Ipc/ServiceProtocol.cs`) is protocol **v1**: `Adapter` is the Windows alias
  ("Ethernet 2"), null means the work adapter. The service refuses any other `V`.
- `AdapterInfo` already carries `Id` (`NetworkInterface.Id`, the `{GUID}` NetCfgInstanceId), `Index`
  and `Mac`. Nothing on the wire uses `Id` today.
- `NetPawService.PlanProfile` resolves a profile's adapter by `AdapterMac` first (vSwitch uplinks
  excluded), then by alias, then throws. `ResolveAdapter(name)` is alias-only.
- `ApplyPlanner` writes the alias into every netsh step (`name="Ethernet 2"`). netsh also accepts the
  interface index in `name=`.
- `RevertLastApply` compares the snapshot's MAC with the live adapter and refuses on mismatch (0.16).
- The threat model trusts local admins. A Network Configuration Operator (NCO tier) may rename an
  adapter (`netsh interface set interface name=`). An alias swap between the tray's listing and the
  apply is the measured gap (PLAN-v1.0 G5, exit: "a renamed alias no longer redirects an apply").
- Standing rules: security-model PRs are reviewed and merged by the orchestrator. No pve work here.

## Requirements
- R1. Every change intent that touches an adapter carries a stable identity. The service resolves it
  against the live list. It never trusts the alias alone when an identity is present.
- R2. A v1 client may still ask Status and List, so an old tray or CLI can print "upgrade the client".
  A v1 change intent is refused. The MSI ships all three binaries together; only a stale portable zip
  breaks, and it breaks loudly instead of keeping the alias gap open for a release.
- R3. User profiles bind to the identity. Managed (fleet) profiles keep MAC/alias addressing: a GUID
  differs per host, so a profile deployed to many machines cannot carry one. Existing files still load.
- R4. The alias is shown everywhere a human reads: menus, CLI output, audit lines, plan preview.
- R5. A mismatch between identity and MAC refuses the request. It never falls back to the alias.
- R6. No new privilege, no new file, no new pipe field that reaches a command line.

## Options considered
- A. MAC only. Rejected: Hyper-V gives the management vNIC the uplink's MAC (t-dd3d27); two live adapters
  can share one MAC, and a MAC can be changed by an admin.
- B. Interface index only. Rejected: the index is reassigned on driver reinstall and on some sleep
  cycles; it is not stable across boots.
- C. NetCfgInstanceId (GUID) as the key, MAC as a cross-check, alias for display. Chosen: the GUID is
  stable for the lifetime of the adapter binding, unique per adapter, already in `AdapterInfo.Id`, and
  is what Windows itself keys the interface registry on.
- D. GUID in netsh steps (`name={GUID}`). Rejected: netsh wants the alias or the index in `name=`.
  The steps use the index (see below); the preview and the audit show the alias.

## Chosen approach
Add `AdapterId` (GUID string) and `AdapterMac` to `ServiceRequest` and `Profile`, bump the protocol to
v2, and make identity the primary key in the one resolver both paths share. The service refuses v1
change intents and keeps v1 Status/List. The alias becomes display-only.

## Wire and model changes
- `ServiceProtocol.Version = 2`. `ServiceRequest` gains `string? AdapterId` and `string? AdapterMac`.
  `Adapter` (alias) stays as a display hint. `ServiceStatus.ProtocolVersion` reports 2.
- Parsing: `V == 2` → `AdapterId` is required for every adapter-bound change intent
  (ApplyProfile, Dhcp, Reach, SetMtu, Reset, AddRoute, DeleteRoute, Listen, RevertLastApply);
  Status and List intents need none. `V == 1` → Status and List answered as today; every change intent
  is refused with "protocol v1 is no longer accepted for changes: upgrade the client" (audited).
  `V` other → refused as today.
- `Profile` gains `AdapterId` (JSON `adapterId`), written by the editor for user profiles only. `Adapter`
  and `AdapterMac` stay. Managed profiles in `profiles.d` never carry `adapterId` (the loader ignores
  one if present and logs it). Old files load unchanged (missing field = null).
- Resolver (one function, `AdapterSelector.Resolve(id, mac, alias, adapters)`):
  1. `id` given → the live adapter with that `Id`, else **refuse** ("adapter {id} is not present").
     If `mac` is also given and differs from the live MAC → **refuse** (R5).
  2. no `id`, `mac` given → by MAC as today (vSwitch uplinks excluded, Hyper-V vNIC last).
  3. neither → by alias as today (legacy user profiles), logged.
- The plan is built from the resolved live `AdapterInfo`. Every netsh step addresses the adapter by
  `name=<Index>` (netsh accepts the interface index), taken from the same live `AdapterInfo`, so an
  alias rename between resolve and run cannot redirect a step. The preview and the audit line show the
  alias next to the index ("Ethernet 2 (#12)").
- `RevertLastApply` compares `Id` first, then MAC (the snapshot gains `Id`).

## Client changes
- Tray and CLI resolve `-a` input (alias, index, GUID or MAC) against their own adapter list and send
  `AdapterId` + `AdapterMac` + alias. A profile saved from the editor gets `AdapterId`.
- Display: alias everywhere, GUID only in `netpaw-cli adapters --json` and the audit line.
- `ProfileLint` warns on a **user** profile bound by alias only ("bind to the adapter in the editor").
  Managed profiles are exempt: MAC/alias is their intended addressing.

## Steps (each one PR, orchestrator review on 2 and 3)
1. This page (reviewed, m-10073).
2. 18a Core + service: `AdapterId`/`AdapterMac` on `ServiceRequest` and `Profile`, `Version = 2`, v1
   change intents refused, the shared resolver, netsh steps by index, snapshot `Id`. Tests in
   `IntentHandlerTests` and `PlannerTests` (below).
3. 18b Tray + CLI: send identity, show alias, editor binds by id, lint warning. `verify-windows` gains an
   apply by GUID and an apply by old alias after a rename.
4. Docs: `docs/policy.md` audit-line example, `docs/threat-model.md` "Defended" bullet and the
   scope-limits bullet removed, `ENTERPRISE.md`: managed profiles stay MAC/alias-bound (no `adapterId`,
   state why), CHANGELOG line "protocol v2; 0.16 and older clients must be upgraded for changes".

## Verification
- Unit: renamed alias + same id → resolves to the same NIC. Same alias on another NIC + id given →
  refused. Id present but MAC differs → refused. v1 Status → answered; v1 ApplyProfile → refused with
  the upgrade message. Plan steps carry `name=<Index>`, preview shows the alias. User profile without
  `adapterId` → MAC, then alias, with a lint warning; managed profile → no warning. 409 existing tests
  stay green.
- Harness (PLAN-G11a clone, `RESULT|` lines): `netsh interface set interface "Ethernet 2" newname="X"`
  then apply by the stored profile → the right NIC changes. Swap the aliases of net0 and net1 → the
  apply is refused and net0 (management) is untouched. 0.16 CLI against the v2 service → `status`
  works, `apply` prints the upgrade message.
- Exit (PLAN-v1.0 M3): "a renamed alias no longer redirects an apply", measured on the VM.

## Risks
- Hyper-V management vNIC shares the uplink's MAC: the MAC cross-check only runs when an `Id` is given,
  and the id disambiguates; MAC-only resolution keeps today's uplink exclusion.
- USB NICs get a new GUID when plugged into another port: the profile then shows "not present"; the
  editor offers rebinding (same message as today's MAC case).
- A stale 0.16 portable zip against the 0.17 service cannot apply anything until it is replaced. The
  refusal text says so; the MSI never produces this mix.
- A managed profile is still alias/MAC-addressed by design; the netsh-by-index step closes the rename
  window for it too, since the index comes from the resolved live adapter.

## Review decisions (orchestrator, m-10073)
1. v1 change intents refused from 0.17; v1 Status/List kept. No v1 audit marker, no 0.18 step.
2. netsh steps by `name=<Index>` from the start; alias in preview and audit.
3. Managed profiles keep MAC/alias; the alias lint is for user profiles only; ENTERPRISE.md to match.
