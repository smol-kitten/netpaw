# NetPaw in the enterprise

Three building blocks, each optional:

| block | what | where |
|---|---|---|
| **Install** | per-machine MSI | `NetPaw-<version>.msi` from Releases |
| **Managed profiles** | read-only profiles every user sees | `%ProgramData%\NetPaw\profiles.d\*.json` |
| **Policy** | pin settings, deny features, pre-set repositories | `HKLM\SOFTWARE\Policies\NetPaw` (ADMX / Intune / script) |

Users can still have their own profiles next to managed ones unless `AllowUserProfiles` is disabled.
Managed profiles show a *managed* badge and cannot be edited or deleted; *Copy* makes a private editable copy.

## 1. Install

### Intune (line-of-business app)
1. *Apps → Windows → Add → Line-of-business app*, upload `NetPaw-<version>.msi`.
2. Install behaviour **System**. Intune reads product code and version from the MSI; no detection rule needed.
3. Command lines, if you prefer a Win32 app (`.intunewin`):
   - install: `msiexec /i NetPaw-<version>.msi /qn`
   - uninstall: `msiexec /x NetPaw-<version>.msi /qn`
   - detection: file `%ProgramFiles%\NetPaw\NetPaw.exe` exists

### GPO
*Computer Configuration → Policies → Software Settings → Software installation → New → Package*, pick the MSI from a share. Assigned, per machine.

### What the MSI does
Installs `NetPaw.exe` and `netpaw-cli.exe` to `%ProgramFiles%\NetPaw`, adds the folder to the system `PATH`, creates `%ProgramData%\NetPaw\profiles.d\`, and a Start-menu shortcut. No service, no scheduled task, no custom actions. Upgrades in place (major-upgrade table). Users start NetPaw themselves or via *Settings → start with Windows* (an elevated per-user scheduled task; the app creates it).

### Portable
The zip needs no install. Managed profiles and policy work exactly the same.

## 2. Managed profiles

Author on a reference PC, then deploy the file:

```
netpaw-cli capture "Company LAN"            # or build them in the editor
netpaw-cli export 10-company-base.json --managed
```

`--managed` strips ids and hotkeys (users bind their own). The file is a JSON list of profiles
(schema: see README). Files load in name order, so `10-…` before `20-…`; a managed name wins over
a user profile with the same name.

Deploy options:

- **Intune platform script**: `deploy/intune/Deploy-NetPawProfiles.ps1` (run as SYSTEM). Paste the
  JSON into the script or set `-SourceUrl` to an HTTPS file. The script also locks the folder to
  read-only for users.
- **Intune Win32 app**: package the JSON with a one-line install `cmd /c copy 10-company-base.json "%ProgramData%\NetPaw\profiles.d\"`, detection = that file exists.
- **GPO**: *Computer Configuration → Preferences → Windows Settings → Files*, source share → `%CommonAppDataDir%\NetPaw\profiles.d\10-company-base.json`.

NetPaw re-reads `profiles.d` at start and on every *Manage profiles* open. Broken files are skipped and logged, never fatal.

## 3. Policy

Registry: `HKLM\SOFTWARE\Policies\NetPaw`. Values:

| value | type | effect |
|---|---|---|
| `WorkAdapter` | REG_SZ | pin the adapter; the settings field is greyed |
| `PanelHotkey` | REG_SZ | pin the panel hotkey |
| `ConfirmBeforeApply` | DWORD 0/1 | force the plan preview on/off |
| `RepoUrls` | REG_MULTI_SZ | repositories every user gets; `url|keyId:base64key` pins a signing key |
| `AllowUserProfiles` | DWORD | 0 = only managed profiles; the user's file is ignored |
| `AllowUserRepos` | DWORD | 0 = users cannot add repositories |
| `AllowReach` | DWORD | 0 = no reach mode (panel + CLI) |
| `AllowTempAddresses` | DWORD | 0 = reach may only switch to saved profiles; presets hidden |
| `AllowDhcp` | DWORD | 0 = no DHCP button/command |
| `AllowUnsignedRepos` | DWORD | 0 = repositories need a valid signature and a pinned key |
| `AllowTelemetry` | DWORD | 0 = the telemetry build sends nothing (the standard build has no telemetry code) |
| `AllowAutoSwitch` | DWORD | 0 = profiles never apply themselves on link-up |
| `AllowScan` | DWORD | 0 = no scan dialog / find-routers (they apply profiles and borrow addresses) |
| `AllowCapture` | DWORD | 0 = no pktmon capture (LLDP/CDP discovery) |

Every `Allow*` defaults to allowed. Denied actions disappear from the panel and menu; `netpaw-cli` exits with code **4**. `netpaw-cli policy` prints the effective policy on a device.

### GPO (ADMX)
Copy `deploy/gpo/NetPaw.admx` to `%SystemRoot%\PolicyDefinitions\` (or the central store) and
`deploy/gpo/en-US/NetPaw.adml` to `…\PolicyDefinitions\en-US\`. Policies appear under
*Computer Configuration → Administrative Templates → NetPaw*.

### Intune (ADMX ingestion)
1. *Devices → Configuration → Create → Templates → Imported Administrative templates*, import `NetPaw.admx` + `NetPaw.adml`.
2. Create a profile from *Settings catalog / Imported ADMX*; the NetPaw policies are listed by name.
3. Manual OMA-URI alternative: ingest with
   `./Device/Vendor/MSFT/Policy/ConfigOperations/ADMXInstall/NetPaw/Policy/NetPawAdmx` (value = ADMX file content), then set e.g.
   `./Device/Vendor/MSFT/Policy/Config/NetPaw~Policy~NetPaw/AllowReach` to `<disabled/>`.

### Script (no ADMX)
`deploy/intune/Set-NetPawPolicy.ps1` writes the registry values directly; edit the hashtable, deploy as an Intune platform script (SYSTEM). Same for any RMM.

## Checklist for a locked-down fleet

```
AllowUserProfiles = 0      # only IT's profiles
AllowReach        = 0      # no ad-hoc addressing
AllowDhcp         = 1      # but DHCP is always fine
RepoUrls          = https://intranet.example/netpaw/index.json|kid1:BASE64KEY
AllowUserRepos    = 0
AllowUnsignedRepos= 0
```
