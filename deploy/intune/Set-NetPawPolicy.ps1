<#
.SYNOPSIS
  Sets NetPaw machine policy without GPO/ADMX (Intune platform script, run as SYSTEM).
  Edit the hashtable. Missing keys are removed so the script is the single source of truth.
#>
$ErrorActionPreference = 'Stop'
$policy = @{
    # WorkAdapter        = 'Ethernet'
    # PanelHotkey        = 'Ctrl+Alt+N'
    ConfirmBeforeApply   = 0
    RepoUrls             = @('https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json')
    AllowUserProfiles    = 1
    AllowUserRepos       = 1
    AllowReach           = 1
    AllowTempAddresses   = 1
    AllowDhcp            = 1
    AllowUnsignedRepos   = 1
}
$key = 'HKLM:\SOFTWARE\Policies\NetPaw'
New-Item -Path $key -Force | Out-Null
foreach ($name in 'WorkAdapter','PanelHotkey','ConfirmBeforeApply','RepoUrls','AllowUserProfiles','AllowUserRepos','AllowReach','AllowTempAddresses','AllowDhcp','AllowUnsignedRepos') {
    if ($policy.ContainsKey($name)) {
        $v = $policy[$name]
        $type = if ($v -is [array]) { 'MultiString' } elseif ($v -is [int]) { 'DWord' } else { 'String' }
        New-ItemProperty -Path $key -Name $name -Value $v -PropertyType $type -Force | Out-Null
    } else {
        Remove-ItemProperty -Path $key -Name $name -ErrorAction SilentlyContinue
    }
}
Write-Output "NetPaw policy set: $($policy.Keys -join ', ')"
