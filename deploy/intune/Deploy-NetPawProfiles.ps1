<#
.SYNOPSIS
  Drops managed NetPaw profiles into %ProgramData%\NetPaw\profiles.d (Intune platform script or GPO startup script).
.DESCRIPTION
  Paste the JSON produced by "netpaw-cli export base.json --managed" into $Profiles below, or point
  $SourceUrl at an HTTPS location your devices can reach. Run as SYSTEM (Intune: "Run this script
  using the logged on credentials = No"). Idempotent: rewrites the file only when content changed.
#>
param(
    [string]$FileName = '10-company-base.json',
    [string]$SourceUrl = ''
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:ProgramData 'NetPaw\profiles.d'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

if ($SourceUrl) {
    $content = (Invoke-WebRequest -Uri $SourceUrl -UseBasicParsing).Content
} else {
    $content = @'
[
  {
    "name": "Company LAN",
    "adapter": null,
    "dhcp": false,
    "addresses": [ { "address": "10.10.0.50", "prefixLength": 16 } ],
    "gateway": "10.10.0.1",
    "dns": [ "10.10.0.53", "10.10.0.54" ],
    "dnsSuffix": "corp.example",
    "note": "Managed by IT — deployed via Intune"
  },
  { "name": "Company DHCP", "dhcp": true }
]
'@
}

$target = Join-Path $dir $FileName
if (-not (Test-Path $target) -or (Get-Content $target -Raw) -ne $content) {
    Set-Content -Path $target -Value $content -Encoding UTF8
    Write-Output "wrote $target"
} else {
    Write-Output "unchanged $target"
}
# Lock the folder down: SYSTEM + Administrators full, Users read-only.
icacls $dir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' 'BUILTIN\Users:(OI)(CI)R' | Out-Null
