# Presets & repositories

## Device presets
Factory-default management addresses of common gear (MikroTik `192.168.88.1`, FortiGate `192.168.1.99`, FRITZ!Box `192.168.178.1`, …). Type the vendor in the panel or use *Device presets* in the tray menu. NetPaw puts a host address in the device's subnet and uses the device as gateway, as a temporary secondary.

Your own presets: `%APPDATA%\NetPaw\presets.json` (same schema; same vendor/model overrides the bundled one).

## Online repositories
A repository is an `index.json` at an HTTPS URL. It holds presets and full profiles. NetPaw fetches it **only** when you click *Sync now* in Settings (or run `netpaw-cli repo sync`), caches it, and shows the entries with a badge naming the repository. Nothing from a repository is ever applied by itself.

- The **community** pack from the NetPaw project is pre-configured and signed with key `ec6ef08c`.
- Add your company's pack: paste `https://…/index.json` or `https://…/index.json|keyId:publicKey` to pin its signing key.
- Trust: **verified** (signature matches the pinned key), **unsigned** (no key pinned — shown with a badge), or rejected (signature or entry hash mismatch; the previous good copy is kept).

Authoring your own pack: `netpaw-cli pack keygen`, `pack build`, `pack sign`, `pack verify`. Format: `docs/PACK-FORMAT.md` in the project.
