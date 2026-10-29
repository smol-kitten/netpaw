# PLAN-G11a verify script, run inside a clone by tools/vm-harness.sh. Prints RESULT|key|value lines; exit 0 only when every
# smoke check passed. FIRST check, before anything else: the test NIC must sit on the isolated peer subnet (10.99.0.0/24 on
# vmbr-nptest) and the management NIC must NOT. Clones share the template's local admin password (m-8466); that is
# acceptable only while net1 is on the isolated bridge, so a wrong bridge stops the run here with RESULT|guard|wrong-bridge
# and nothing is applied. NetPaw is applied to $TestNic ONLY (named by MAC after the guard); net0 is never touched.
#   -Msi <path>    install this MSI first (msiexec /qn), then verify against the installed CLI
#   -Build <dir>   use a self-contained build dir (netpaw-cli.exe, netpaw-svc.exe) instead: service registered by hand
#   neither        expect NetPaw already installed (upgrade/rollback runs chain several invocations)
param([string]$TestNic = 'Ethernet 2', [string]$MgmtNic = 'Ethernet', [string]$PeerNet = '10.99.0.', [string]$Msi, [string]$Build)
$ErrorActionPreference = 'Continue'; $fail = 0
function R($k, $v) { "RESULT|$k|$v" }
function Bad($k, $v) { R $k "FAIL $v"; $script:fail++ }
$test = Get-NetIPAddress -InterfaceAlias $TestNic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty IPAddress
$mgmt = Get-NetIPAddress -InterfaceAlias $MgmtNic -AddressFamily IPv4 -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty IPAddress
if (-not $test -or -not $test.StartsWith($PeerNet)) { R 'guard' "wrong-bridge test NIC '$TestNic' has '$test', expected $PeerNet*"; exit 3 }
if ($mgmt -and $mgmt.StartsWith($PeerNet)) { R 'guard' "wrong-bridge management NIC '$MgmtNic' is on the test subnet ($mgmt)"; exit 3 }
$testMac = (Get-NetAdapter -Name $TestNic).MacAddress
R 'guard' "ok test=$test ($testMac) mgmt=$mgmt"
R 'host' "$(hostname) $((Get-CimInstance Win32_OperatingSystem).Version) user=$(whoami)"
$bl = Get-BitLockerVolume -ErrorAction SilentlyContinue | ForEach-Object { "$($_.MountPoint)=$($_.ProtectionStatus)" }
if ($bl -match 'On') { Bad 'bitlocker' ($bl -join ',') } else { R 'bitlocker' ($bl -join ',') }

# --- install -----------------------------------------------------------------------------------------------------------
$dir = 'C:\Program Files\NetPaw'
if ($Msi) {
  $p = Start-Process msiexec.exe -ArgumentList "/i `"$Msi`" /qn /l*v C:\harness\install.log" -Wait -PassThru
  if ($p.ExitCode -ne 0) { Bad 'install' "msiexec exit $($p.ExitCode)" } else { R 'install' "msiexec exit 0" }
} elseif ($Build) {
  $dir = $Build
  if (-not (Get-Service netpaw-svc -ErrorAction SilentlyContinue)) {
    sc.exe create netpaw-svc binPath= "`"$Build\netpaw-svc.exe`"" start= demand obj= LocalSystem | Out-Null
    sc.exe privs netpaw-svc SeChangeNotifyPrivilege | Out-Null
  }
  sc.exe start netpaw-svc | Out-Null; Start-Sleep 3
  R 'install' "build dir $Build, service registered by hand"
} else { R 'install' "pre-installed (chained run)" }
$cli = Join-Path $dir 'netpaw-cli.exe'
if (-not (Test-Path $cli)) { Bad 'cli' "missing $cli"; R 'summary' "fail=$fail"; exit 1 }
R 'version' ((& $cli --version 2>&1) -join ' ')
$svc = Get-Service netpaw-svc -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') { R 'service' "running start=$($svc.StartType)" } else { Bad 'service' "status=$($svc.Status)" }
$privs = (sc.exe qprivs netpaw-svc) -join ' '
if ($privs -match 'SeChangeNotifyPrivilege' -and $privs -notmatch 'SeDebugPrivilege') { R 'service-privs' 'SeChangeNotifyPrivilege only' } else { R 'service-privs' "unexpected: $privs" }
$st = & $cli service status 2>&1; $rc = $LASTEXITCODE
if ($rc -eq 0) { R 'pipe' (($st | Select-String 'service|tier') -join '; ') } else { Bad 'pipe' "service status rc=$rc $($st -join ' ')" }
$acl = (Get-Acl '\\.\pipe\NetPaw' -ErrorAction SilentlyContinue).Access | ForEach-Object { "$($_.IdentityReference):$($_.AccessControlType)" }
R 'pipe-dacl' ($acl -join ',')
R 'pipe-network-deny' $(if ($acl -match 'NETWORK:Deny') { 'present' } else { 'not visible via Get-Acl (t-1db04a: verified from the peer in G11, not here)' })

# --- one apply + revert on the test NIC only ----------------------------------------------------------------------------
$before = Get-NetIPConfiguration -InterfaceAlias $TestNic
$beforeMgmt = (Get-NetIPAddress -InterfaceAlias $MgmtNic -AddressFamily IPv4).IPAddress
$prof = @(@{ Name = 'harness-static'; Dhcp = $false; AdapterMac = $testMac; Addresses = @(@{ Address = '10.99.0.42'; PrefixLength = 24 }); Gateway = '10.99.0.1'; Dns = @('10.99.0.1') })
$prof | ConvertTo-Json -Depth 5 | Set-Content C:\harness\profile.json -Encoding UTF8
$imp = & $cli import C:\harness\profile.json 2>&1; R 'import' (($imp -join ' ') + " rc=$LASTEXITCODE")
$ap = & $cli apply harness-static -a $TestNic 2>&1; $rc = $LASTEXITCODE
Start-Sleep 3
$now = Get-NetIPAddress -InterfaceAlias $TestNic -AddressFamily IPv4 | Select-Object -First 1 -ExpandProperty IPAddress
if ($rc -eq 0 -and $now -eq '10.99.0.42') { R 'apply' "rc=0 test NIC now $now" } else { Bad 'apply' "rc=$rc test NIC $now: $($ap -join ' ')" }
$gw = (Get-NetRoute -InterfaceAlias $TestNic -DestinationPrefix 0.0.0.0/0 -ErrorAction SilentlyContinue).NextHop
R 'apply-gateway' "$gw"
$ping = Test-Connection 10.99.0.1 -Count 1 -Quiet -ErrorAction SilentlyContinue
if ($ping) { R 'peer-reach' 'peer 10.99.0.1 answers' } else { Bad 'peer-reach' 'peer 10.99.0.1 does not answer' }
$mgmtNow = (Get-NetIPAddress -InterfaceAlias $MgmtNic -AddressFamily IPv4).IPAddress
if ($mgmtNow -eq $beforeMgmt) { R 'mgmt-untouched' "$mgmtNow" } else { Bad 'mgmt-untouched' "was $beforeMgmt now $mgmtNow" }
$rv = & $cli revert 2>&1; $rc = $LASTEXITCODE
Start-Sleep 5
$dh = (Get-NetIPInterface -InterfaceAlias $TestNic -AddressFamily IPv4).Dhcp
$after = Get-NetIPAddress -InterfaceAlias $TestNic -AddressFamily IPv4 | Select-Object -First 1 -ExpandProperty IPAddress
if ($rc -eq 0 -and $dh -eq 'Enabled' -and $after -ne '10.99.0.42') { R 'revert' "rc=0 dhcp=$dh test NIC now $after" } else { Bad 'revert' "rc=$rc dhcp=$dh ip=$after: $($rv -join ' ')" }
if ($Msi) {
  $p = Start-Process msiexec.exe -ArgumentList "/x `"$Msi`" /qn" -Wait -PassThru
  if ($p.ExitCode -eq 0 -and -not (Get-Service netpaw-svc -ErrorAction SilentlyContinue)) { R 'uninstall' 'msiexec exit 0, service gone' } else { Bad 'uninstall' "exit $($p.ExitCode) service=$((Get-Service netpaw-svc -ErrorAction SilentlyContinue).Status)" }
}
R 'summary' "fail=$fail"
if ($fail -gt 0) { exit 1 } else { exit 0 }
