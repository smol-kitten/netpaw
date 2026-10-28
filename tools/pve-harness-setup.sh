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
if ip link show "$BRIDGE" >/dev/null 2>&1; then say "bridge|$BRIDGE|exists"; else
  if ! grep -q "^iface $BRIDGE " /etc/network/interfaces; then
    run bash -c "printf '\nauto %s\niface %s inet manual\n\tbridge-ports none\n\tbridge-stp off\n\tbridge-fd 0\n\t# NetPaw harness test network (PLAN-G11a): no uplink, peer LXC %s + disposable clones only\n' '$BRIDGE' '$BRIDGE' '$PEER' >> /etc/network/interfaces"
  fi
  run ifreload -a
  (( APPLY )) && { ip link show "$BRIDGE" >/dev/null && say "bridge|$BRIDGE|created" || { say "bridge|$BRIDGE|FAILED"; exit 1; }; } || say "bridge|$BRIDGE|would create"
fi

# ---- 2. pool ------------------------------------------------------------------------------------------------------------
if pvesh get /pools --output-format json | grep -q "\"poolid\":\"$POOL\""; then say "pool|$POOL|exists"; else
  run pvesh create /pools --poolid "$POOL" --comment "NetPaw disposable-VM harness (PLAN-G11a): template $TPL, peer $PEER, clones 9161-9168"
  say "pool|$POOL|$([[ $APPLY == 1 ]] && echo created || echo 'would create')"
fi

# ---- 3. roles, user, token, ACLs (least privilege: pool-scoped VM rights + allocate on local-lvm only) --------------
if pveum role list --output-format json | grep -q "\"roleid\":\"$ROLE\""; then say "role|$ROLE|exists"; else
  run pveum role add "$ROLE" -privs "VM.Clone,VM.Allocate,VM.PowerMgmt,VM.Audit,VM.Monitor,VM.Console"
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
if pveum user token list "$USER_" --output-format json 2>/dev/null | grep -q "\"tokenid\":\"$TOKEN\""; then say "token|$USER_!$TOKEN|exists (value not shown again)"; else
  if (( APPLY )); then
    echo "RUN | pveum user token add $USER_ $TOKEN -privsep 0 -comment 'n4 harness driver'"
    secret=$(pveum user token add "$USER_" "$TOKEN" -privsep 0 -comment "n4 harness driver" --output-format json | python3 -c 'import json,sys; print(json.load(sys.stdin)["value"])')
    say "token|$USER_!$TOKEN|created|store it on n4 as vault key pve.harness_token (shown ONCE below, not logged)"
    echo "TOKEN-VALUE-ONCE: $secret"
  else echo "PLAN| pveum user token add $USER_ $TOKEN -privsep 0  (value printed once, goes to the n4 vault as pve.harness_token)"; say "token|$USER_!$TOKEN|would create"; fi
fi

# ---- 4. peer LXC on the test bridge only: DHCP + DNS + gateway address + smbclient --------------------------------
if pct status "$PEER" >/dev/null 2>&1; then say "peer|$PEER|exists"; else
  run pct create "$PEER" "$CT_TPL" --hostname netpaw-peer --memory 256 --swap 0 --cores 1 --rootfs local-lvm:2 \
      --net0 "name=eth0,bridge=$BRIDGE,ip=$PEER_IP/24" --unprivileged 1 --features nesting=0 --onboot 0 --pool "$POOL" \
      --description "NetPaw harness peer (PLAN-G11a): dnsmasq DHCP+DNS on $PEER_NET, smbclient for the remote-pipe test. No uplink."
  if (( APPLY )); then
    pct start "$PEER"; sleep 8
    pct exec "$PEER" -- bash -c "apt-get -qq update && DEBIAN_FRONTEND=noninteractive apt-get -qq install -y dnsmasq smbclient samba-common-bin >/dev/null"
    pct exec "$PEER" -- bash -c "printf 'interface=eth0\nbind-interfaces\ndhcp-range=$DHCP_RANGE\ndhcp-option=option:router,$PEER_IP\ndhcp-option=option:dns-server,$PEER_IP\naddress=/peer.nptest/$PEER_IP\nlog-dhcp\n' > /etc/dnsmasq.d/nptest.conf && systemctl restart dnsmasq && systemctl is-active dnsmasq"
    pct stop "$PEER"
    say "peer|$PEER|created (dnsmasq on $PEER_NET, stopped; the driver starts and stops it per run)"
  else say "peer|$PEER|would create"; fi
fi

# ---- 5. backups: the pool is never backed up -------------------------------------------------------------------------
for job in $(pvesh get /cluster/backup --output-format json | python3 -c 'import json,sys; [print(j["id"]) for j in json.load(sys.stdin)]'); do
  ex=$(pvesh get "/cluster/backup/$job" --output-format json | python3 -c 'import json,sys; print(json.load(sys.stdin).get("exclude",""))')
  want="$TPL,$PEER,${CLONES// /,}"
  if [[ ",$ex," == *",$TPL,"* ]]; then say "vzdump|$job|already excludes the pool ids"; else
    run pvesh set "/cluster/backup/$job" --exclude "${ex:+$ex,}$want"
    say "vzdump|$job|$([[ $APPLY == 1 ]] && echo 'excluded' || echo 'would exclude') $want"
  fi
done

# ---- 6. measurements the plan's Step 0 asks for ------------------------------------------------------------------------
say "acl|$(pveum acl list --output-format json 2>/dev/null | python3 -c 'import json,sys; print("; ".join(f"{a[\"path\"]}={a[\"roleid\"]}" for a in json.load(sys.stdin) if a.get("ugid","").startswith("harness")))' 2>/dev/null || echo n/a)"
say "pool|$(pvesh get /pools/$POOL --output-format json 2>/dev/null | python3 -c 'import json,sys; d=json.load(sys.stdin); print(",".join(str(m.get("vmid")) for m in d.get("members",[])))' 2>/dev/null || echo n/a)"
say "lvm|free-before|data=${thin}% $(vgs pve -o vg_free --noheadings | tr -d ' ')"
say "bridge|$(ip -br link show "$BRIDGE" 2>/dev/null || echo "$BRIDGE absent")"
say "done|$([[ $APPLY == 1 ]] && echo applied || echo 'dry run, nothing changed')"
