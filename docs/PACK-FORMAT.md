# Profile pack format

A pack is one `index.json` at any HTTPS URL — GitHub raw, an S3 bucket, an intranet web folder.
NetPaw fetches it only when the user clicks *Sync* (or runs `netpaw-cli repo sync`), caches it in
`%APPDATA%\NetPaw\cache\`, and shows its entries next to the bundled presets. Nothing from a pack
is ever applied automatically.

```json
{
  "name": "community",
  "version": "1",
  "updated": "2026-09-15",
  "description": "…",
  "homepage": "https://…",
  "entries": [
    {
      "id": "mikrotik-routeros",
      "vendor": "MikroTik",
      "model": "RouterOS (any)",
      "kind": "preset",
      "ip": "192.168.88.1",
      "prefix": 24,
      "hostIp": null,
      "note": "admin / blank",
      "url": "http://192.168.88.1",
      "sha256": "…"
    },
    {
      "id": "mikrotik-first-boot",
      "vendor": "MikroTik",
      "model": "First boot",
      "kind": "profile",
      "profile": { "name": "MikroTik first boot", "dhcp": false, "addresses": [ { "address": "192.168.88.250", "prefixLength": 24 } ], "gateway": "192.168.88.1", "dns": [] },
      "note": "…",
      "sha256": "…"
    }
  ],
  "signature": { "alg": "ecdsa-p256-sha256", "keyId": "ec6ef08c", "value": "base64 DER signature" }
}
```

| field | meaning |
|---|---|
| `kind: preset` | a device's factory address. NetPaw picks a free host in that subnet when used. `hostIp` forces one. |
| `kind: profile` | a complete profile (same schema as `profiles.json`, `adapter` normally null). Applied as-is; *Ctrl+E* copies it into the user's own profiles. Invalid profiles are dropped at load. |
| `sha256` | hex SHA-256 of the entry's canonical JSON without the `sha256` field. Optional per entry; a mismatch rejects the whole pack. |
| `signature` | ECDSA P-256 / SHA-256 over the canonical `entries` array. Optional. |

**Canonical JSON**: object keys sorted ordinally, `null` members dropped, no whitespace, UTF-8.
`netpaw-cli pack build/sign/verify` handle all of it; you never compute hashes by hand.

## Trust

| repo config | pack | result |
|---|---|---|
| URL only | any | *unsigned* — shown with a badge, hidden if policy `AllowUnsignedRepos=0` |
| URL + pinned key | signed with that key | *verified* |
| URL + pinned key | unsigned, other key, or tampered entry | rejected, cached good copy kept |

Pin a key by writing the repo as `https://host/index.json|keyId:base64PublicKey` (settings, `netpaw-cli repo add`, or the `RepoUrls` policy). The key id is the first 8 hex chars of SHA-256 over the SPKI bytes; NetPaw derives it if you pass only the key.

## Publishing your own pack

```
netpaw-cli pack keygen  C:\keys\netpaw            # once; keep private.pem off the share
netpaw-cli pack build   my-presets.json  index.json --name "ACME IT"
# add "profile" entries by hand if you like, then:
netpaw-cli pack sign    index.json  C:\keys\netpaw\private.pem
netpaw-cli pack verify  index.json  <public key from public.txt>
```

Serve `index.json` over HTTPS (`Cache-Control` and `ETag` are honoured). Hand out
`https://…/index.json|<keyId>:<publicKey>` to your users or put it in the `RepoUrls` policy.

The community pack lives in [`packs/community/`](../packs/community/) of this repository and is
served from `https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json`.
It is signed with key `ec6ef08c`; CI verifies every change. It is **not** compiled into NetPaw.
