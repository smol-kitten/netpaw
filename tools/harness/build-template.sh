#!/usr/bin/env bash
# PLAN-G11a step 3: template VM 9160 on pve, mirroring VM 150's hardware (ovmf + vTPM + q35 + virtio-scsi-single),
# installed unattended from the Win11 ISO + netpaw-harness-unattend.iso, then generalised and converted to a template.
# Runs ON pve as root, inside the harness window (02:30-07:00 Berlin), load1 < 20. Phases are explicit; rerun resumes.
#   phase create   : qm create 9160 + attach ISOs (nothing started)
#   phase install  : start the VM; unattended setup runs (~20-30 min); the script waits for ssh on net0 (DHCP from vmbr0)
#   phase evidence : RESULT lines over ssh: whoami, hostname, Get-BitLockerVolume, sshd state, qemu-ga
#   phase template : sysprep /generalize /oobe /shutdown with clone-unattend.xml, detach ISOs, qm template 9160
set -euo pipefail
VMID=9160; NAME=netpaw-win11-tpl; WIN_ISO=local:iso/Windows_11_02.11.2024.iso; VIRTIO_ISO=local:iso/virtio-win.iso; UNATTEND_ISO=local:iso/netpaw-harness-unattend.iso
BRIDGE_MGMT=vmbr0; BRIDGE_TEST=vmbr-nptest; SSHKEY=${SSHKEY:-/root/.ssh/netpaw-harness}   # staged by run-template-build.sh (claude box) for the run; its trap removes it
[[ -s "$SSHKEY" ]] || { echo "RESULT|gate|refused:no driver key at $SSHKEY (run tools/harness/run-template-build.sh from the claude box, it stages it)"; exit 3; }
phase=${1:-all}
say() { echo "RESULT|$*"; }
load1=$(cut -d' ' -f1 /proc/loadavg); (( $(echo "$load1 >= 20" | bc -l) )) && { say "gate|refused:load $load1"; exit 3; }
h=$(TZ=Europe/Berlin date +%H%M); (( 10#$h < 230 || 10#$h >= 700 )) && { say "gate|refused:window $h Berlin"; exit 3; }
qm status 150 2>/dev/null | grep -q running && { say "gate|refused:vm150 running"; exit 3; }

if [[ $phase == create || $phase == all ]]; then
  if qm status $VMID >/dev/null 2>&1; then say "create|$VMID|exists"; else
    qm create $VMID --name $NAME --pool netpaw-harness --ostype win11 --machine q35 --bios ovmf --cpu host --cores 4 --memory 8192 \
      --scsihw virtio-scsi-single --scsi0 local-lvm:64,discard=on,ssd=1 --efidisk0 local-lvm:1,efitype=4m,pre-enrolled-keys=1 --tpmstate0 local-lvm:1,version=v2.0 \
      --net0 virtio,bridge=$BRIDGE_MGMT,firewall=1 --net1 virtio,bridge=$BRIDGE_TEST,link_down=0 --agent enabled=1 \
      --ide2 $WIN_ISO,media=cdrom --ide0 $UNATTEND_ISO,media=cdrom --sata0 $VIRTIO_ISO,media=cdrom --boot order=ide2 \
      --description "NetPaw harness template (PLAN-G11a step 3). Clones: netpaw-h-<run>, 9161-9168. Built $(date -u +%F)."
    say "create|$VMID|created (ovmf, vTPM, q35, virtio-scsi-single, 64G thin, net0 $BRIDGE_MGMT, net1 $BRIDGE_TEST)"
  fi
fi
if [[ $phase == install || $phase == all ]]; then
  qm status $VMID | grep -q running || qm start $VMID
  say "install|$VMID|started $(date -u +%T); waiting for ssh on net0 (up to 60 min)"
  ip=""; for i in $(seq 1 240); do ip=$(qm guest cmd $VMID network-get-interfaces 2>/dev/null | python3 -c 'import json,sys
try:
  for n in json.load(sys.stdin):
    for a in n.get("ip-addresses",[]):
      if a["ip-address-type"]=="ipv4" and not a["ip-address"].startswith(("127.","169.254.","10.99.")): print(a["ip-address"]); raise SystemExit
except Exception: pass' 2>/dev/null | head -1); [[ -n "$ip" ]] && ssh -o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=no -i "$SSHKEY" harness@"$ip" 'hostname' >/dev/null 2>&1 && break; sleep 15; done
  [[ -n "$ip" ]] || { say "install|$VMID|FAILED no ssh after 60 min"; exit 1; }
  say "install|$VMID|ssh up at $ip after $((i*15)) s"; echo "$ip" > /root/netpaw-tpl.ip
  # clone-unattend.xml must be on C: BEFORE the unattend ISO goes away (the template phase needs it for sysprep).
  # The CD letter is not fixed (ide2/ide0/sata0 order), so find it by the ISO's volume label; scp from pve as fallback.
  S="ssh -o BatchMode=yes -o StrictHostKeyChecking=no -i $SSHKEY harness@$ip"
  bytes=$($S 'New-Item -ItemType Directory -Force C:\harness | Out-Null; $d = (Get-Volume -FileSystemLabel HARNESS -ErrorAction SilentlyContinue).DriveLetter; if ($d) { Copy-Item "${d}:\clone-unattend.xml" C:\harness\clone-unattend.xml -Force }; if (Test-Path C:\harness\clone-unattend.xml) { (Get-Item C:\harness\clone-unattend.xml).Length } else { 0 }' | tr -d '\r' || echo 0)
  if [[ ${bytes:-0} -eq 0 && -s /root/netpaw-harness/clone-unattend.xml ]]; then
    scp -q -o BatchMode=yes -o StrictHostKeyChecking=no -i "$SSHKEY" /root/netpaw-harness/clone-unattend.xml "harness@$ip:C:/harness/clone-unattend.xml" && bytes=$($S '(Get-Item C:\harness\clone-unattend.xml).Length' | tr -d '\r' || echo 0)
    say "install|clone-unattend|copied by scp from pve (no HARNESS volume seen in the guest)"
  fi
  [[ ${bytes:-0} -gt 0 ]] || { say "install|clone-unattend|FAILED not on C:\harness; unattend ISO left attached for a retry"; exit 1; }
  say "install|clone-unattend|staged C:\harness\clone-unattend.xml bytes=$bytes"
  # The filled unattend ISO holds the local admin password in clear text: gone from pve as soon as setup is done (m-8466).
  qm set $VMID --delete ide0 >/dev/null 2>&1 || true
  rm -f /var/lib/vz/template/iso/netpaw-harness-unattend.iso
  say "install|unattend-iso|detached and deleted from local:iso $([[ -e /var/lib/vz/template/iso/netpaw-harness-unattend.iso ]] && echo FAILED || echo ok)"
fi
if [[ $phase == evidence || $phase == all ]]; then
  ip=$(cat /root/netpaw-tpl.ip); S="ssh -o BatchMode=yes -o StrictHostKeyChecking=no -i $SSHKEY harness@$ip"
  say "evidence|whoami|$($S 'whoami /user | Select-Object -Last 1' | tr -d '\r')"
  say "evidence|hostname|$($S hostname | tr -d '\r')"
  say "evidence|bitlocker|$($S 'Get-BitLockerVolume | ForEach-Object { $_.MountPoint + \"=\" + $_.ProtectionStatus }' | tr -d '\r' | paste -sd,)"
  say "evidence|sshd|$($S '(Get-Service sshd).Status, (Get-Service sshd).StartType' | tr -d '\r' | paste -sd,)"
  say "evidence|qemu-ga|$(qm agent $VMID ping >/dev/null 2>&1 && echo ok || echo absent)"
  say "evidence|prevent-device-encryption|$($S 'Get-ItemPropertyValue HKLM:\SYSTEM\CurrentControlSet\Control\BitLocker PreventDeviceEncryption' | tr -d '\r')"
  say "evidence|sshd-password-auth|$($S 'Select-String -Path C:\ProgramData\ssh\sshd_config -Pattern "^PasswordAuthentication" | ForEach-Object Line' | tr -d '\r' | paste -sd,)  (must be: PasswordAuthentication no)"
  say "evidence|sshd-authorized-keys|$($S '(Get-Content C:\ProgramData\ssh\administrators_authorized_keys | Measure-Object -Line).Lines' | tr -d '\r') key line(s) for Administrators; no per-user authorized_keys"
  say "evidence|autologon|AutoAdminLogon=$($S 'Get-ItemPropertyValue "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" AutoAdminLogon' | tr -d '\r') DefaultPassword=$($S 'if (Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon" -Name DefaultPassword -ErrorAction SilentlyContinue) { "PRESENT" } else { "absent" }' | tr -d '\r')  (must be: 0 / absent)"
  say "evidence|unattend-iso-gone|$([[ -e /var/lib/vz/template/iso/netpaw-harness-unattend.iso ]] && echo NO || echo yes)"
fi
if [[ $phase == template || $phase == all ]]; then
  ip=$(cat /root/netpaw-tpl.ip); S="ssh -o BatchMode=yes -o StrictHostKeyChecking=no -i $SSHKEY harness@$ip"
  bytes=$($S 'if (Test-Path C:\harness\clone-unattend.xml) { (Get-Item C:\harness\clone-unattend.xml).Length } else { 0 }' | tr -d '\r' || echo 0)
  [[ ${bytes:-0} -gt 0 ]] || { say "template|clone-unattend|FAILED C:\harness\clone-unattend.xml missing (install phase stages it)"; exit 1; }
  say "template|clone-unattend|bytes=$bytes"
  $S 'Start-Process C:\Windows\System32\Sysprep\sysprep.exe -ArgumentList "/generalize /oobe /shutdown /unattend:C:\harness\clone-unattend.xml"' || true
  for i in $(seq 1 80); do qm status $VMID | grep -q stopped && break; sleep 15; done
  qm status $VMID | grep -q stopped || { say "template|$VMID|FAILED sysprep did not shut down in 20 min"; exit 1; }
  qm set $VMID --delete ide0 --delete ide2 --delete sata0 --boot order=scsi0 >/dev/null
  qm template $VMID
  say "template|$VMID|generalised and converted $(date -u +%T); $(lvs pve/data -o data_percent --noheadings | tr -d ' ')% thin pool"
fi
