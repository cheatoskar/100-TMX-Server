# Sets top-level keys (and replaces whole [tables]) in a live config.toml, keeping a backup.
# Runs on the game machine as root; deploy/enable-sync.sh copies it over and pipes the values in
# on stdin as JSON:
#   {"path": "...", "keys": {"Key": "toml value", ...}, "tables": {"EventTmxQuery": "inhasrecord = 0\n"},
#    "secret_keys": {"SyncToken": "<value>"}}
# Values are written verbatim (already TOML), except secret_keys, which are quoted here and never
# printed.
import json
import re
import shutil
import sys
import time

spec = json.load(sys.stdin)
path = spec["path"]
text = open(path, encoding="utf-8").read()
shutil.copy2(path, f"{path}.bak-{time.strftime('%Y%m%d-%H%M%S')}")

head, sep, rest = text.partition("\n[")
keys = dict(spec.get("keys", {}))
for k, v in spec.get("secret_keys", {}).items():
    if not re.fullmatch(r"[A-Za-z0-9_\-]{24,128}", v):
        sys.exit(f"{k}: unexpected characters or length")
    keys[k] = f'"{v}"'

for k, v in keys.items():
    line = f"{k} = {v}"
    if re.search(rf"^{re.escape(k)}\s*=", head, re.M):
        head = re.sub(rf"^{re.escape(k)}\s*=.*$", lambda _: line, head, flags=re.M)
    else:
        head = head.rstrip("\n") + "\n" + line + "\n"

text = head + sep + rest
for table, body in spec.get("tables", {}).items():
    text = re.sub(rf"\n\[{re.escape(table)}\][^\[]*", "\n", text)
    text = text.rstrip("\n") + f"\n\n[{table}]\n{body}"

open(path, "w", encoding="utf-8").write(text)
secret = set(spec.get("secret_keys", {}))
for line in text.splitlines():
    name = line.split("=", 1)[0].strip()
    if name in keys or line.startswith("[") and line[1:-1] in spec.get("tables", {}):
        print(f"{name} = <set>" if name in secret else line)
