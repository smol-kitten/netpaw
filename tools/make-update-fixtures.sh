#!/usr/bin/env bash
# Regenerates the offline fixtures for the update verifier tests (tests/NetPaw.Core.Tests/Fixtures/update).
# A throwaway test PKI shaped like the real one (root → CA → 1-hour code-signing leaf, root → TSA), a tiny
# MSI from msitools, and osslsigncode's built-in timestamping at a FIXED time, so the files never expire
# and the tests need no network. Needs: openssl >= 3.4, osslsigncode, msibuild (msitools), python3.
set -euo pipefail
OUT=${1:-$(cd "$(dirname "$0")/.." && pwd)/tests/NetPaw.Core.Tests/Fixtures/update}
T=1790769600                     # 2026-09-30T12:00:00Z: signing time; the TSA stamps T+60
NB=20260930113000Z; NA=20260930123000Z   # the leaf lives one hour, like a catboy-sign run leaf
w=$(mktemp -d); trap 'rm -rf "$w"' EXIT; cd "$w"

key() { openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:"$2" -out "$1.key" 2>/dev/null; }
root() { key "$1" P-384; openssl req -x509 -new -key "$1.key" -subj "/O=NetPaw tests/CN=$2" -not_before 20260101000000Z -not_after 20360101000000Z \
           -addext basicConstraints=critical,CA:true -addext keyUsage=critical,keyCertSign,cRLSign -out "$1.crt"; }
issue() { # name issuer cn extensions not_before not_after
  key "$1" P-256; openssl req -new -key "$1.key" -subj "/O=NetPaw tests/CN=$3" -out "$1.csr"
  printf '%b' "$4" > "$1.ext"
  openssl x509 -req -in "$1.csr" -CA "$2.crt" -CAkey "$2.key" -set_serial "0x$(openssl rand -hex 8)" -not_before "$5" -not_after "$6" -extfile "$1.ext" -out "$1.crt" 2>/dev/null; }
CA='basicConstraints=critical,CA:true,pathlen:1\nkeyUsage=critical,keyCertSign,cRLSign\n'
LEAF='basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=codeSigning\n'
TSA='basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=critical,timeStamping\n'

root root "Test Root R0";        issue ca root "Test CA" "$CA" 20260101000000Z 20340101000000Z
issue leaf ca "smol-kitten\\/netpaw" "$LEAF" $NB $NA
issue tsa root "Test Timestamp T0" "$TSA" 20260101000000Z 20340101000000Z
root other "Other Root";          issue oca other "Other CA" "$CA" 20260101000000Z 20340101000000Z
issue oleaf oca "smol-kitten\\/netpaw" "$LEAF" $NB $NA

msibuild unsigned.msi -s "NetPaw fixture" "smol-kitten" ";1033" "{11111111-2222-3333-4444-555555555555}"
sign() { # in out leaf ca [notimestamp]
  cat "$3.crt" "$4.crt" > chain.pem
  local ts=(-TSA-certs tsa.crt -TSA-key tsa.key -TSA-time $((T + 60)))
  [ "${5:-}" = notimestamp ] && ts=()
  osslsigncode sign -certs chain.pem -key "$3.key" -h sha256 -n smol-kitten/netpaw -time $T "${ts[@]}" -in "$1" -out "$2" >/dev/null; }
sign unsigned.msi valid.msi leaf ca
sign unsigned.msi otherroot.msi oleaf oca
sign unsigned.msi notimestamp.msi leaf ca notimestamp
# tampered: one byte of the summary stream changes after signing (the signature stream is untouched)
python3 - valid.msi tampered.msi <<'EOF'
import sys
b = bytearray(open(sys.argv[1], "rb").read()); i = b.index(b"NetPaw fixture"); b[i] ^= 0x01
open(sys.argv[2], "wb").write(bytes(b))
EOF
osslsigncode verify -CAfile root.crt -TSA-CAfile root.crt -in valid.msi | grep -q 'Signature verification: ok'
! osslsigncode verify -CAfile root.crt -TSA-CAfile root.crt -in tampered.msi >/dev/null 2>&1

mkdir -p "$OUT"; cp root.crt "$OUT/test-root.crt"; cp other.crt "$OUT/other-root.crt"
cp unsigned.msi valid.msi otherroot.msi notimestamp.msi tampered.msi "$OUT/"
{ echo "# Signatures"; echo; echo '| file | sha256 | signature |'; echo '|---|---|---|'
  for f in valid.msi tampered.msi otherroot.msi notimestamp.msi; do echo "| \`$f\` | $(sha256sum "$f" | cut -c1-64) | Authenticode |"; done; } > "$OUT/SIGNATURES.md"
ls -la "$OUT"
