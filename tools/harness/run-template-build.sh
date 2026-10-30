#!/usr/bin/env bash
# PLAN-G11a step 3 driver, run FROM THE CLAUDE BOX inside the harness window: stages what pve needs, then runs
# build-template.sh there over ssh. Nothing secret stays on pve after the run (trap), nothing secret is in git.
#   1. stage the driver's private ssh key to pve:/root/.ssh/netpaw-harness (0600) from the vault, via env, never argv
#   2. stage clone-unattend.xml to pve:/root/netpaw-harness/ (scp fallback for the in-guest copy)
#   3. build + upload the unattend ISO (make-unattend-iso.sh), unless the phase does not need it
#   4. ssh pve 'bash -s -- <phase>' < build-template.sh
#   trap: remove the staged key from pve on ANY exit (success, failure, Ctrl-C); the ISO is deleted by the install phase
# Usage: run-template-build.sh [all|create|install|evidence|template]   (default all)
set -euo pipefail
PVE=${PVE:-root@10.10.10.1}; here=$(cd "$(dirname "$0")" && pwd); V=/home/claude/.claude-memory/vault.py
phase=${1:-all}; S="ssh -o BatchMode=yes -o ConnectTimeout=10 $PVE"
cleanup() { $S 'rm -f /root/.ssh/netpaw-harness; [[ -e /root/.ssh/netpaw-harness ]] && echo "RESULT|key|FAILED still on pve" || echo "RESULT|key|removed from pve"' || echo "RESULT|key|FAILED could not reach pve to remove /root/.ssh/netpaw-harness — remove it by hand"; }
trap cleanup EXIT
# 1. private key: vault -> env -> ssh stdin -> file on pve. Verified by sha8 of the file on pve vs the vault metadata.
exp=$(python3 "$V" get pve.harness_ssh_key | grep -o 'sha8=[0-9a-f]*' | head -1); [[ -n "$exp" ]] || { echo "RESULT|key|FAILED vault metadata unreadable"; exit 2; }
python3 "$V" exec pve.harness_ssh_key:K -- sh -c 'printf "%s\n" "$K" | ssh -o BatchMode=yes "$0" "umask 077; mkdir -p /root/.ssh; cat > /root/.ssh/netpaw-harness"' "$PVE"
got=$($S 'python3 -c "import hashlib,sys; print(hashlib.sha256(open(\"/root/.ssh/netpaw-harness\").read().rstrip(\"\\n\").encode()).hexdigest()[:8])"')
[[ "sha8=$got" == "$exp" ]] && echo "RESULT|key|staged on pve, $exp matches the vault" || { echo "RESULT|key|FAILED staged key sha8=$got != vault $exp"; exit 2; }
# Backstop for a dead claude box (hard kill, reboot, claude-service restart): a TRANSIENT pve-side timer removes the
# key 6 h after staging whatever happened here. No persistent host config; a rerun re-stages and re-arms it.
$S 'systemctl stop netpaw-harness-key-rm.timer 2>/dev/null; systemctl reset-failed netpaw-harness-key-rm.service netpaw-harness-key-rm.timer 2>/dev/null; systemd-run --quiet --unit=netpaw-harness-key-rm --on-active=6h --timer-property=AccuracySec=1min rm -f /root/.ssh/netpaw-harness' \
  && echo "RESULT|key|backstop: pve removes it in 6 h (transient unit netpaw-harness-key-rm)" || echo "RESULT|key|backstop timer not armed (systemd-run failed); the trap and the template phase still remove it"
# 2. clone-unattend.xml for the scp fallback in the install phase
$S 'mkdir -p /root/netpaw-harness'; scp -q -o BatchMode=yes "$here/clone-unattend.xml" "$PVE:/root/netpaw-harness/clone-unattend.xml"
echo "RESULT|stage|clone-unattend.xml on pve:/root/netpaw-harness/"
# 3. the unattend ISO is only needed before the install phase
if [[ $phase == all || $phase == create ]]; then "$here/make-unattend-iso.sh"; fi
# 4. run the phase on pve; the script goes over stdin so nothing needs to be checked out there
ssh -o BatchMode=yes "$PVE" bash -s -- "$phase" < "$here/build-template.sh"
