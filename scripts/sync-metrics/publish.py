"""Merge collected sync metrics with workflow run/job metadata and append to history.jsonl.

Idempotent: existing records for the same (run_id, network, mode) are replaced,
so a retried push loop can re-run this script safely.

Usage: publish.py <metrics_dir> <history.jsonl>
Env: GITHUB_REPOSITORY, GITHUB_RUN_ID, GH_TOKEN
"""

import json
import os
import sys
import urllib.request
from datetime import datetime


def api(path):
    request = urllib.request.Request(
        f"https://api.github.com/{path}",
        headers={
            "Authorization": f"Bearer {os.environ['GH_TOKEN']}",
            "Accept": "application/vnd.github+json",
        },
    )
    with urllib.request.urlopen(request, timeout=30) as response:
        return json.load(response)


def main():
    metrics_dir, history_path = sys.argv[1], sys.argv[2]
    repo = os.environ["GITHUB_REPOSITORY"]
    run_id = int(os.environ["GITHUB_RUN_ID"])

    run = api(f"repos/{repo}/actions/runs/{run_id}")
    attempt = int(os.environ.get("GITHUB_RUN_ATTEMPT", run["run_attempt"]))
    job_min = {}
    page = 1
    while True:
        jobs = api(f"repos/{repo}/actions/runs/{run_id}/attempts/{attempt}/jobs?per_page=100&page={page}")["jobs"]
        for job in jobs:
            name = job["name"]
            if name.endswith(" / sync"):
                name = name.removesuffix(" / sync")
            elif " / " in name:
                continue
            if name.startswith("Sync ") and job["conclusion"] == "success" and job["completed_at"]:
                started = datetime.fromisoformat(job["started_at"].replace("Z", "+00:00"))
                completed = datetime.fromisoformat(job["completed_at"].replace("Z", "+00:00"))
                elapsed = (completed - started).total_seconds()
                if elapsed >= 0:
                    job_min[name] = round(elapsed / 60, 1)
        if len(jobs) < 100:
            break
        page += 1

    records = []
    for name in sorted(os.listdir(metrics_dir)):
        if not name.endswith(".json"):
            continue
        with open(os.path.join(metrics_dir, name)) as f:
            record = json.load(f)
        record = {
            "run_id": run_id,
            "run_attempt": attempt,
            "date": run["created_at"],
            "event": run["event"],
            "commit": run["head_sha"][:10],
            **record,
            "source": "ci",
        }
        duration = job_min.get(f"Sync {record['network']} ({record['mode']})")
        if duration is not None:
            record["job_min"] = duration
        else:
            print(f"No successful job matched 'Sync {record['network']} ({record['mode']})'; job_min omitted.")
        records.append(record)

    if not records:
        print("No metrics records found.")
        return

    replaced_keys = {(r["run_id"], r["network"], r["mode"]) for r in records}
    lines = []
    if os.path.exists(history_path):
        with open(history_path) as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                existing = json.loads(line)
                if (existing.get("run_id"), existing.get("network"), existing.get("mode")) not in replaced_keys:
                    lines.append(line)

    for record in records:
        lines.append(json.dumps(record, separators=(",", ":")))
        print("Appending:", lines[-1])

    with open(history_path, "w") as f:
        f.write("\n".join(lines) + "\n")


if __name__ == "__main__":
    main()
