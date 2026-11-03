# G5 design: adapters by identity, aliases for display (protocol v2)

Design page for PLAN-improvements-25 step 17. Orchestrator review before any code (steps 18a/18b).

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
- R2. A v1 client keeps working for one release. Its requests are audited as alias-addressed.
- R3. Profiles (user and managed) bind to the identity. Existing profile files still load.
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
- D. GUID in netsh steps (`name={GUID}`). Rejected for v2: netsh wants the alias or the index in
  `name=`; the plan stays human-readable with the live alias, resolved just before the step runs.

## Chosen approach
Add `AdapterId` (GUID string) and `AdapterMac` to `ServiceRequest` and `Profile`, bump the protocol to
v2, and make identity the primary key in the one resolver both paths share. The service accepts v1 for
one release and logs each v1 request as alias-addressed. The alias becomes display-only.

## Wire and model changes
- `ServiceProtocol.Version = 2`. `ServiceRequest` gains `string? AdapterId` and `string? AdapterMac`.
  `Adapter` (alias) stays as a display hint. `ServiceStatus.ProtocolVersion` reports 2.
- Parsing: `V == 2` → `AdapterId` is required for every adapter-bound change intent
  (ApplyProfile, Dhcp, Reach, SetMtu, Reset, AddRoute, DeleteRoute, Listen, RevertLastApply);
  Status and List intents need none. `V == 1` → accepted, alias resolution as today, audit line gets
  the marker `, v1 alias-addressed`. `V` other → refused as today.
- `Profile` gains `AdapterId` (JSON `adapterId`). `Adapter` and `AdapterMac` stay. Old files load
  unchanged (missing field = null).
- Resolver (one function, `AdapterSelector.Resolve(id, mac, alias, adapters)`):
  1. `id` given → the live adapter with that `Id`, else **refuse** ("adapter {id} is not present").
     If `mac` is also given and differs from the live MAC → **refuse** (R5).
  2. no `id`, `mac` given → by MAC as today (vSwitch uplinks excluded, Hyper-V vNIC last).
  3. neither → by alias as today (legacy profiles, v1 clients), logged.
- The plan is built from the resolved live `AdapterInfo` (its current alias, milliseconds before the
  steps run). The residual window is one netsh call; if a later measurement shows it matters, the
  steps switch to `name=<Index>` (netsh accepts it) without a protocol change.
- `RevertLastApply` compares `Id` first, then MAC (the snapshot gains `Id`).

## Client changes
- Tray and CLI resolve `-a` input (alias, index, GUID or MAC) against their own adapter list and send
  `AdapterId` + `AdapterMac` + alias. A profile saved from the editor gets `AdapterId`.
- Display: alias everywhere, GUID only in `netpaw-cli adapters --json` and the audit line.
- `ProfileLint` warns on a profile bound by alias only ("bind to the adapter in the editor").

## Steps (each one PR, orchestrator review on 2 and 3)
1. This page (review). Record the answer to the open question below.
2. 18a Core + service: `AdapterId`/`AdapterMac` on `ServiceRequest` and `Profile`, `Version = 2` with v1
   accepted, the shared resolver, the v1 audit marker, snapshot `Id`. Tests in `IntentHandlerTests` and
   `PlannerTests` (below).
3. 18b Tray + CLI: send identity, show alias, editor binds by id, lint warning. `verify-windows` gains an
   apply by GUID and an apply by old alias after a rename.
4. Docs: `docs/policy.md` audit-line example, `docs/threat-model.md` "Defended" bullet, `ENTERPRISE.md`
   managed-profile `adapterId` example, CHANGELOG line "protocol v2; v1 clients work until 0.18".
5. 0.18: drop v1 (one-line change, separate PR).

## Verification
- Unit: renamed alias + same id → resolves to the same NIC. Same alias on another NIC + id given →
  refused. Id present but MAC differs → refused. v1 request → applied, audit line carries the marker.
  Profile without `adapterId` → MAC, then alias, with a lint warning. 396 existing tests stay green.
- Harness (PLAN-G11a clone, `RESULT|` lines): `netsh interface set interface "Ethernet 2" newname="X"`
  then apply by the stored profile → the right NIC changes. Swap the aliases of net0 and net1 → the
  apply is refused and net0 (management) is untouched. v1 CLI from 0.17 against the v2 service → works.
- Exit (PLAN-v1.0 M3): "a renamed alias no longer redirects an apply", measured on the VM.

## Risks
- Hyper-V management vNIC shares the uplink's MAC: the MAC cross-check only runs when an `Id` is given,
  and the id disambiguates; MAC-only resolution keeps today's uplink exclusion.
- USB NICs get a new GUID when plugged into another port: the profile then shows "not present"; the
  editor offers rebinding (same message as today's MAC case).
- A v1 client against a v2 service keeps the alias gap open for one release; the audit marker makes
  every such request visible. Mitigation: ship tray, CLI and service together (one MSI), so v1 only
  exists for the portable zip of 0.16 and earlier.

## Open question (orchestrator)
- Should v1 be refused outright for change intents from 0.17 on (MSI ships all three binaries; only a
  stale portable zip would break), or accepted for one release as written here?
