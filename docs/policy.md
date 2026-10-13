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
| DeleteRoute | NetworkConfigOperators | delete a route |
| ResetAdapter | NetworkConfigOperators | reset (disable/enable) an adapter |
| Release | NetworkConfigOperators | release a DHCP lease |
| Prefer | NetworkConfigOperators | prefer an adapter (interface metric) |
| SetMtu | NetworkConfigOperators | set an adapter MTU |
| VirtualUnlock | NetworkConfigOperators | target a virtual (non-host-facing) adapter via `appliesTo` |

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

## Fail-closed behaviour

The last policy that read cleanly is cached to a SYSTEM/Administrators-only file in the service store. If the
policy later becomes unreadable or invalid (access denied, a bad tier value), the service uses that
**last-known-good** cache — so a tightening an admin applied is not silently lost — and writes a warn audit line.
On a first run with no cache, it falls back to the default table. This fallback never collapses to a blanket
deny — it reverts to the last-known-good or default tiers. (An administrator may still deliberately tighten an
intent, including Renew, up to Admin via policy; "never deny-all" is about the unreadable-policy fallback, not a
promise that any intent stays available once policy raises it.)

Every decision writes one audit line to the service log: caller user + tier, intent, required tier, allow/deny.
