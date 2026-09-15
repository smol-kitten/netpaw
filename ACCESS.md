# ACCESS — netpaw

How to reach the things this repo depends on. Pointers only; no secrets in this file.

| what | where | notes |
|---|---|---|
| Community pack signing key (private) | maintainer box, `/root/.netpaw-keys/private.pem` (0600) | keyId `ec6ef08c`; public key in `packs/community/public.txt`. After editing `packs/community/index.json`: `netpaw-cli pack sign packs/community/index.json <private.pem>`; CI rejects unsigned/tampered indexes. Not in any vault yet — rotate = `pack keygen`, publish the new public key, update `Settings.CommunityRepo` pin. |
| Telemetry ingest token | GitHub secret `NETPAW_TELEMETRY_TOKEN` (this repo) and `/root/.netpaw-keys/telemetry.token` | Project `netpaw` in the workflow-manager `ProjectRegistry` (Coolify app `ydlxlq8zc1elzwqjmvlz98im`, host 10.10.0.1, state `/var/www/html/data/state`). Rotate: `ProjectRegistry::rotateToken('netpaw')` in the container, then update the secret. CI builds `NetPaw-<ver>-telemetry.msi` only when the secret exists. |
| Telemetry hub | `https://telemetry.catboy.systems` | Errors bucket to issues on `smol-kitten/netpaw`. |
| Windows test VM | VMID 150 on pve `10.10.10.1`, `ssh paws@10.10.20.50` | Runbook in the pawsktop repo wiki (`dev-environment`). Spare NIC `Ethernet 2` for destructive tests; never touch `Ethernet` (SSH path). GUI apps via `schtasks /IT /RL HIGHEST`; console screenshot `/root/shot.sh 150` on the pve host. |
| CI | self-hosted Linux ARC runner (tests, zips) + GitHub-hosted `windows-latest` (WiX 5 MSI, install/uninstall smoke) | `/opt/runner2` lacks `zip`; ci.yml has a python/7z fallback. |
| Releases | tag `vX.Y.Z` on main | Publishes zips + MSI(s) automatically. |
