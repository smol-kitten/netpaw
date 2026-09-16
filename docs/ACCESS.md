# ACCESS — netpaw

How to reach the things this repo depends on. Pointers only: hosts, paths and credentials live in
the maintainer's local access ledger (`access.py list`) and memory scope `netpaw`, never here.

| what | where | notes |
|---|---|---|
| Community pack signing key (private) | maintainer box, local ledger / memory scope `netpaw` | keyId `ec6ef08c`; public key in `packs/community/public.txt`. After editing `packs/community/index.json`: `netpaw-cli pack sign packs/community/index.json <private.pem>`; CI rejects unsigned/tampered indexes. Rotate = `pack keygen`, publish the new public key, update `Settings.CommunityRepo` pin. |
| Telemetry ingest token | GitHub secret `NETPAW_TELEMETRY_TOKEN` (this repo); origin in memory scope `netpaw` | Minted by the telemetry hub's project registry. Rotate there, then update the secret. CI builds `NetPaw-<ver>-telemetry.msi` only when the secret exists. |
| Telemetry hub | `https://telemetry.catboy.systems` | Errors bucket to issues on `smol-kitten/netpaw`. |
| Windows test VM | memory scope `netpaw` (`mem.py recall "netpaw test vm"`) | Runbook in the maintainer wiki (`dev-environment`). Spare NIC `Ethernet 2` for destructive tests; never touch `Ethernet` (SSH path). GUI apps via `schtasks /IT /RL HIGHEST`. |
| CI | self-hosted Linux runner (tests, zips) + GitHub-hosted `windows-latest` (WiX 5 MSI, install/uninstall smoke) | The Linux runner may lack `zip`; ci.yml has a python/7z fallback. |
| Releases | tag `vX.Y.Z` on main | Publishes zips + MSI(s) automatically. |
