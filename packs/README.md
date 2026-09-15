# Packs

Online profile packs served raw from this repository. They are **not** part of the build — NetPaw
downloads them on request. Format: [docs/PACK-FORMAT.md](../docs/PACK-FORMAT.md).

| pack | URL | key |
|---|---|---|
| `community` | `https://raw.githubusercontent.com/smol-kitten/netpaw/main/packs/community/index.json` | `ec6ef08c` (`community/public.txt`) |

## Contributing an entry

1. Edit `community/index.json` (presets: `vendor`, `model`, `kind`, `ip`, `prefix`, `note`, `url`; profiles: a full `profile` object).
2. Run `netpaw-cli pack verify community/index.json` — it lists invalid or duplicate entries.
3. Open a PR. A maintainer re-signs the index on merge (`pack sign` with the private key); CI rejects an index whose hashes or signature do not match, so please do not hand-edit `sha256`/`signature`.
