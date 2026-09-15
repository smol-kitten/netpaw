# Command line

`netpaw-cli.exe` shares profiles and logic with the tray. Read-only commands work anywhere; changes need an elevated prompt.

- `adapters` — adapters, current config, VLAN capability.
- `list` / `show <profile>` / `apply <profile> [-a adapter] [-n]` — `-n` prints the plan only.
- `dhcp [-a adapter]`
- `reach <ip[/prefix]> [--replace]`
- `preset [query]` / `preset apply <vendor/model>`
- `capture <name>` / `export <file> [--managed]` / `import <file>`
- `temp` / `clear-temp`
- `vlan <adapter> [id]`
- `repo` / `repo add|remove|sync|search` / `pack keygen|build|sign|verify`
- `policy` / `where`

Exit codes: 0 ok · 1 a step failed · 2 usage · 3 not VLAN-capable · 4 denied by policy.
