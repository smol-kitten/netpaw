#!/usr/bin/env bash
# PLAN-G11a step 4: the harness driver. One command on the claude box gives a fresh Windows 11 clone of template 9160,
# runs tools/harness/vm-verify.ps1 on it with a NetPaw build, prints RESULT|key|value lines and destroys the clone.
#
#   tools/vm-harness.sh                 check mode: gates + stale-clone report, changes nothing (default)
#   tools/vm-harness.sh --sweep         gates + destroy stale clones (run file gone or older than 2 h), no new clone
#   tools/vm-harness.sh --run [--build <dir>] [--msi <file>] [--keep]
#                                       full cycle: gates → sweep → clone → start → ssh → verify → stop+destroy
#   tools/vm-harness.sh --gates         gates only, exit 0/3
#   tools/vm-harness.sh --selftest      destroy-guard self-test, no API/ssh (template/peer/out-of-range VMIDs must be refused)
#
# Authority split (measured 2026-10-29): the API token harness@pve!n4 (vault: pve.harness_token) sees only the pool,
# the template, local-lvm and its own clones — every MUTATING call (clone/start/stop/destroy, peer start/stop) goes
# through it. It has no Sys.Audit and no VM.Audit on VM 150 by design, so the four HOST-level gates (load, VM 150,
# PawCheck, vzdump) are READ over root ssh with read-only commands; nothing else uses root ssh.
# Exit codes: 0 ok, 3 a gate refused (nothing was touched), 1 a run step failed (cleanup still ran), 4 cleanup failed.
set -euo pipefail
PVE_HOST=${PVE_HOST:-10.10.10.1}; PVE_NODE=${PVE_NODE:-pve}; PVE_SSH=${PVE_SSH:-root@10.10.10.1}
TPL=9160; PEER=9169; POOL=netpaw-harness; RANGE_LO=9161; RANGE_HI=9168; STALE_SECS=7200
RUNDIR=${RUNDIR:-/run/netpaw-harness}; VAULT=/home/claude/.claude-memory/vault.py
LOAD_MAX=20; THINPOOL_MAX=85; SSH_WAIT_MIN=15
here=$(cd "$(dirname "$0")" && pwd); VERIFY="$here/harness/vm-verify.ps1"
mode=check; build=; msi=; keep=0
if [[ ${1:-} == --selftest ]]; then   # no API, no ssh: the destroy guard against a fake pool listing (orchestrator m-8896)
  RANGE_LO=9161; RANGE_HI=9168; TPL=9160; PEER=9169
  destroyable() { [[ $1 =~ ^[0-9]+$ ]] && (( $1 >= RANGE_LO && $1 <= RANGE_HI && $1 != TPL && $1 != PEER )); }
  ok=1; for c in "9160 refused" "9169 refused" "9161 ok" "9168 ok" "9159 refused" "9170 refused" "150 refused" "x refused"; do
    set -- $c; if destroyable "$1"; then got=ok; else got=refused; fi
    [[ $got == "$2" ]] && echo "RESULT|selftest|$1 $got" || { echo "RESULT|selftest|$1 FAILED got $got want $2"; ok=0; }; done
  (( ok )) && exit 0 || exit 1
fi
while [[ $# -gt 0 ]]; do case $1 in
  --run) mode=run;; --sweep) mode=sweep;; --gates) mode=gates;; --build) build=$2; shift;; --msi) msi=$2; shift;;
  --keep) keep=1;; -h|--help) sed -n '2,13p' "$0"; exit 0;; *) echo "unknown flag $1" >&2; exit 2;; esac; shift; done
say() { echo "RESULT|$*"; }
refuse() { say "gate|refused:$*"; exit 3; }
rssh() { ssh -o BatchMode=yes -o ConnectTimeout=8 "$PVE_SSH" "$@"; }   # read-only host facts only
TOKEN=$(python3 "$VAULT" get pve.harness_token --reveal 2>/dev/null || true)
[[ -n "$TOKEN" ]] || refuse "no-token (vault pve.harness_token unreadable)"
api() { # api <METHOD> <path> [curl --data-urlencode args...]; prints the body; non-2xx → returns 1 with the body on stderr
  local m=$1 p=$2; shift 2; local out code
  out=$(curl -sk -X "$m" -H "Authorization: PVEAPIToken=harness@pve!n4=$TOKEN" -w '\n%{http_code}' "$@" "https://$PVE_HOST:8006/api2/json/$p")
  code=${out##*$'\n'}; out=${out%$'\n'*}
  [[ $code == 2* ]] && { printf '%s' "$out"; return 0; }; echo "api $m $p -> $code $out" >&2; return 1; }
jqd() { jq -r "$@"; }
# Defense in depth on the destroy path (orchestrator m-8896): the token holds VM.Allocate on the template and the pool
# (the peer), so a name collision must never reach DELETE. Only VMIDs inside the clone range are ever destroyed.
destroyable() { [[ $1 =~ ^[0-9]+$ ]] && (( $1 >= RANGE_LO && $1 <= RANGE_HI && $1 != TPL && $1 != PEER )); }

# ---- gates: every refusal is its own RESULT line; no state changes before all pass ---------------------------------
hm=$(TZ=Europe/Berlin date +%H%M); (( 10#$hm >= 30 && 10#$hm < 230 )) && refuse "pawcheck-window $hm Berlin"
(( 10#$hm >= 230 && 10#$hm < 700 )) || refuse "window $hm Berlin (02:30-07:00)"
host=$(rssh 'cut -d" " -f1 /proc/loadavg; qm status 150 2>/dev/null | awk "{print \$2}"; qm status 9150 >/dev/null 2>&1 && echo 9150 || echo -; test -e /run/lock/pawcheck-vm150.lock && (flock -n /run/lock/pawcheck-vm150.lock true 2>/dev/null && echo free || echo held) || echo nolock; pvesh get /cluster/tasks --output-format json | python3 -c "import json,sys; print(sum(1 for t in json.load(sys.stdin) if t.get(\"type\")==\"vzdump\" and not t.get(\"endtime\")))"; pgrep -c vzdump || true') \
  || refuse "host-facts (read-only ssh to $PVE_SSH failed)"
{ read -r load1; read -r vm150; read -r pcvm; read -r pclock; read -r vzt; read -r vzp; } <<<"$host"
awk -v l="$load1" -v m="$LOAD_MAX" 'BEGIN{exit !(l>=m)}' && refuse "load $load1 (max $LOAD_MAX)"
[[ $vm150 == running ]] && refuse "vm150 running"
[[ $pcvm == 9150 ]] && refuse "pawcheck (VM 9150 exists)"; [[ $pclock == held ]] && refuse "pawcheck (lock held)"
(( vzt > 0 || vzp > 0 )) && refuse "vzdump running (tasks=$vzt procs=$vzp)"
st=$(api GET "nodes/$PVE_NODE/storage/local-lvm/status") || refuse "thinpool (storage status unreadable)"
tp=$(jqd '.data | (.used*100/.total) | floor' <<<"$st"); (( tp >= THINPOOL_MAX )) && refuse "thinpool ${tp}% (max $THINPOOL_MAX)"
tplcfg=$(api GET "nodes/$PVE_NODE/qemu/$TPL/config" 2>/dev/null || echo '{"data":{}}')
is_tpl=$(jqd '.data.template // 0' <<<"$tplcfg")
if [[ $mode == run && $is_tpl != 1 ]]; then refuse "no-template ($TPL is not a template yet: PLAN-G11a step 3)"; fi
say "gate|ok window=$hm load=$load1 vm150=${vm150:-?} pawcheck=$pcvm/$pclock vzdump=$vzt/$vzp thinpool=${tp}% template=$([[ $is_tpl == 1 ]] && echo yes || echo no)"
[[ $mode == gates ]] && exit 0

# ---- stale-clone sweep: pool VMs named netpaw-h-<run> whose run file is gone or older than 2 h ---------------------
mkdir -p "$RUNDIR"; now=$(date +%s)
res=$(api GET "cluster/resources?type=vm")
while IFS=$'\t' read -r vmid name status; do
  [[ $name == netpaw-h-* ]] || { say "sweep|$vmid|$name not a harness clone, left alone"; continue; }
  run=${name#netpaw-h-}; f="${RUNDIR:?}/${run:?}.vmid"; age=$(( now - $(stat -c %Y "$f" 2>/dev/null || echo 0) ))
  if [[ -e $f && $age -lt $STALE_SECS && $(cat "$f") == "$vmid" ]]; then say "sweep|$vmid|$name live (run file ${age}s old)"; continue; fi
  if [[ $mode == check ]]; then say "sweep|$vmid|$name STALE (run file $([[ -e $f ]] && echo "${age}s old" || echo missing)); check mode: not destroyed"; continue; fi
  destroyable "$vmid" || { say "sweep|$vmid|$name refused: outside clone range $RANGE_LO-$RANGE_HI (template $TPL / peer $PEER are never destroyed)"; continue; }
  [[ $status == running ]] && api POST "nodes/$PVE_NODE/qemu/$vmid/status/stop" >/dev/null && sleep 5
  api DELETE "nodes/$PVE_NODE/qemu/$vmid?purge=1&destroy-unreferenced-disks=1" >/dev/null && say "sweep|$vmid|$name destroyed (stale)" || say "sweep|$vmid|$name FAILED to destroy"
  rm -f "${RUNDIR:?}/${run:?}.vmid"
done < <(jqd --arg p "$POOL" '.data[] | select(.type=="qemu" and .pool==$p) | [.vmid,.name,.status] | @tsv' <<<"$res")
say "sweep|pool-qemu|$(jqd --arg p "$POOL" '[.data[] | select(.type=="qemu" and .pool==$p)] | length' <<<"$res") VM(s) in $POOL"
[[ $mode == run ]] || exit 0

# ---- run: run file + trap BEFORE the clone exists --------------------------------------------------------------------
mapfile -t used < <(jqd --arg p "$POOL" '.data[] | select(.type=="qemu" and .pool==$p) | .vmid' <<<"$res"); vmid=
for c in $(seq $RANGE_LO $RANGE_HI); do [[ " ${used[*]:-} " == *" $c "* ]] || { vmid=$c; break; }; done
[[ -n $vmid ]] || { say "run|FAILED no free VMID in $RANGE_LO-$RANGE_HI"; exit 1; }
run=$(date -u +%Y%m%dT%H%M%S); name="netpaw-h-$run"; runfile="${RUNDIR:?}/${run:?}.vmid"; echo "$vmid" > "$runfile"
tmp=$(mktemp -d); chmod 700 "$tmp"; key="$tmp/key"; peer_started=0; cloned=0; rc=0
cleanup() {
  local r=$?; set +e; trap - EXIT
  if (( cloned )) && (( keep )); then say "cleanup|$vmid|KEPT (--keep): destroy by hand or let the next sweep take it after 2 h"
  elif (( cloned )) && ! destroyable "$vmid"; then say "cleanup|$vmid|refused: outside clone range $RANGE_LO-$RANGE_HI; NOT destroyed"; r=4
  elif (( cloned )); then
    api POST "nodes/$PVE_NODE/qemu/$vmid/status/stop" >/dev/null 2>&1; for i in $(seq 1 24); do [[ $(api GET "nodes/$PVE_NODE/qemu/$vmid/status/current" | jqd '.data.status') == stopped ]] && break; sleep 5; done
    if api DELETE "nodes/$PVE_NODE/qemu/$vmid?purge=1&destroy-unreferenced-disks=1" >/dev/null; then say "cleanup|$vmid|destroyed"; else say "cleanup|$vmid|FAILED destroy"; r=4; fi
  fi
  (( peer_started )) && { api POST "nodes/$PVE_NODE/lxc/$PEER/status/stop" >/dev/null 2>&1 && say "cleanup|peer|$PEER stopped" || say "cleanup|peer|$PEER FAILED to stop"; }
  (( keep )) || rm -f "${runfile:?}"; rm -rf "${tmp:?}"
  say "lvm|used-after|$(api GET "nodes/$PVE_NODE/storage/local-lvm/status" | jqd '.data.used')"
  say "pool|members-after|$(api GET "pools/$POOL" | jqd '[.data.members[].vmid] | join(",")')"
  exit $r; }
trap cleanup EXIT INT TERM
python3 "$VAULT" get pve.harness_ssh_key --reveal > "$key" && chmod 600 "$key" && [[ -s $key ]] || { say "run|FAILED ssh key (vault pve.harness_ssh_key)"; exit 1; }
say "lvm|used-before|$(jqd '.data.used' <<<"$st")"
t0=$(date +%s); api POST "nodes/$PVE_NODE/qemu/$TPL/clone" --data-urlencode "newid=$vmid" --data-urlencode "name=$name" --data-urlencode "pool=$POOL" --data-urlencode "full=0" >/dev/null || { say "clone|$vmid|FAILED"; exit 1; }
cloned=1; for i in $(seq 1 90); do lock=$(api GET "nodes/$PVE_NODE/qemu/$vmid/config" 2>/dev/null | jqd '.data.lock // "none"' || echo clone); [[ $lock == none ]] && break; sleep 2; done
say "clone|$vmid|$name linked clone in $(( $(date +%s) - t0 )) s"
if [[ $(api GET "nodes/$PVE_NODE/lxc/$PEER/status/current" | jqd '.data.status') != running ]]; then api POST "nodes/$PVE_NODE/lxc/$PEER/status/start" >/dev/null && peer_started=1 && say "peer|$PEER|started"; else say "peer|$PEER|already running"; fi
t1=$(date +%s); api POST "nodes/$PVE_NODE/qemu/$vmid/status/start" >/dev/null || { say "start|$vmid|FAILED"; exit 1; }
SSHO=(-o BatchMode=yes -o ConnectTimeout=6 -o StrictHostKeyChecking=no -o UserKnownHostsFile="$tmp/kh" -i "$key")
S() { ssh "${SSHO[@]}" "harness@$ip" "$@"; }
ip=; for _ in $(seq 1 $((SSH_WAIT_MIN*4))); do
  ip=$(api GET "nodes/$PVE_NODE/qemu/$vmid/agent/network-get-interfaces" 2>/dev/null | jqd '[.data.result[]?["ip-addresses"][]? | select(.["ip-address-type"]=="ipv4") | .["ip-address"] | select(startswith("127.")|not) | select(startswith("169.254.")|not) | select(startswith("10.99.")|not)] | first // empty' || true)
  [[ -n $ip ]] && S hostname >/dev/null 2>&1 && break; ip=; sleep 15; done
[[ -n $ip ]] || { say "boot|$vmid|FAILED no ssh after $SSH_WAIT_MIN min"; exit 1; }
say "boot|$vmid|ssh up at $ip after $(( $(date +%s) - t1 )) s (hostname $(S hostname | tr -d '\r'))"
S 'New-Item -ItemType Directory -Force C:\harness | Out-Null' >/dev/null
scp -q "${SSHO[@]}" "$VERIFY" "harness@$ip:C:/harness/vm-verify.ps1"
args=""
[[ -n $msi ]] && { scp -q "${SSHO[@]}" "$msi" "harness@$ip:C:/harness/NetPaw.msi"; args="$args -Msi C:\\harness\\NetPaw.msi"; }
[[ -n $build ]] && { scp -q -r "${SSHO[@]}" "$build" "harness@$ip:C:/harness/build"; args="$args -Build C:\\harness\\build"; }
set +e; S "powershell -NoProfile -ExecutionPolicy Bypass -File C:\\harness\\vm-verify.ps1 $args" | tr -d '\r' | tee "$tmp/verify.log" | grep -E '^RESULT\|'; rc=${PIPESTATUS[0]}; set -e
grep -vE '^RESULT\|' "$tmp/verify.log" | sed 's/^/log|/' | head -40 || true
say "verify|$vmid|exit $rc ($(grep -c '^RESULT|' "$tmp/verify.log" || true) RESULT lines) template-built=$(jqd '.data.description // ""' <<<"$tplcfg" | grep -oE 'Built [0-9-]+' || echo n/a)"
exit $rc
