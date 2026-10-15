# Security policy

## Reporting a vulnerability

Use GitHub's private vulnerability reporting on this repository (Security → Report a vulnerability).
Do not open a public issue for a security problem. You get an acknowledgement within 3 working days.

## Supported versions

| Version | Supported |
|---|---|
| 0.14.x | Yes |
| 0.13.3 | Security fixes until 0.15.0 ships |
| < 0.13.3 | No — removed from the releases page; a local user could gain administrator rights through a planted profile. Update. |

## Fix targets

Critical (local privilege escalation, code execution as the service): a fixed release within 7 days.
High (bypass of a policy tier, a signature check or the pipe ACL): within 30 days. Others: the next
release.

## What a release guarantees

Every release is Authenticode-signed with an RFC 3161 timestamp and comes with `SIGNATURES.md`
(SHA-256 of each asset) and a verification recipe (`docs/signed-release.md`). The winget manifest
refers to the exact signed bytes. The threat model is in `docs/threat-model.md`.
