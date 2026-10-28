#!/usr/bin/env bash
# Creates the pve API token for the harness driver and stores its value in the fleet vault WITHOUT the value ever
# reaching stdout, a log or a transcript (orchestrator m-8459). Run from the claude box. Idempotent: an existing
# token is left alone (pve shows a token value only once; to rotate, remove it on pve first).
set -euo pipefail
PVE=${PVE:-root@10.10.10.1}; USER_=harness@pve; TOKEN=n4; KEY=pve.harness_token
if ssh -o BatchMode=yes "$PVE" "pveum user token list $USER_ --output-format json" | grep -q "\"tokenid\":\"$TOKEN\""; then
  echo "RESULT|token|$USER_!$TOKEN|exists; value not re-readable (rotate: pveum user token remove $USER_ $TOKEN, then rerun)"; exit 0; fi
ssh -o BatchMode=yes "$PVE" "pveum user token add $USER_ $TOKEN -privsep 0 -comment 'NetPaw harness driver' --output-format json" \
  | python3 -c 'import json,sys; sys.stdout.write(json.load(sys.stdin)["value"])' \
  | python3 /home/claude/.claude-memory/vault.py set "$KEY" --stdin --type apikey --scope pve --desc "pve API token $USER_!$TOKEN for the NetPaw harness driver (PLAN-G11a step 2); privsep 0, ACLs on pool netpaw-harness, /vms/9160, /storage/local-lvm" >/dev/null
sha=$(python3 /home/claude/.claude-memory/vault.py list 2>/dev/null | grep -E "^$KEY " | grep -oE "sha8=[0-9a-f]+" | head -1)
echo "RESULT|token|$USER_!$TOKEN|stored in the vault as $KEY, ${sha:-sha8 n/a}"
