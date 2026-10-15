# Threat model (v0.14+)

NetPaw changes a Windows machine's network configuration. Since v0.14 a LocalSystem service does every
change; the tray and the CLI only send typed requests. This page says what that design defends against,
what it trusts, and the decisions behind it.

## Trust boundaries

- **The service (`netpaw-svc`, LocalSystem)** is the only component that runs `netsh`, `ipconfig` or
  `powershell` to change configuration (nothing runs `route`; the tray runs read-only tools as the user:
  `arp`, a `pktmon` capture, and `schtasks` once to remove the pre-0.14 task). It accepts typed intents over `\\.\pipe\NetPaw`, resolves the adapter against the live
  list, validates every value (`ProfileGuard`), plans the commands itself, and audits each decision to the
  Windows Event Log. It never runs a command line a client sent.
- **The tray and the CLI (`asInvoker`)** run as the logged-on user. They can read, diagnose and preview.
  Every change is a request the service may refuse.
- **Policy** lives in `HKLM\Software\Policies\NetPaw` (GPO/Intune). Only administrators and SYSTEM can
  write it. The service reads it; the tray only greys out buttons.
- **Managed profiles** live in `%ProgramData%\NetPaw\profiles.d`. The folder must be SYSTEM/Administrators-
  owned and not a reparse point; each file's owner is checked on the open handle before it is read.

## Who may do what

A caller's tier comes from its token (TOKEN_GROUPS), never from the request: Administrators →
**Admin**; Network Configuration Operators → **NCO**; everyone else → **StandardUser**. Each intent has a
minimum tier (`docs/policy.md`); a policy may raise or lower it within a hard floor.

## Defended

- A standard user cannot run anything as SYSTEM: there is no command-string path.
- A planted or user-owned managed profile is ignored (owner check on the handle, folder gate).
- A squatter cannot pose as the service: the client checks the server PID against the Service Control
  Manager before it sends anything, so a foreign pipe is never talked to. The service also refuses to start
  when the name is taken, keeps one listener open at all times, and clients cannot add pipe instances.
- Remote (SMB) callers are refused twice: the service closes any connection whose client has a computer name
  (`GetNamedPipeClientComputerName` succeeds only for an SMB client, loopback included) before it reads a byte,
  and the pipe ACL denies the NETWORK SID (SMB opens, SSH network logons). Measured on a hosted runner: a loopback
  SMB open passed the ACE, so the computer-name check is the layer CI asserts.
- A tampered update is refused: installers are verified against a pinned root before they run.
- An unreadable policy does not loosen anything: the last valid policy stays in force; with none, the
  defaults.

## Trusted (not defended against)

- A local administrator. They can change policy, the service binary and the profiles folder.
- The GPO/Intune channel. A tightened or loosened policy is taken as the organisation's decision.
- Physical access and a compromised kernel.

## Decisions, and why

1. **A UAC-filtered administrator is a StandardUser.** An admin's non-elevated process carries the
   Administrators SID deny-only. NetPaw treats it as a standard user — stricter than Microsoft's own
   UAC boundary, the same as `netsh` and `Set-NetIPInterface`, which require elevation.
2. **Any interactive session may use StandardUser intents, including RDP and fast-user-switching sessions.**
   Applying a profile from a desk is the product's purpose. The audit line names the session; a policy
   can raise every tier to NCO or Admin.
3. **The NCO tier is broader than the OS group.** Windows' Network Configuration Operators may set
   addresses and DNS, not routes or metrics. NetPaw's NCO tier also allows routes, metrics, reset and
   MTU, because the service does the work on the operator's behalf under audit. A policy can raise those
   intents to Admin.
4. **The last-known-good policy cache is an availability control.** A readable policy always wins. The
   cache applies only when the policy is unreadable or invalid, and never below the floor.

## Scope limits

- **IPv4 only.** Profiles set IPv4 addresses, gateways, DNS and routes. IPv6 addresses and IPv6 DNS
  servers are left untouched, so a profile that changes IPv4 DNS does not change IPv6 resolution (a
  possible DNS leak on dual-stack networks). IPv6 support is on the 1.0 roadmap (`docs/PLAN-v1.0.md`).
- No automatic rollback yet: if an apply cuts the connection the UI sits on, use *Reset adapter* or the
  previous profile. A commit-confirmed rollback is planned (0.17).
- Adapters are addressed by name in requests. A Network Configuration Operator can rename an adapter;
  persistent-ID targeting is planned (0.17).
