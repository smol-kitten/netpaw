#!/usr/bin/env python3
"""One-off VirusTotal report for a release's signed files (docs/signed-release.md, "Release record").

    VT_API_KEY=... tools/vt-scan.py NetPaw-0.13.0.msi NetPaw.exe netpaw-cli.exe ...
    (on the maintainer box: vault.py exec virustotal.api_key:VT_API_KEY -- tools/vt-scan.py <files>)

Per file: look the sha256 up first and upload only files VirusTotal does not know, then poll the
analysis until it is completed. Free-tier limits (4 requests/min) are respected with a fixed gap
between requests. Prints a Markdown table: file, sha256, permalink, detections n/total, flagging
engines. Exit 1 when a major engine reports the file as malicious, 0 otherwise (suspicious or
heuristic-only hits are listed but do not fail). The key is read from the environment and is never
printed or logged.
"""
import json, os, sys, time, hashlib, urllib.request, urllib.error, uuid

API = "https://www.virustotal.com/api/v3"
GAP = 16          # seconds between requests: 4/min
MAJOR = {"Microsoft", "ESET-NOD32", "Kaspersky", "BitDefender", "CrowdStrike", "SentinelOne"}
_last = 0.0


def call(method, path, body=None, ctype=None):
    global _last
    wait = _last + GAP - time.time()
    if wait > 0:
        time.sleep(wait)
    _last = time.time()
    req = urllib.request.Request(path if path.startswith("http") else API + path, data=body, method=method)
    req.add_header("x-apikey", os.environ["VT_API_KEY"])
    req.add_header("accept", "application/json")
    if ctype:
        req.add_header("content-type", ctype)
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.status, json.load(r)
    except urllib.error.HTTPError as e:
        return e.code, None


def upload(path):
    data = open(path, "rb").read()
    b = uuid.uuid4().hex
    body = (f"--{b}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"{os.path.basename(path)}\"\r\n"
            "Content-Type: application/octet-stream\r\n\r\n").encode() + data + f"\r\n--{b}--\r\n".encode()
    target = "/files"
    if len(data) > 32 * 1024 * 1024:   # large files go to a one-time upload URL
        _, j = call("GET", "/files/upload_url")
        target = j["data"]
    st, j = call("POST", target, body, f"multipart/form-data; boundary={b}")
    if st != 200:
        raise SystemExit(f"upload of {path} failed: HTTP {st}")
    return j["data"]["id"]


def report(path):
    sha = hashlib.sha256(open(path, "rb").read()).hexdigest()
    st, j = call("GET", f"/files/{sha}")
    if st == 404:
        aid = upload(path)
        print(f"  {os.path.basename(path)}: uploaded, analysis {aid}", file=sys.stderr)
        for _ in range(60):
            _, a = call("GET", f"/analyses/{aid}")
            if a and a["data"]["attributes"]["status"] == "completed":
                break
        st, j = call("GET", f"/files/{sha}")
    elif st == 200:
        print(f"  {os.path.basename(path)}: already known to VirusTotal, no upload", file=sys.stderr)
    if st != 200:
        raise SystemExit(f"lookup of {path} failed: HTTP {st}")
    attr = j["data"]["attributes"]
    stats = attr.get("last_analysis_stats", {})
    results = attr.get("last_analysis_results", {})
    mal = sorted(n for n, r in results.items() if r.get("category") == "malicious")
    sus = sorted(n for n, r in results.items() if r.get("category") == "suspicious")
    total = sum(stats.get(k, 0) for k in ("malicious", "suspicious", "undetected", "harmless"))
    return {"file": os.path.basename(path), "sha256": sha, "link": f"https://www.virustotal.com/gui/file/{sha}",
            "flagged": len(mal) + len(sus), "total": total, "malicious": mal, "suspicious": sus,
            "major": sorted(set(mal) & MAJOR)}


def main(files):
    if not os.environ.get("VT_API_KEY"):
        raise SystemExit("VT_API_KEY is not set (vault.py exec virustotal.api_key:VT_API_KEY -- ...)")
    rows = [report(f) for f in files]
    print("| file | sha256 | VirusTotal | detections | flagged by |")
    print("|---|---|---|---|---|")
    for r in rows:
        who = ", ".join([f"{n} (malicious)" for n in r["malicious"]] + [f"{n} (suspicious)" for n in r["suspicious"]]) or "none"
        print(f"| `{r['file']}` | `{r['sha256']}` | [report]({r['link']}) | {r['flagged']}/{r['total']} | {who} |")
    bad = [r for r in rows if r["major"]]
    for r in bad:
        print(f"\nFAIL: {r['file']} is flagged malicious by a major engine: {', '.join(r['major'])}", file=sys.stderr)
    return 1 if bad else 0


if __name__ == "__main__":
    if len(sys.argv) < 2:
        raise SystemExit(__doc__)
    sys.exit(main(sys.argv[1:]))
