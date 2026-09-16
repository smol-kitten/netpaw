# Command line

`netpaw-cli.exe` shares profiles and logic with the tray. Read-only commands work anywhere; changes need an elevated prompt.

- `adapters` — adapters, current config, VLAN capability.
- `list` / `show <profile>` / `apply <profile> [-a adapter] [-n]` — `-n` prints the plan only.
- `dhcp [-a adapter]`
- `reach <ip[/prefix]> [--replace]`
- `stuck` — programs waiting on an unanswered connection, why, and the profile/preset that covers the target (exit 1 when any, 4 when policy denies)
- `diag [file.zip]` — diagnostics bundle: ipconfig /all, routes, interfaces, netsh config, arp, DNS cache, adapters.json, log, incidents, settings (credentials redacted)
- `wake <mac> [subnet/prefix]` — magic packet to the global and subnet broadcast (same segment only)
- `routes` — the IPv4 route table with interface names
- `trace <host> [--max N]` — traceroute over ICMP TTL; prints hops as they answer, exit 1 when the target stays silent
- `check <host:port> [--timeout ms]` — one TCP connect; exit 0 open, 1 refused/unreachable, 2 timeout, 3 unresolved
- `preset [query]` / `preset apply <vendor/model>`
- `capture <name>` / `export <file> [--managed]` / `import <file>`
- `temp` / `clear-temp`
- `vlan <adapter> [id]`
- `repo` / `repo add|remove|sync|search` / `pack keygen|build|sign|verify`
- `policy` / `where`

Exit codes: 0 ok · 1 a step failed · 2 usage · 3 not VLAN-capable · 4 denied by policy.
