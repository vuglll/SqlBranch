#!/bin/sh
# Prints the Codex plan limits recorded by the most recent Codex CLI run.
# The CLI has no usage command; each run writes a rate_limits snapshot into
# its session log under ~/.codex/sessions.
set -eu
dir="${CODEX_HOME:-$HOME/.codex}/sessions"
latest=$(find "$dir" -name '*.jsonl' -type f -print 2>/dev/null | xargs ls -t 2>/dev/null | head -n 1)
if [ -z "$latest" ]; then
  echo "No Codex CLI session log found under $dir." >&2
  exit 1
fi
python3 - "$latest" <<'PY'
import json, sys, datetime
snapshot = None
with open(sys.argv[1], encoding="utf-8") as log:
    for line in log:
        if '"rate_limits"' not in line:
            continue
        try:
            item = json.loads(line)
        except ValueError:
            continue
        def find(node):
            if isinstance(node, dict):
                if "rate_limits" in node and isinstance(node["rate_limits"], dict):
                    return node["rate_limits"]
                for value in node.values():
                    found = find(value)
                    if found:
                        return found
            return None
        snapshot = find(item) or snapshot
if not snapshot:
    print("The latest Codex session log has no rate-limit snapshot.", file=sys.stderr)
    sys.exit(1)
for key, label in (("primary", "5-hour"), ("secondary", "weekly")):
    window = snapshot.get(key) or {}
    resets = window.get("resets_at")
    when = datetime.datetime.fromtimestamp(resets).strftime("%Y-%m-%d %H:%M") if resets else "unknown"
    print(f"{label}: {window.get('used_percent', '?')}% used, resets {when}")
print(f"source: {sys.argv[1]}")
PY
