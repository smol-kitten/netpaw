# Telemetry (opt-in build only)

NetPaw ships in two builds. **You choose at download time.**

| download | telemetry |
|---|---|
| `NetPaw-<ver>.msi`, `netpaw-win-x64*.zip` | **none**. The telemetry code is not compiled in (`#if TELEMETRY`); there is nothing to switch off. |
| `NetPaw-<ver>-telemetry.msi` | crash reports + anonymous usage counts to `https://telemetry.catboy.systems`, so bugs get found and fixed. Shows a one-time notice at first start; *Settings → Telemetry* turns it off; policy `AllowTelemetry=0` turns it off fleet-wide. |

Add/Remove Programs shows which build is installed (*Comments* column).

## What the telemetry build sends

Every payload carries: NetPaw **version**, **OS version** (e.g. `Windows 10.0.26100`), a **random install id**
(16 hex chars generated on first run, stored in `settings.json`) and `environment=production`.

| when | what |
|---|---|
| start | heartbeat with the number of physical adapters, own profiles and managed profiles (counts only) |
| a profile / DHCP / reach / preset is applied | event `apply` with kind, step count, warning count, result (`ok`/`failed`), and the *first word* of each failed step's description (e.g. `Primary`, `Route`) |
| an apply fails | error `ApplyFailed` with the kind and those step words |
| monitor mode changes state | event `monitor` with the state names (e.g. `IntranetOnly` ← `Online`) |
| NetPaw crashes | the exception type, message and stack trace |

## What it never sends

- IP addresses, prefixes, gateways, DNS servers, routes, VLAN ids — no profile contents at all
- adapter names, computer name, user name, domain, MAC addresses
- command output from `netsh`/PowerShell
- repository URLs or pack contents
- anything while *Settings → Telemetry* is off or policy `AllowTelemetry=0`

Every send is logged as one line in `%APPDATA%\NetPaw\netpaw.log` (`telemetry api/v2/events -> 202`),
so you can audit exactly how often it talks.

## How the build is made

`dotnet publish -p:Telemetry=true -p:TelemetryToken=…` defines the `TELEMETRY` symbol and embeds the
project's ingest token as assembly metadata; CI builds this variant only when the
`NETPAW_TELEMETRY_TOKEN` secret exists and asserts the default build carries no such metadata.
The token only allows *ingest* to the NetPaw project bucket. Minting it is a one-time maintainer step:
`netpaw-cli telemetry adopt` prints a 6-digit code to confirm in Workflow Manager.

The hub (Workflow Manager, `telemetry.catboy.systems`) groups errors into issues on
`smol-kitten/netpaw` and tracks which version fixed them.

## What an error report contains (v0.10+)

Every background task (check round, switch discovery, auto-switch, update check, port check, apply)
runs through one guard. When one fails, the report carries: exception type and message, the
operation name, the current network state (`Online`, `NoGateway`, …), the work adapter's *name*
(never its addresses), the NetPaw version, uptime, and the last 40 lines of `netpaw.log` as they
are on disk at your chosen log level. Addresses that appear in those log lines are the ones NetPaw
logged for you — set *Settings → Log level* to *errors only* if you do not want them to travel.
