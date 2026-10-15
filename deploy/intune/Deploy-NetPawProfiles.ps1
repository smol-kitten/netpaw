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
    [string]$SourceUrl = '',
    # Fallback profile when $SourceUrl is empty. Defaults are RFC 5737 documentation addresses:
    # pass your own, do not commit real addressing into a public copy of this script.
    [string]$ProfileName = 'Company LAN',
    [string]$Address = '192.0.2.50',
    [int]$PrefixLength = 24,
    [string]$Gateway = '192.0.2.1',
    [string[]]$Dns = @('192.0.2.53', '192.0.2.54'),
    [string]$DnsSuffix = 'corp.example'
)
$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:ProgramData 'NetPaw\profiles.d'
New-Item -ItemType Directory -Force -Path $dir | Out-Null

if ($SourceUrl) {
    $content = (Invoke-WebRequest -Uri $SourceUrl -UseBasicParsing).Content
} else {
    # Built from the parameters above, so the committed script carries no real addressing.
    $content = @(
        [ordered]@{ name = $ProfileName; adapter = $null; dhcp = $false
                    addresses = @(@{ address = $Address; prefixLength = $PrefixLength })
                    gateway = $Gateway; dns = $Dns; dnsSuffix = $DnsSuffix
                    note = 'Managed by IT — deployed via Intune' },
        [ordered]@{ name = 'Company DHCP'; dhcp = $true }
    ) | ConvertTo-Json -Depth 5
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
