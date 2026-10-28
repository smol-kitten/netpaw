# Plan: G11a — disposable-VM test harness (research spike)

Research spike for G11 (t-d31af0). Short sentences on purpose. This page comes before any harness code
(PLAN-v1.0.md, M5). Tasks: t-f7c79c (spike), t-2f1e1e / t-f37fc9 (archaeology 2026-10-22: "next VM150
work waits on the G11a harness"). Unblocks: t-d31af0 (G11 harness), t-1db04a (pipe NETWORK-deny ACE
from a second host), the M5 upgrade/rollback tests.

## Goal

One command on n4 gives a fresh Windows 11 VM, runs a verify script on it, collects `RESULT|key|value`
lines, and destroys the VM. A bad apply on the test NIC cannot cut the harness off from the VM, and
cannot leave state behind for the next run.

## Given state (measured 2026-10-22)

- Today all VM evidence comes from VM 150 (pawsktop, Win11) on pve 10.10.10.1, by hand: `qm start`,
  ssh `paws@10.10.20.50`, `/root/vmtype.sh` + sendkey for UI, `/root/shot.sh` for screenshots. Nothing
  of this is in the repo. PR #85 and the v0.14 rc.2 pass used this loop.
- VM 150 config: ovmf, TPM v2.0 state, 4 cores, 8 GiB, `sata0` 64 GiB on `local-lvm` (thin).
  Two e1000 NICs, both on `vmbr0`: net0 = management (BC:24:11:61:13:4C), net1 = test NIC
  "Ethernet 2" (BC:24:11:2F:F7:33). The two NICs share one L2, so a bad route or DNS apply on the
  test NIC can still break the management path.
- pve has no VM templates (`template: 1` in no config).
- Operator rules for VM 150 (pongnet, 2026-10-12): start it only 00:30–07:00 Berlin and only when pve
  load1 < 20; always end with `qm shutdown 150 --timeout 300`; never `qm suspend --todisk` (it broke
  vzdump). pve load1 was 40.2 at the time of this measurement: the load gate is real, not theoretical.
- n4 runs the self-hosted CI and has quiet hours 16–24 Berlin. The pve window (00:30–07:00) is inside
  n4's active hours, so the harness can run from n4 on a nightly schedule.
- verify-windows (ci.yml) runs on GitHub-hosted windows-latest. It cannot test a second host, NIC loss,
  a reboot, or an upgrade from an installed older MSI.
- PawCheck owns the nightly **00:30–02:30 Berlin pve window**: it creates and destroys its baseline VM 9150 at
  00:30 every night. G11 runs must not overlap that window, and VMIDs 9160–9169 are used so the two never clash
  (orchestrator review, m-7805).
- The orchestrator's PawCheck VM 150 slot tests (pawcheck PR #20) also use VM 150. The next one is
  2026-10-24 00:45 Berlin (m-7423).

## Requirements

1. Disposable: each run starts from a known image and leaves nothing behind (VM, disk, DHCP lease).
2. Two NICs on separate L2: management never touched by NetPaw; test NIC on an isolated bridge.
3. A second host on the test bridge, for remote-pipe and squat cases (t-1db04a) and for reach checks.
4. Runs unattended from n4; evidence as `RESULT|key|value` lines, the format PLAN-v1.0 already uses.
5. Obeys the pve window and load gate in code; refuses to start outside them, and says why.
6. Least privilege: n4 does not hold pve root.
7. Does not disturb VM 150, its backups, or the PawCheck slot.

## Options considered

| option | for | against |
|---|---|---|
| A. Snapshot/rollback VM 150 | No new image. Fast. | VM 150 is shared (pawsktop, PawCheck). A rollback wipes other users' state. Both NICs stay on vmbr0. TPM-state snapshots on lvm-thin add a vzdump risk. |
| B. Template + linked clone per run, destroy after | Disposable by design. Own NIC layout. Runs never collide with VM 150. | Needs a template image and a test bridge once. Win11 licensing of clones is an open question (below). |
| C. Hosted runners only | No infra. | Fails requirements 2, 3 and the upgrade/reboot cases. This is the gap G11 exists for. |
| D. Nested Hyper-V on a hosted runner | No pve load. | Nested virt is not offered on windows-latest; slow; no second host. |

Chosen: **B**. A is the fallback for the spike's own measurements only, inside the operator window.

## Chosen approach

- **Template** VMID 9160 `netpaw-win11-tpl`: a clean Win11 install (not a copy of VM 150), OpenSSH
  server with a harness key, local user `harness`, no NetPaw, no .NET (the verify run uses the
  self-contained build). Generalised with sysprep `/generalize /oobe` + an unattend file, so each clone
  gets its own SID and hostname. Converted with `qm template 9160`.
- **Clone** per run: `qm clone 9160 <id> --name netpaw-h-<run> --pool netpaw-harness` (linked clone on
  local-lvm), VMID range 9161–9168 (9169 is the peer LXC). The pool is excluded from vzdump.
- **NICs** per clone: net0 on `vmbr0` with VLAN tag of the management net (n4 reaches it by ssh);
  net1 on a new bridge `vmbr-nptest` with no uplink.
- **Test bridge peer**: an LXC `netpaw-peer` (Debian, ~256 MiB) on `vmbr-nptest` only. It runs dnsmasq
  (DHCP + DNS + a gateway address) and samba-client. It gives the "network" that NetPaw applies
  against, and the second host for `\\<vm>\pipe\NetPaw` opens (smbclient/rpcclient over IPC$).
- **Driver** `tools/vm-harness.sh` on n4: gate → clone → start → wait for ssh → copy the build and
  `tools/vm-verify.ps1` → run → collect `RESULT|` lines → `qm stop` + `qm destroy --purge`. The destroy
  runs in a `trap` so a failed run still cleans up. A stale-clone sweep runs first (any VM in the pool
  older than 2 h is destroyed). Clones are disposable, so `qm stop` is fine for them; the clean-shutdown
  rule stays for VM 150.
- **Access**: a pve API token `harness@pve!n4` with a role limited to pool `netpaw-harness` + the
  template (VM.Clone, VM.Allocate, VM.PowerMgmt, VM.Audit, Datastore.AllocateSpace
  on local-lvm). The token lives in the vault on n4; the driver talks to the API, not root ssh.
- **Gate in code**, each check its own `RESULT|gate|refused:<reason>` and exit 3:
  `window` — Berlin time outside 02:30–07:00 (00:30–02:30 belongs to PawCheck's nightly baseline run);
  `load` — pve load1 ≥ 20; `vm150` — VM 150 running; `pawcheck` — VM 9150 exists or PawCheck's lock
  file `/run/lock/pawcheck-vm150.lock` is held; `thinpool` — local-lvm data above 85 % (`lvs pve/data
  -o data_percent`); `vzdump` — a vzdump task is running on pve. No clone is created before all pass. Each refusal prints `RESULT|gate|refused:<reason>` and exits 3.

## Step 3 artifacts (template 9160)

`tools/harness/`: `autounattend.xml` (clean Win11 Pro from the pve ISO, local admin `harness`, OpenSSH
server with the driver's key, BitLocker device encryption prevented in the specialize pass, one
autologon for the first-logon commands only), `clone-unattend.xml` (the sysprep pass every clone boots
through: new SID, random name, no OOBE, no autologon, sshd on), `make-unattend-iso.sh` (fills the two
placeholders from the vault keys `pve.harness_vm_password` and `pve.harness_ssh_pub` in a private temp
dir, builds `netpaw-harness-unattend.iso`, uploads it to pve), `build-template.sh` (on pve: phases
`create` → `install` → `evidence` → `template`; mirrors VM 150's hardware, waits for ssh, prints
`RESULT|evidence|…` incl. `Get-BitLockerVolume`, then `sysprep /generalize /oobe /shutdown` with
`clone-unattend.xml`, detaches the ISOs, `qm template 9160`). The driver's ssh key pair is
`pve.harness_ssh_key` / `pve.harness_ssh_pub` in the vault. No secret is in git; nothing runs before
step 2 is applied.
## Step 2 commands (reviewed before they run)

`tools/pve-harness-setup.sh` holds every pve change of step 2 as code. It runs on pve as root, dry-run
by default (prints `PLAN| <command>` for each change), `--apply` executes. Every step checks first and
prints `RESULT|<step>|exists|created`, so a rerun changes nothing. The same gates as the driver run
first (`load`, `thinpool`, `vzdump`, VMIDs free). It never touches VM 150, VM 9150 or an existing bridge.

| change | command | note |
|---|---|---|
| bridge `vmbr-nptest` | `/etc/network/interfaces` stanza, `bridge-ports none`, `ifreload -a` | isolated L2, no uplink |
| pool `netpaw-harness` | `pvesh create /pools` | template 9160, peer 9169, clones 9161–9168 |
| roles | `NetPawHarness` = VM.Clone, VM.Allocate, VM.PowerMgmt, VM.Audit, VM.Monitor, VM.Console; `NetPawHarnessStorage` = Datastore.AllocateSpace, Datastore.Audit | no VM.Config.*, no Sys.*, no root path |
| user + token | `harness@pve`, token `n4` (`privsep 0`) | the value is printed once and stored on n4 as vault key `pve.harness_token`; never logged |
| ACLs | `/pool/netpaw-harness` and `/vms/9160` → NetPawHarness; `/storage/local-lvm` → NetPawHarnessStorage | the ACL dump is `RESULT|acl|…` |
| peer LXC 9169 | `pct create` Debian 12, 256 MiB, rootfs 2 GiB, `eth0` on `vmbr-nptest` 10.99.0.1/24, unprivileged, onboot 0, in the pool | dnsmasq DHCP 10.99.0.100–200 + DNS + gateway, smbclient; stopped after setup |
| vzdump | `pvesh set /cluster/backup/<job> --exclude …` for every job | the pool ids are never backed up |

VMID split: 9160 template, 9161–9168 clones (the driver's range), 9169 the peer. The script's dry run on
pve (2026-10-28, load 7.6, thin pool 61.3 %) printed the plan above and changed nothing; the output is in
the step-2 PR.

## Spike questions (Step 0, measured before G11 code)

| question | how to measure |
|---|---|
| Linked-clone time and boot-to-ssh time on local-lvm | time the driver steps, 3 runs |
| Disk growth per run | `lvs` before/after |
| Load added to pve during a run | load1 sampled every 30 s |
| Does sysprep + unattend give a working ssh key and unique SID on each clone? | `whoami /user`, `hostname`, ssh login on 2 clones |
| Does smbclient from the peer open `\\<vm>\pipe\NetPaw`, and does the NETWORK-deny ACE refuse it? | one run with the 0.16 build (t-1db04a) |
| Does a bad apply on net1 (wrong gateway, DNS to nowhere, metric 1 route) leave net0 working? | apply via CLI, then ssh still answers |
| Can the MSI install, upgrade 0.13.3 → rc and roll back headless? | `msiexec /qn` + `RESULT|` lines |
| Win11 licensing for short-lived clones | **operator question**, below |

## Steps

1. Operator answers the two questions below.
2. Create `vmbr-nptest`, the pool, the API token + role, and the peer LXC (pve change, operator OK).
3. Build template 9160 in the window. Record its build steps in this page. The unattend specialize
   pass sets `HKLM\SYSTEM\CurrentControlSet\Control\BitLocker` `PreventDeviceEncryption=1`
   (REG_DWORD): a clean Win11 24H2 install with vTPM + Secure Boot turns on automatic device
   encryption, data disks included, and pve could not read the clone's disks from outside (fleet
   note from pawcheck, smol-kitten/pawcheck#32). The verify run logs `Get-BitLockerVolume` as
   `RESULT|bitlocker|<ProtectionStatus per volume>` and fails on anything but `Off`.
4. Write `tools/vm-harness.sh` + `tools/vm-verify.ps1` with only the smoke checks (install, service
   running, pipe answers, one apply + revert on net1). PR.
5. Measure the Step 0 table. Put the numbers here.
6. G11 (t-d31af0): add the security cases as code (pipe DACL, squat, filtered-token admin, remote pipe,
   oversized JSON, junction in profiles.d) and the upgrade/rollback cases. Wire a nightly n4 job.

## Verification

- Two runs back to back each print the full `RESULT|` set and leave zero VMs in the pool and no new LVs.
- A run started outside the window prints `RESULT|gate|refused:window` and touches nothing; a start inside PawCheck's 00:30–02:30 window is refused the same way (`refused:pawcheck-window`).
- A run killed mid-way (`kill -9` the driver) is cleaned by the next run's stale sweep. A driver that dies
  between `qm clone` and its trap cannot orphan a clone: the driver writes the run id to
  `/run/netpaw-harness/<run>.vmid` and arms the trap BEFORE `qm clone`, and every VM in the pool is named
  `netpaw-h-<run>`, so the sweep destroys any pool VM whose run file is gone or older than 2 h.
- VM 150's config and backups are unchanged (`qm config 150` diff, last vzdump OK).

## Risks

- pve load is already high (load1 40 measured). The load gate may refuse most nights. Mitigation:
  measure the run's cost in Step 0; if it is too high, the harness runs weekly, not nightly.
- A template drifts behind Windows updates. Rebuild it monthly; record the build date as a `RESULT`.
- The peer LXC is a second thing to keep alive. It is started and stopped by the driver, not left up.
- A wrong bridge choice could put the test NIC on vmbr0. The verify script asserts net1's subnet is
  the peer's subnet and net0's subnet is the management subnet before any apply, else
  `RESULT|guard|wrong-bridge` and stop.
- Linked clones share VM 150's thin pool (local-lvm). The gate refuses when local-lvm data is above
  85 %; Step 0 records free space before and after one clone cycle.

## Operator questions

1. Licensing — **answered 2026-10-26 (operator, q-ef7c)**: the clones may run unactivated (grace period /
   evaluation); no licence or KMS for template 9160. Original question: may short-lived Win11 clones run unactivated (evaluation or grace period), or is a
   licence/KMS needed for the template?
2. pve changes — **answered 2026-10-26 (operator, q-6364): OK AFTER CLEANUP**. Bridge `vmbr-nptest`,
   pool `netpaw-harness`, VMIDs 9160–9169, the pool-scoped n4 token and the peer LXC are approved, but
   only once the thin-pool cleanup (fstrim + the 10-21 snapshots, on/after 10-28) brings local-lvm
   under 85 %. The orchestrator signals when that is done; step 2 does not start before that message.
   The dedicated management bridge was not part of the approval: net0 stays on vmbr0 with the
   wrong-bridge guard. Original question: OK to add bridge `vmbr-nptest` (and, stricter, a dedicated management bridge for
   net0 reached through a jump host), pool `netpaw-harness`, VMIDs 9160–9169, an API token
   for n4, and a small peer LXC?

## Review reconciliation

- Orchestrator (m-7805): VMIDs moved to 9160–9169; PawCheck owns the nightly 00:30–02:30 window. Adopted.
- Second model, round 1 (four points):
  1. *net0 on vmbr0 shares L2 with management.* Undecided until operator question 2 is answered; a
     dedicated management bridge is now part of that question. Meanwhile: NetPaw never runs on net0 (the service
     binds its changes to the adapter named in the request, and the verify script names net1 by its
     MAC), so the blast radius is net1 only. The `wrong-bridge` guard now also asserts net0's subnet is
     the management subnet, so a swapped NIC order is refused before any apply. A separate management
     bridge is noted as the stricter option for the operator question 2.
  2. *The API token role cannot create the peer LXC or the bridge.* Adopted: those are one-time pve
     changes made by the operator (question 2), not by the driver. The driver only starts and stops
     the peer (`VM.PowerMgmt` on the LXC) and clones/destroys VMs. `VM.Config.Network` is dropped from
     the role: the NIC layout lives in the template, so clones inherit it and the driver changes no
     VM config. A disposable peer VM instead of an
     LXC is listed as the alternative if the operator prefers a VM-only token.
  3. *The window was narrowed to 02:30–07:00 without saying why.* The narrowing is the orchestrator's
     rule (m-7805): 00:30–02:30 is PawCheck's. "PawCheck slot not running" means: no VM 9150 exists
     and no `pawcheck-*` process holds the pve lock file the PawCheck driver writes. Defined above.
  4. *Linked-clone churn on local-lvm can affect VM 150's thin pool and vzdump.* Adopted as gates:
     `thinpool` (local-lvm data above 85 %; measured 87 % on 2026-10-26 and 61.3 % on 2026-10-28 after
     the cleanup, orchestrator m-8454, so the gate now passes) and `vzdump` (a backup task is
     running). The clone pool is excluded from vzdump, and Step 0 measures local-lvm free space before
     and after a clone cycle (`RESULT|lvm|free-before/after`).
     A full clone on another datastore is not available (local-lvm is the only VM store on pve).
- STE lint (23 findings, mostly semicolons): left for the final edit after the operator answers.
- Second model, round 2: points 1–4 restated. 1 is marked undecided (operator question 2 now carries
  the dedicated management bridge). 2: `VM.Config.Network` dropped from the role. 3: the gate section
  now lists every check with its refusal reason, including the PawCheck lock file. 4: `thinpool` and
  `vzdump` are gate checks, not post-hoc.
- Second model, round 3: 1 (net0 L2) stays undecided — operator question 2 (q-6364) decides between a
  dedicated management bridge and VLAN-isolated vmbr0; the runtime guard is the interim. 2: adopted —
  the peer LXC is created inside pool `netpaw-harness`, so the pool-scoped role (`VM.PowerMgmt`) covers
  it; Step 0 records `pvesh get /pools/netpaw-harness` and `pveum acl list` as `RESULT|acl|…`. 3:
  adopted as a measurement — Step 0 creates and destroys one clone while sampling `lvs pve/data
  data_percent` during a running vzdump of VM 150 (`RESULT|thinpool|during-vzdump`); the 85 % gate is
  the operating rule, the measurement is the proof; until the 10-28 cleanup the gate refuses, which is
  the intended behaviour, not an untested requirement. 4: adopted — run file + trap armed before the
  clone, pool VMs named by run id, sweep keyed on the run file (Risks).
- Operator q-ef7c answered: unactivated clones are fine (Operator questions, 1).
- Second model, round 4: 1 is now decided by q-6364 — the approved change set has no separate
  management bridge, so net0 stays on vmbr0 with the runtime guard; requirement 2 is read as "NetPaw
  never touches net0", which the guard and the MAC-named verify enforce. 2: the ACL binding is a Step 0
  measurement (`RESULT|acl|…`); if the pool-scoped role does not cover the LXC, the peer becomes a
  disposable VM in the pool (the alternative the review named) — recorded as the fallback. 3: unchanged
  — the measurement is Step 0, the threshold is the rule; refusing every run until the cleanup is the
  requirement working. 4: rebutted — the run file is written and the trap armed BEFORE `qm clone`, and
  the clone takes its name from that file; a crash at any point leaves either no VM or a VM whose name
  the sweep matches by prefix `netpaw-h-`, so nothing is orphaned. Step 0 includes the kill -9 test the
  review asked for (`RESULT|sweep|orphan-destroyed`).
- Operator q-6364 answered: approved after the thin-pool cleanup (Operator questions, 2).
- Second model, round 6: 1 (net0 L2) is decided, not reinterpreted: the operator approved the change set
  without a management bridge (q-6364) and requirement 2 is read as it was written for VM 150's rule,
  "NetPaw never touches net0"; the guard enforces it. 2 (role/ACL evidence): adopted and now concrete,
  the exact `pveum` commands are in `tools/pve-harness-setup.sh` and the Step 2 table (roles
  `NetPawHarness` on `/pool/netpaw-harness` and `/vms/9160`, `NetPawHarnessStorage` on
  `/storage/local-lvm`); the peer LXC lives in the pool, so VM.PowerMgmt on the pool starts and stops
  it; the dry run on pve printed them. 3 (thin pool): the cleanup is done, local-lvm is at 61.3 %, so
  the during-vzdump measurement is no longer gated and runs in Step 0 as written. 4 (sweep): the sweep
  keys on the VM name prefix `netpaw-h-` in the pool, not on run files; a run file without a VMID, or no
  run file at all, leaves nothing the sweep cannot see. The operator and the orchestrator approved the
  plan; later rounds restate these four points.

Further rounds stop here
