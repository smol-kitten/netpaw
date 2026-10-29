# Policy tiers (v0.14, PR 4)

The NetPaw service gates every change request on a **minimum caller tier**. A caller is one of, lowest to
highest: **StandardUser** → **NetworkConfigOperators** (the Windows *Network Configuration Operators* group) →
**Admin** (the *Administrators* group). The tier comes from the caller's access token read over the pipe (a
UAC-filtered admin reads as StandardUser), never from the request.

## Defaults (equal to pre-v0.14 behaviour)

| Intent | Default min tier | What it does |
|---|---|---|
| ApplyProfile | StandardUser | apply a profile |
| Dhcp | StandardUser | switch an adapter to DHCP |
| Reach | StandardUser | reach mode (temporary address) |
| Renew | StandardUser | renew a DHCP lease |
| ClearTemp | NetworkConfigOperators | remove temporary addresses |
| DeleteRoute | Admin (floor NetworkConfigOperators) | delete one route by hand (map tab / CLI). Routes that are part of a profile are applied and removed under ApplyProfile, so standard users keep their profiles |
| ResetAdapter | NetworkConfigOperators | reset (disable/enable) an adapter |
| Release | NetworkConfigOperators | release a DHCP lease |
| Prefer | NetworkConfigOperators | prefer an adapter (interface metric) |
| SetMtu | NetworkConfigOperators | set an adapter MTU |
| VirtualUnlock | NetworkConfigOperators | target a virtual (non-host-facing) adapter via `appliesTo` |
| InstallUpdate | Admin (floor NetworkConfigOperators) | update from CatWinget: the service resolves, downloads, verifies (sha256 and Authenticode) and installs its own file (0.17); capability `AllowCatWingetUpdate` turns the feature off for everyone |
| RevertLastApply | StandardUser | put the adapter of the last apply back to the state the service snapshotted just before it (0.16). Restores DHCP on/off, IPv4 addresses, the first gateway and DNS only — not metrics, DNS suffix, VLAN, routes, IPv6 or a "DHCP address + static DNS" mix. A non-host-facing adapter needs the `VirtualUnlock` tier; a static pre-state needs `AllowUserProfiles` |

`Status` is not tier-gated (it only reports that the service is present). Capability policies
(`AllowUserProfiles`, `AllowDhcp`, `AllowReach`, …) are **orthogonal**: they turn a feature on or off for
everyone; tiers decide **who**. A call must satisfy both.

## Overriding with policy

Set per-intent overrides under `HKLM\Software\Policies\NetPaw`, one string value per intent named
`Tier.<Intent>` = `StandardUser` | `NetworkConfigOperators` | `Admin` (GPO/Intune via the ADMX in
`deploy/gpo/`). An override may **tighten or loosen** an intent, within a hard floor:

- **VirtualUnlock floor = NetworkConfigOperators.** A policy may raise it to Admin but never lower it below NCO.
  (A managed, admin-defined profile may always target a virtual adapter it lists, independent of this.)
- Every other intent has an implicit floor of StandardUser.

## Schema version

An optional string value `PolicySchemaVersion` under the same key names the schema the policy was written
for. This build understands `1`. A missing value is treated as `1` — a deliberate compatibility exception, so
policies written before the marker existed stay valid. Any other value makes the whole policy invalid, so the
last-known-good policy (or, with none, the defaults) applies — a newer policy is never half-applied.

## Fail-closed behaviour

The last policy that read cleanly is cached to a SYSTEM/Administrators-only file in the service store. If the
policy later becomes unreadable or invalid (access denied, a bad tier value), the service uses that
**last-known-good** cache — so a tightening an admin applied is not silently lost — and writes a warn audit line.
On a first run with no cache, it falls back to the default table. This fallback never collapses to a blanket
deny — it reverts to the last-known-good or default tiers. (An administrator may still deliberately tighten an
intent, including Renew, up to Admin via policy; "never deny-all" is about the unreadable-policy fallback, not a
promise that any intent stays available once policy raises it.)

Every decision writes one audit line to the service log: caller user + tier, intent, required tier, allow/deny.
