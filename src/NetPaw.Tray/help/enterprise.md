# Enterprise

## Managed profiles
IT drops JSON files into `%ProgramData%\NetPaw\profiles.d\`. They appear with a **managed** badge: you can apply or copy them, not edit or delete them. A managed name wins over your own profile of the same name.

## Policy
`HKLM\SOFTWARE\Policies\NetPaw` (GPO via the ADMX template, Intune via ADMX ingestion, or a script) can:
- pin the work adapter, the panel hotkey, the confirm setting, the repository list;
- deny: own profiles, own repositories, reach mode, temporary addresses, DHCP, unsigned repositories.

Denied actions disappear from the panel and menu; `netpaw-cli` exits with code 4 and says why. `netpaw-cli policy` prints what applies on this machine.

## Install
The MSI installs per machine (`%ProgramFiles%\NetPaw`), adds the CLI to `PATH`, creates `profiles.d`. Intune: upload as a line-of-business app. GPO: software installation. Details: `docs/ENTERPRISE.md` in the project.
