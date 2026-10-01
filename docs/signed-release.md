# Signed releases

From v0.13.0, every NetPaw release file is signed by the catboy.systems PKI. This page explains how the
signing chain works, how to check a download yourself, what the staging root means for Windows, and how
to go back.

## The chain

```
Marc Schneider Root CA R0 (Staging)          sha256 ed5eaa1b7a66e154e8c82a3d45f65ddd36a3fbbad1f41a68d2bf4334ccc2d375
├── catboy.systems CA CB0 (Staging)
│   └── per-runner issuer → 1-hour leaf "smol-kitten/netpaw"   signs the files
└── catboy.systems Timestamp CA T0 (Staging) → TSA             RFC 3161 timestamp on every signature
```

The release workflow (`.github/workflows/ci.yml`) uses the fleet workflow
`workcollection/fleet-actions/.github/workflows/catboy-sign.yml@v2.1.5`:

1. **build** (Linux runner): tests, then the framework-dependent, standalone and (optional) telemetry
   builds.
2. **sign-bin**: Authenticode on `NetPaw.exe` and `netpaw-cli.exe`. The GitHub OIDC token of the run logs
   in to the CA. The CA issues a leaf that lives for one hour. Each signature gets a timestamp, so it
   stays valid after the leaf expires.
3. **msi** (hosted Windows runner, because WiX cannot build on Linux): packs the **signed** binaries into
   the MSI and the zips. What you install or unzip is therefore signed too.
4. **sign-pkg**: Authenticode on the MSI(s). Each zip gets a detached CMS signature `<zip>.p7s` and an
   RFC 3161 token `<zip>.p7s.tsr`.
5. **verify-signed** (Linux): `tools/verify-release.sh` checks every file against R0 and checks the hashes
   in `SIGNATURES.md`. It also runs NetPaw's own update check (`netpaw-cli update verify`) on the MSI.
6. **verify-windows** (hosted Windows): `Get-AuthenticodeSignature` before and after R0 is imported, then
   install, run and uninstall the signed MSI. The installed files must have the same hashes as the signed
   files.
7. **release**: publishes the signed files, `SIGNATURES.md` (both passes), `r0.crt` and the winget facts.
   The winget `installerSha256` is the hash of the **signed** MSI.

Signing happens on `v*` tags only. Branch and PR runs pass the same files through unsigned (gate 1), and a
tag that does not sign fails (`strict`).

## Check a download

Each release contains `SIGNATURES.md` (file, sha256, signature type) and `r0.crt`. **Check the root
first.** Its SHA-256 must be `ed5eaa1b7a66e154e8c82a3d45f65ddd36a3fbbad1f41a68d2bf4334ccc2d375`. The same
value is in this page, in `deploy/pki/r0.crt` in the repository, and in NetPaw itself
(`UpdateTrust.R0Sha256`).

### Linux (or WSL)

```sh
git clone https://github.com/smol-kitten/netpaw && cd netpaw
mkdir dl && cd dl && gh release download v0.13.0 -R smol-kitten/netpaw && cd ..
tools/verify-release.sh dl          # needs osslsigncode and openssl; exit 0 = everything verified
```

The script runs the recipe from the fleet-actions README. To check the exes inside a zip as well, unzip
it next to `SIGNATURES.md` and run the script on that folder: each exe is listed there by its path. To check a single file by hand:

```sh
cat deploy/pki/r0.crt deploy/pki/cb0.crt deploy/pki/t0.crt > tsa-ca.pem
osslsigncode verify -in NetPaw-0.13.0.msi -CAfile deploy/pki/r0.crt -TSA-CAfile tsa-ca.pem
```

### Windows

```powershell
Get-AuthenticodeSignature .\NetPaw-0.13.0.msi | Format-List Status, StatusMessage, SignerCertificate, TimeStamperCertificate
(Get-FileHash .\NetPaw-0.13.0.msi -Algorithm SHA256).Hash    # compare with SIGNATURES.md
```

With the staging root, `Status` is **UnknownError** or **NotTrusted** ("terminated in a root certificate
which is not trusted"). This is expected. Check that the signer is `smol-kitten/netpaw`, that a
timestamp signer is present and that the sha256 is in `SIGNATURES.md`. To see **Valid**, trust R0 on a
test machine (next section).

NetPaw can check an installer the same way it checks its own updates:
`netpaw-cli update verify NetPaw-0.13.0.msi SIGNATURES.md` (exit 0 = verified).

## Staging caveat: "unknown publisher"

R0 is a **staging** root. Windows does not trust it, so SmartScreen and UAC still show "Unknown
publisher" when you run a signed file. That stays until a production root exists and is trusted by the
machine (for a managed fleet: distributed by GPO or Intune). NetPaw's own update check does not depend on
the Windows store: it trusts only the roots built into it (R0 now, with a slot for the production root).

To trust R0 on a **test** machine (elevated PowerShell):

```powershell
Import-Certificate -FilePath .\r0.crt -CertStoreLocation Cert:\LocalMachine\Root
# remove it again:
Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq (Get-PfxCertificate .\r0.crt).Thumbprint | Remove-Item
```

Do not import a staging root on machines that you do not use for testing.

## The update check

When the update check is on (Settings → Updates; policy `AllowUpdateCheck`), NetPaw downloads the
installer of a newer release for its own variant (`NetPaw-<v>.msi` or `NetPaw-<v>-telemetry.msi`) and
the release's `SIGNATURES.md`. It accepts only downloads from `github.com/smol-kitten/netpaw/releases`.
It offers *Install* only when all of these checks pass:

- the sha256 of the file is listed in `SIGNATURES.md`;
- the Authenticode digest of the MSI matches the signature, and the signature verifies;
- an RFC 3161 timestamp is present and covers that signature;
- the signer and the timestamp authority chain to a **pinned** root, valid at the timestamp time.

If a check fails, the balloon says **NOT verified** with the reason, and a click opens the release page.
The file is not written to disk, so nothing can run it. A verified file is checked again just before
`msiexec` starts. Releases before v0.13.0 have no `SIGNATURES.md`, so for those the balloon says "NOT
verified (unsigned release)".

VirusTotal is not part of this check: it is a one-time report per release (below), not a runtime gate.

## Rollback

The previous unsigned release (v0.12.2) stays on the releases page. To go back, uninstall NetPaw and
install the older MSI, or unzip the older zip. The settings in `%APPDATA%\NetPaw` stay in place. The
older version does not install updates; it only links to the release page.

If a signed release itself is bad, publish a fixed one under a new tag. Never replace the assets of a
published release: the winget manifest and `SIGNATURES.md` refer to the exact bytes.

## Release record

| release | verify-signed | verify-windows | VirusTotal |
|---|---|---|---|
| v0.13.0-rc.3 (prerelease, [run 36892929504](https://github.com/smol-kitten/netpaw/actions/runs/36892929504)) | 20 verified, 0 failed (osslsigncode 2.8); `update verify` ok on both MSIs | UnknownError → R0 imported → Valid; signed MSI installs; installed hashes = signed | not scanned (prerelease) |
| v0.13.0 | _filled in after the release_ | | |

Notes from the release candidates:

- rc.1: signing worked, including the MSI. Verification failed because the R0/CB0/T0 files were DER, not
  PEM, and because osslsigncode's own CRL downloads failed intermittently. Fixed in #65.
- rc.2: both signing passes ran on the same runner. The second signed artifact then also held the
  first pass's files, because catboy-sign v2.1.5 does not clean its `dist/` folder. Since #66 the release
  fails when a signed artifact holds a file that did not go in. One runner had no osslsigncode, so the
  job now installs it.
- Neither candidate was published. The release job runs only after both verify jobs pass.
