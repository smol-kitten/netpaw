# PLAN-G11a verify script, run inside a clone by the driver. FIRST check, before anything else: the test NIC must sit on
# the isolated peer subnet (10.99.0.0/24 on vmbr-nptest) and the management NIC must NOT. Clones share the template's
# local admin password (m-8466); that is acceptable only while net1 is on the isolated bridge, so a wrong bridge stops
# the run here with RESULT|guard|wrong-bridge and nothing is applied.
param([string]$TestNic = 'Ethernet 2', [string]$MgmtNic = 'Ethernet', [string]$PeerNet = '10.99.0.')
function R($k, $v) { "RESULT|$k|$v" }
$test = Get-NetIPAddress -InterfaceAlias $TestNic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty IPAddress
$mgmt = Get-NetIPAddress -InterfaceAlias $MgmtNic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty IPAddress
if (-not $test -or -not $test.StartsWith($PeerNet)) { R 'guard' "wrong-bridge test NIC '$TestNic' has '$test', expected $PeerNet*"; exit 3 }
if ($mgmt -and $mgmt.StartsWith($PeerNet)) { R 'guard' "wrong-bridge management NIC '$MgmtNic' is on the test subnet ($mgmt)"; exit 3 }
R 'guard' "ok test=$test mgmt=$mgmt"
# --- the verify steps follow in the driver PR (PLAN-G11a step 4): apply on $TestNic only, revert, remote pipe open from the peer, RESULT lines.
