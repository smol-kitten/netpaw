#!/usr/bin/env bash
# PLAN-G11a step 2: the one-time pve changes for the disposable-VM harness, as reviewable code.
# Runs ON pve as root. Dry run by default: prints every command it would run as "PLAN| <cmd>".
# `--apply` executes them. Idempotent: every step checks first and prints RESULT|<step>|exists|created|skipped.
# Nothing here touches VM 150, VM 9150 (PawCheck) or any existing bridge; the only network change is a new
# bridge with no ports.  Operator approval: q-6364 (2026-10-26). Orchestrator GO per message, after review.
set -euo pipefail

APPLY=0; [[ "${1:-}" == "--apply" ]] && APPLY=1
BRIDGE=vmbr-nptest; POOL=netpaw-harness; TPL=9160; PEER=9169; CLONES="9161 9162 9163 9164 9165 9166 9167 9168"
ROLE=NetPawHarness; STOROLE=NetPawHarnessStorage; USER_=harness@pve; TOKEN=n4
PEER_IP=10.99.0.1; PEER_NET=10.99.0.0/24; DHCP_RANGE="10.99.0.100,10.99.0.200,12h"
CT_TPL=local:vztmpl/debian-12-standard_12.12-1_amd64.tar.zst

say() { echo "RESULT|$*"; }
run() { if (( APPLY )); then echo "RUN | $*"; "$@"; else echo "PLAN| $*"; fi; }

# ---- gates: the same ones the driver will use, so a refused run says why ------------------------------------------
load1=$(cut -d' ' -f1 /proc/loadavg); if (( $(echo "$load1 >= 20" | bc -l) )); then say "gate|refused:load $load1"; exit 3; fi
thin=$(lvs pve/data -o data_percent --noheadings | tr -d ' '); if (( $(echo "$thin > 85" | bc -l) )); then say "gate|refused:thinpool ${thin}%"; exit 3; fi
if pgrep -x vzdump >/dev/null; then say "gate|refused:vzdump running"; exit 3; fi
for id in $TPL $PEER $CLONES; do
  if qm status "$id" >/dev/null 2>&1 || pct status "$id" >/dev/null 2>&1; then
    if [[ "$id" == "$TPL" || "$id" == "$PEER" ]]; then say "vmid|$id|exists (reused)"; else say "gate|refused:vmid $id already in use"; exit 3; fi
  fi
done
say "gate|ok|load=$load1 thinpool=${thin}%"

# ---- 1. bridge with no uplink (isolated L2 for the test NIC) ---------------------------------------------------------
# Network safety (orchestrator m-8459): never `ifreload -a` on the fleet's host. Back up the file, validate the
# whole config with ifupdown2's syntax check, bring up ONLY the new bridge; on any failure restore and stop.
if ip link show "$BRIDGE" >/dev/null 2>&1; then say "bridge|$BRIDGE|exists"; else
  stamp=$(date +%Y%m%d-%H%M%S); bak="/etc/network/interfaces.bak-$stamp"
  if ! grep -q "^iface $BRIDGE " /etc/network/interfaces; then
    run cp -a /etc/network/interfaces "$bak"
    # orchestrator 2026-10-29: this ifupdown2 (3.3.0+pmx12) has no 'ifquery --syntax-check', and 'ifup --syntax-check'
    # already exits 1 on the existing file (vmbr0 bridge-fd 0 warning). So the check is DIFFERENTIAL: parse before and
    # after; any NEW 'error:' line, or any new warning other than this bridge's bridge-fd range, fails and restores.
    base_chk=$(ifup -a --syntax-check 2>&1 | sort || true)
    run bash -c "printf '\nauto %s\niface %s inet manual\n\tbridge-ports none\n\tbridge-stp off\n\tbridge-fd 0\n\t# NetPaw harness test network (PLAN-G11a): no uplink, peer LXC %s + disposable clones only\n' '$BRIDGE' '$BRIDGE' '$PEER' >> /etc/network/interfaces"
    if (( APPLY )); then
      new_lines=$(comm -13 <(printf '%s\n' "$base_chk") <(ifup -a --syntax-check 2>&1 | sort || true) | grep -v "^warning: $BRIDGE: bridge-fd: value of out range" || true)
      if [[ -n "$new_lines" ]]; then cp -a "$bak" /etc/network/interfaces; say "bridge|$BRIDGE|FAILED syntax check ($new_lines); /etc/network/interfaces restored from $bak"; exit 1; fi
      say "bridge|syntax-check|ok (backup $bak)"
    else echo "PLAN| ifquery -a --syntax-check   (restore $bak and stop on failure)"; fi
  fi
  run ifup "$BRIDGE"
  if (( APPLY )); then
    if ip link show "$BRIDGE" >/dev/null 2>&1; then say "bridge|$BRIDGE|created (only this interface was brought up)"
    else cp -a "$bak" /etc/network/interfaces 2>/dev/null; say "bridge|$BRIDGE|FAILED ifup; file restored from $bak"; exit 1; fi
  else say "bridge|$BRIDGE|would create"; fi
fi

# ---- 2. pool ------------------------------------------------------------------------------------------------------------
if pvesh get /pools --output-format json | grep -q "\"poolid\":\"$POOL\""; then say "pool|$POOL|exists"; else
  run pvesh create /pools --poolid "$POOL" --comment "NetPaw disposable-VM harness (PLAN-G11a): template $TPL, peer $PEER, clones 9161-9168"
  say "pool|$POOL|$([[ $APPLY == 1 ]] && echo created || echo 'would create')"
fi

# ---- 3. roles, user, token, ACLs (least privilege: pool-scoped VM rights + allocate on local-lvm only) --------------
if pveum role list --output-format json | grep -q "\"roleid\":\"$ROLE\""; then say "role|$ROLE|exists"; else
  run pveum role add "$ROLE" -privs "VM.Clone,VM.Allocate,VM.PowerMgmt,VM.Audit,VM.GuestAgent.Audit,VM.Console,Pool.Audit"   # Pool.Audit: the stale-clone sweep lists the pool (GET /pools is 403 without it)
  say "role|$ROLE|$([[ $APPLY == 1 ]] && echo created || echo 'would create')"
fi
if pveum role list --output-format json | grep -q "\"roleid\":\"$STOROLE\""; then say "role|$STOROLE|exists"; else
  run pveum role add "$STOROLE" -privs "Datastore.AllocateSpace,Datastore.Audit"
  say "role|$STOROLE|$([[ $APPLY == 1 ]] && echo created || echo 'would create')"
fi
if pveum user list --output-format json | grep -q "\"userid\":\"$USER_\""; then say "user|$USER_|exists"; else
  run pveum user add "$USER_" -comment "NetPaw harness driver on n4 (token-only, no password)"
  say "user|$USER_|$([[ $APPLY == 1 ]] && echo created || echo 'would create')"
fi
# ACLs: the pool (clones, peer), the template (clone source), the storage (linked-clone allocation). No root path.
run pveum acl modify "/pool/$POOL" -user "$USER_" -role "$ROLE"
run pveum acl modify "/vms/$TPL" -user "$USER_" -role "$ROLE"
run pveum acl modify "/storage/local-lvm" -user "$USER_" -role "$STOROLE"
# The token VALUE never reaches stdout or a transcript (orchestrator m-8459): tools/pve-harness-token.sh, run from the
# claude box, creates it over ssh and pipes the value straight into the fleet vault (key pve.harness_token).
if pveum user token list "$USER_" --output-format json 2>/dev/null | grep -q "\"tokenid\":\"$TOKEN\""; then say "token|$USER_!$TOKEN|exists"; else
  echo "PLAN| (from the claude box) tools/pve-harness-token.sh  ->  pveum user token add $USER_ $TOKEN -privsep 0 | vault set pve.harness_token --stdin"
  say "token|$USER_!$TOKEN|not created here; run tools/pve-harness-token.sh from the claude box"
fi

# ---- 4. peer LXC on the test bridge only: DHCP + DNS + gateway address + smbclient --------------------------------
if pct status "$PEER" >/dev/null 2>&1; then say "peer|$PEER|exists"; else
  run pct create "$PEER" "$CT_TPL" --hostname netpaw-peer --memory 256 --swap 0 --cores 1 --rootfs local-lvm:2 \
      --net0 "name=eth0,bridge=$BRIDGE,ip=$PEER_IP/24" --unprivileged 1 --features nesting=0 --onboot 0 --pool "$POOL" \
      --description "NetPaw harness peer (PLAN-G11a): dnsmasq DHCP+DNS on $PEER_NET, smbclient for the remote-pipe test. No uplink."
  if (( APPLY )); then
    # orchestrator 2026-10-29: the test bridge has no uplink, so packages come over a TEMPORARY LAN NIC (net1),
    # which is removed again once the peer is stopped. End state = peer on vmbr-nptest only, as planned.
    pct set "$PEER" --net1 "name=eth1,bridge=vmbr0,ip=dhcp,firewall=1"
    pct start "$PEER"; sleep 12
    pct exec "$PEER" -- bash -c "apt-get -qq update && DEBIAN_FRONTEND=noninteractive apt-get -qq install -y dnsmasq smbclient samba-common-bin >/dev/null"
    pct exec "$PEER" -- bash -c "printf 'interface=eth0\nbind-interfaces\ndhcp-range=$DHCP_RANGE\ndhcp-option=option:router,$PEER_IP\ndhcp-option=option:dns-server,$PEER_IP\naddress=/peer.nptest/$PEER_IP\nlog-dhcp\n' > /etc/dnsmasq.d/nptest.conf && systemctl restart dnsmasq && systemctl is-active dnsmasq"
    pct stop "$PEER"
    pct set "$PEER" --delete net1
    say "peer|$PEER|created (dnsmasq on $PEER_NET, stopped; the driver starts and stops it per run)"
  else say "peer|$PEER|would create"; fi
fi

# ---- 5. backups: the pool is never backed up -------------------------------------------------------------------------
# Only jobs that select ALL guests can ever pick up the pool ids; a job pinned to specific VMIDs cannot. Each edited
# job's full config is saved first; the new ids are MERGED into the existing exclude list (q-6364 allows exactly this edit).
jobdir="/root/pve-backup-jobs-$(date +%Y%m%d)"; want="$TPL,$PEER,${CLONES// /,}"
for job in $(pvesh get /cluster/backup --output-format json | python3 -c 'import json,sys; [print(j["id"]) for j in json.load(sys.stdin) if j.get("all")==1]'); do
  cfg=$(pvesh get "/cluster/backup/$job" --output-format json)
  ex=$(printf '%s' "$cfg" | python3 -c 'import json,sys; print(json.load(sys.stdin).get("exclude",""))')
  if [[ ",$ex," == *",$TPL,"* ]]; then say "vzdump|$job|already excludes the pool ids"; else
    run mkdir -p "$jobdir"
    if (( APPLY )); then printf '%s\n' "$cfg" > "$jobdir/$job.json"; say "vzdump|$job|config saved to $jobdir/$job.json"; else echo "PLAN| save $job config to $jobdir/$job.json"; fi
    run pvesh set "/cluster/backup/$job" --exclude "${ex:+$ex,}$want"
    say "vzdump|$job|$([[ $APPLY == 1 ]] && echo 'excluded' || echo 'would exclude') $want (merged with: ${ex:-none})"
  fi
done
for job in $(pvesh get /cluster/backup --output-format json | python3 -c 'import json,sys; [print(j["id"]) for j in json.load(sys.stdin) if j.get("all")!=1]'); do say "vzdump|$job|untouched (pinned to specific guests, cannot select the pool)"; done

# ---- 6. measurements the plan's Step 0 asks for ------------------------------------------------------------------------
say "acl|$(pveum acl list --output-format json 2>/dev/null | python3 -c 'import json,sys; print("; ".join(f"{a[\"path\"]}={a[\"roleid\"]}" for a in json.load(sys.stdin) if a.get("ugid","").startswith("harness")))' 2>/dev/null || echo n/a)"
say "pool|$(pvesh get /pools/$POOL --output-format json 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(",".join(str(m.get("vmid")) for m in d.get("members",[])))' 2>/dev/null || echo n/a)"
say "lvm|free-before|data=${thin}% $(vgs pve -o vg_free --noheadings | tr -d ' ')"
say "bridge|$(ip -br link show "$BRIDGE" 2>/dev/null || echo "$BRIDGE absent")"
say "done|$([[ $APPLY == 1 ]] && echo applied || echo 'dry run, nothing changed')"
