#!/usr/bin/env bash
# Verify a signed NetPaw release directory against the pinned catboy.systems root.
# CI runs this on the signed artifacts; docs/signed-release.md tells people to run the same file
# on a download. Fails (exit 1) on ANY file that does not verify.
#
#   tools/verify-release.sh <dir> [pki-dir]     (pki-dir defaults to deploy/pki next to this script)
#
# Checks, per file:
#   *.exe *.dll *.msi  Authenticode chain to R0 + RFC 3161 timestamp + CRLs (osslsigncode verify)
#   *.zip              detached CMS <zip>.p7s at the timestamp time + the token itself (<zip>.p7s.tsr)
#   SIGNATURES.md      every listed sha256 matches the file next to it
# Recipe: workcollection/fleet-actions README §"Verifying a signed release".
set -uo pipefail

DIR=${1:?usage: verify-release.sh <dir> [pki-dir]}
PKI=${2:-$(cd "$(dirname "$0")/../deploy/pki" && pwd)}
R0_SHA256=ed5eaa1b7a66e154e8c82a3d45f65ddd36a3fbbad1f41a68d2bf4334ccc2d375   # staging R0, pinned by catboy-sign v2.1.5

fail=0; ok=0
pass() { ok=$((ok+1)); echo "ok    $1"; }
bad()  { fail=$((fail+1)); echo "FAIL  $1"; }

fp=$(openssl x509 -in "$PKI/r0.crt" -outform DER | sha256sum | cut -c1-64)
[ "$fp" = "$R0_SHA256" ] || { echo "FAIL  r0.crt fingerprint $fp is not the pinned $R0_SHA256"; exit 1; }
echo "root  $PKI/r0.crt sha256 $fp (staging R0)"
echo "tool  $(osslsigncode --version 2>&1 | head -1)"

tmp=$(mktemp -d); trap 'rm -rf "$tmp"' EXIT
cat "$PKI/r0.crt" "$PKI/cb0.crt" "$PKI/t0.crt" > "$tmp/tsa-ca.pem"
cat "$PKI/t0.crt" "$PKI/cb0.crt" > "$tmp/tsa-untrusted.pem"

# CRLs: fetched here once per URL with curl (named User-Agent, retries) instead of by osslsigncode, whose own
# fetches send an empty User-Agent and fail intermittently behind the WAF (measured on the first rc run).
# Revocation is still checked: -ignore-cdp only stops osslsigncode's download, the CRLs come in via -CRLfile.
crls() { # <signed file> → path of a PEM bundle holding the CRL of every certificate in its signature (incl. the TSA chain)
  local f=$1 url h
  : > "$tmp/crls.pem"
  rm -f "$tmp/sig.der"   # extract-signature refuses to overwrite
  osslsigncode extract-signature -in "$f" -out "$tmp/sig.der" >/dev/null 2>&1 || return 1
  for url in $(grep -aoE 'https?://[A-Za-z0-9./_-]+\.crl' "$tmp/sig.der" | sort -u); do
    h=$(sha256sum <<<"$url" | cut -c1-16)
    if [ ! -s "$tmp/crl-$h.pem" ]; then
      curl -fsS -m 20 --retry 3 --retry-all-errors -A "netpaw-verify-release/1" -o "$tmp/crl-$h.der" "$url" \
        && openssl crl -inform DER -in "$tmp/crl-$h.der" -out "$tmp/crl-$h.pem" 2>/dev/null \
        || { echo "      CRL not available: $url"; return 1; }
    fi
    cat "$tmp/crl-$h.pem" >> "$tmp/crls.pem"
  done
}

while IFS= read -r -d '' f; do
  if crls "$f" && out=$(osslsigncode verify -in "$f" -CAfile "$PKI/r0.crt" -TSA-CAfile "$tmp/tsa-ca.pem" \
       -ignore-cdp -CRLfile "$tmp/crls.pem" -TSA-CRLfile "$tmp/crls.pem" 2>&1) \
     && grep -q 'Signature verification: ok' <<<"$out" && ! grep -q 'Timestamp is not available' <<<"$out"; then
    pass "${f#"$DIR"/}  Authenticode + timestamp"
  else
    bad "${f#"$DIR"/}  Authenticode"; sed 's/^/      /' <<<"${out:-}" | tail -15
  fi
done < <(find "$DIR" -type f \( -name '*.exe' -o -name '*.dll' -o -name '*.msi' \) -print0 | sort -z)

while IFS= read -r -d '' f; do
  name=${f#"$DIR"/}
  if [ ! -s "$f.p7s" ] || [ ! -s "$f.p7s.tsr" ]; then bad "$name  missing .p7s or .p7s.tsr"; continue; fi
  gen=$(openssl ts -reply -in "$f.p7s.tsr" -text 2>/dev/null | sed -n 's/^Time stamp: //p')
  at=$(date -d "$gen" +%s 2>/dev/null) || { bad "$name  unreadable timestamp token"; continue; }
  if openssl cms -verify -binary -inform DER -in "$f.p7s" -content "$f" -CAfile "$PKI/r0.crt" \
       -no-CApath -no-CAstore -purpose any -attime "$at" -out /dev/null 2>"$tmp/err" \
     && openssl ts -verify -data "$f.p7s" -in "$f.p7s.tsr" -CAfile "$PKI/r0.crt" -untrusted "$tmp/tsa-untrusted.pem" >/dev/null 2>>"$tmp/err"; then
    pass "$name  CMS at $gen + RFC 3161 token"
  else
    bad "$name  CMS/timestamp"; sed 's/^/      /' "$tmp/err" | tail -10
  fi
done < <(find "$DIR" -type f -name '*.zip' -print0 | sort -z)

# SIGNATURES.md rows: | `path` | sha256 | kind |  — paths are relative to the signed artifact root.
while IFS= read -r sig; do
  base=$(dirname "$sig")
  while IFS='|' read -r _ path hash _; do
    path=$(tr -d ' `' <<<"$path"); hash=$(tr -d ' ' <<<"$hash")
    [[ $hash =~ ^[0-9a-f]{64}$ ]] || continue
    f=$(find "$base" -type f -path "*/$path" -print -quit); [ -n "$f" ] || f=$(find "$base" -type f -name "$(basename "$path")" -print -quit)
    if [ -z "$f" ]; then echo "skip  ${sig#"$DIR"/}: $path not in this directory (inside a zip/msi)"; continue; fi
    [ "$(sha256sum "$f" | cut -c1-64)" = "$hash" ] && pass "${sig#"$DIR"/}: $path sha256" || bad "${sig#"$DIR"/}: $path sha256 differs"
  done < "$sig"
done < <(find "$DIR" -type f -name 'SIGNATURES*.md')

echo "verified $ok, failed $fail"
[ "$ok" -gt 0 ] && [ "$fail" = 0 ]
