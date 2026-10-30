#!/usr/bin/env python3
"""Fills @@PASSWORD@@ and @@SSH_PUB@@ in autounattend.xml from the ENVIRONMENT (PW, PUB), never from argv, so the
secrets are not visible in `ps` and never pass through a shell variable or stdout. Run it under
`vault.py exec pve.harness_vm_password:PW pve.harness_ssh_pub:PUB -- python3 fill-unattend.py SRC DST [--expect PW_META PUB_META]`.
--expect compares len and sha8 (sha256[:8], what `vault.py get` prints as metadata) of each value and prints a
RESULT line per value; the values themselves are never printed. Exit 2 on a mismatch or a placeholder left behind."""
import hashlib, os, sys

def meta(v): return f"len={len(v)} sha8={hashlib.sha256(v.encode()).hexdigest()[:8]}"

def main():
    a = sys.argv[1:]
    if len(a) < 2: print(__doc__); return 1
    src, dst = a[0], a[1]
    expect = a[3:5] if len(a) >= 5 and a[2] == "--expect" else None
    pw, pub = os.environ.get("PW"), os.environ.get("PUB")
    if not pw or not pub: print("RESULT|fill|FAILED PW/PUB missing from the environment (run under vault.py exec)"); return 2
    rc = 0
    if expect:
        for name, val, exp in (("PW", pw, expect[0]), ("PUB", pub, expect[1])):
            m = meta(val); ok = m.replace(" ", "") == exp.replace(" ", "")
            print(f"RESULT|selftest|{name} {m} expected {exp} match={'yes' if ok else 'NO'}")
            if not ok: rc = 2
    s = open(src, encoding="utf-8").read()
    out = s.replace("@@PASSWORD@@", pw.replace("&", "&amp;").replace("<", "&lt;")).replace("@@SSH_PUB@@", pub.strip())
    if "@@PASSWORD@@" in out or "@@SSH_PUB@@" in out: print("RESULT|fill|FAILED placeholder left behind"); rc = 2
    if pw in s or pub.strip() in s: print("RESULT|fill|FAILED the source already contains a secret"); rc = 2
    if rc == 0:
        with open(os.open(dst, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600), "w", encoding="utf-8") as f: f.write(out)
        print(f"RESULT|fill|ok {len(out)} bytes, both placeholders replaced")
    return rc

if __name__ == "__main__": sys.exit(main())
