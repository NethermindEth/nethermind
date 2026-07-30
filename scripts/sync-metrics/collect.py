"""Extract sync stage timings from the execution client's docker logs.

Runs on the sync runner right after wait-for-sync.py succeeds. Writes a partial
metrics record (JSON) that publish.py later merges with run/job metadata and
appends to the sync-metrics data branch.

Usage: collect.py <output.json>
Env: NETWORK, SYNC_MODE, DOCKER_IMAGE; LOG_FILE overrides docker for testing.
"""

import json
import os
import re
import subprocess
import sys
from datetime import datetime, timezone

MARKERS = {
    "snap_start": "Starting snap sync.",
    "snap_end": "Snap sync completed.",
    "state_finished": "STATE SYNC FINISHED",
    "verify_start": "Collecting trie stats",
    "drain_start": "Waiting for storage verification workers",
    "verify_end_flat": "Verification complete.",
    "verify_end_halfpath": "Stats after finishing state",
}

# RFC3339 line prefix as emitted by `docker logs -t` (or a GitHub Actions log line)
TS_RE = re.compile(r"^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.\d+)?Z\s")
COUNTERS_RE = re.compile(r"Accounts=(\d+), Slots=(\d+)")


def parse_log(lines):
    found = {}
    counters = {}
    for line in lines:
        m = TS_RE.match(line)
        if m is None:
            continue
        for key, marker in MARKERS.items():
            if key not in found and marker in line:
                found[key] = datetime.strptime(m.group(1), "%Y-%m-%dT%H:%M:%S").replace(tzinfo=timezone.utc)
                if key == "verify_end_flat":
                    cm = COUNTERS_RE.search(line)
                    if cm is not None:
                        counters["accounts"] = int(cm.group(1))
                        counters["slots"] = int(cm.group(2))
    return found, counters


def minutes_between(found, start_key, end_key):
    if start_key not in found or end_key not in found:
        return None
    return round((found[end_key] - found[start_key]).total_seconds() / 60, 1)


def main():
    network = os.environ["NETWORK"]
    sync_mode = os.environ["SYNC_MODE"]
    out_path = sys.argv[1]

    log_file = os.getenv("LOG_FILE")
    if log_file:
        with open(log_file, encoding="utf-8", errors="replace") as f:
            lines = f.readlines()
    else:
        # Same container-name mapping as wait-for-sync.py
        container = "sedge-execution-client"
        for prefix, name in (
            ("base-", "sedge-execution-op-l2-client"),
            ("world-", "sedge-execution-op-l2-client"),
            ("op-", "sedge-execution-op-l2-client"),
            ("taiko-", "sedge-execution-taiko-client"),
        ):
            if network.startswith(prefix):
                container = name
                break
        result = subprocess.run(
            ["docker", "logs", "-t", container],
            capture_output=True, text=True, errors="replace", check=True,
        )
        lines = result.stdout.splitlines() + result.stderr.splitlines()

    found, counters = parse_log(lines)
    verify_end = "verify_end_flat" if sync_mode.lower() == "flat" else "verify_end_halfpath"

    record = {"network": network, "mode": sync_mode}
    image = os.getenv("DOCKER_IMAGE")
    if image:
        record["image"] = image
    for field, (start, end) in {
        "snap_min": ("snap_start", "snap_end"),
        "heal_min": ("snap_end", "state_finished"),
        "verify_min": ("verify_start", verify_end),
        "drain_min": ("drain_start", "verify_end_flat"),
    }.items():
        value = minutes_between(found, start, end)
        if value is not None:
            record[field] = value
    record.update(counters)

    with open(out_path, "w") as f:
        json.dump(record, f)
    print(json.dumps(record, indent=2))


if __name__ == "__main__":
    main()
