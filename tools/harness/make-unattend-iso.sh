#!/usr/bin/env bash
# Builds netpaw-harness-unattend.iso from autounattend.xml with the secrets filled in from the fleet vault, and uploads
# it to pve's local ISO store. Run from the claude box. The filled file lives only in a private temp dir, never in git.
set -euo pipefail
PVE=${PVE:-root@10.10.10.1}; here=$(cd "$(dirname "$0")" && pwd); t=$(mktemp -d); trap 'rm -rf "$t"' EXIT; umask 077
PW=$(python3 /home/claude/.claude-memory/vault.py get pve.harness_vm_password); PUB=$(python3 /home/claude/.claude-memory/vault.py get pve.harness_ssh_pub)
python3 - "$here/autounattend.xml" "$t/autounattend.xml" "$PW" "$PUB" <<'PY'
import sys; src,dst,pw,pub=sys.argv[1:]
s=open(src,encoding="utf-8").read().replace("@@PASSWORD@@",pw.replace("&","&amp;").replace("<","&lt;")).replace("@@SSH_PUB@@",pub.strip())
open(dst,"w",encoding="utf-8").write(s)
PY
cp "$here/clone-unattend.xml" "$t/clone-unattend.xml"
mk=$(command -v genisoimage || command -v mkisofs || command -v xorrisofs || true); [[ -n "$mk" ]] || { echo "need genisoimage/xorrisofs"; exit 1; }
"$mk" -quiet -o "$t/netpaw-harness-unattend.iso" -J -R -V HARNESS "$t/autounattend.xml" "$t/clone-unattend.xml"
scp -q -o BatchMode=yes "$t/netpaw-harness-unattend.iso" "$PVE:/var/lib/vz/template/iso/netpaw-harness-unattend.iso"
echo "RESULT|unattend-iso|uploaded local:iso/netpaw-harness-unattend.iso sha256=$(sha256sum "$t/netpaw-harness-unattend.iso" | cut -c1-16)…"
