#!/usr/bin/env bash
# Builds netpaw-harness-unattend.iso from autounattend.xml with the secrets filled in from the fleet vault, and uploads
# it to pve's local ISO store. Run from the claude box. The filled file lives only in a private temp dir, never in git.
# The secrets reach the filler as ENVIRONMENT variables via `vault.py exec` (never argv, never a shell variable,
# never --reveal): `vault.py get` is masked by default and would bake its "ab••• (len=..)" preview into the XML.
#   --selftest   fill + verify (len/sha8 of PW and PUB against the vault metadata, placeholders gone, ISO builds),
#                nothing uploaded, nothing printed but RESULT lines
set -euo pipefail
PVE=${PVE:-root@10.10.10.1}; here=$(cd "$(dirname "$0")" && pwd); V=/home/claude/.claude-memory/vault.py
t=$(mktemp -d); trap 'rm -rf "$t"' EXIT; umask 077
mode=${1:-upload}
# Expected fingerprints come from the vault METADATA line ("... (len=22 sha8=29f50c91)"), never from the value.
expect() { python3 "$V" get "$1" | grep -o 'len=[0-9]* sha8=[0-9a-f]*' | head -1; }
EXP_PW=$(expect pve.harness_vm_password); EXP_PUB=$(expect pve.harness_ssh_pub)
[[ -n "$EXP_PW" && -n "$EXP_PUB" ]] || { echo "RESULT|fill|FAILED vault metadata unreadable (pve.harness_vm_password / pve.harness_ssh_pub)"; exit 2; }
python3 "$V" exec pve.harness_vm_password:PW pve.harness_ssh_pub:PUB -- \
  python3 "$here/fill-unattend.py" "$here/autounattend.xml" "$t/autounattend.xml" --expect "$EXP_PW" "$EXP_PUB"
cp "$here/clone-unattend.xml" "$t/clone-unattend.xml"
mk=$(command -v genisoimage || command -v mkisofs || command -v xorrisofs || true); [[ -n "$mk" ]] || { echo "RESULT|unattend-iso|FAILED need genisoimage/xorrisofs"; exit 1; }
"$mk" -quiet -o "$t/netpaw-harness-unattend.iso" -J -R -V HARNESS "$t/autounattend.xml" "$t/clone-unattend.xml"
sha=$(sha256sum "$t/netpaw-harness-unattend.iso" | cut -c1-16)
if [[ $mode == --selftest ]]; then echo "RESULT|selftest|iso built locally ($(stat -c %s "$t/netpaw-harness-unattend.iso") bytes, sha256=$sha…), not uploaded"; exit 0; fi
scp -q -o BatchMode=yes "$t/netpaw-harness-unattend.iso" "$PVE:/var/lib/vz/template/iso/netpaw-harness-unattend.iso"
echo "RESULT|unattend-iso|uploaded local:iso/netpaw-harness-unattend.iso sha256=$sha…"
