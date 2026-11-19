#!/usr/bin/env python3
"""sync_report.py DATA_DIR — summarise a sync started by scripts/run-rabdi.sh and watched by its monitor:
blocks executed, time to reach the head, rates, peak memory, database sizes, and errors in the logs."""
import csv, datetime, glob, json, os, re, sys


def ts(s):
    return datetime.datetime.fromisoformat(s.replace("Z", "+00:00"))


def main():
    d = sys.argv[1]
    rows = [r for r in csv.DictReader(open(os.path.join(d, "monitor.csv"))) if r["el_head"] not in ("", "0")]
    started = ts(open(os.path.join(d, "started.txt")).read().split()[0])
    caught = next((r for r in rows if r["cl_sync_distance"] not in ("", None) and r["cl_sync_distance"].isdigit() and int(r["cl_sync_distance"]) <= 2), None)
    last = rows[-1]
    hourly = []
    for r in rows:
        t = ts(r["utc"])
        if not hourly or (t - ts(hourly[-1]["utc"])).total_seconds() >= 3600:
            hourly.append(r)
    # Nethermind rotates its log (rabdi.log, rabdi.2.log, ...): read them all.
    logs = sorted(glob.glob(os.path.join(d, "el", "logs", "rabdi*.log")))
    errors, warnings, invalid = 0, 0, []
    for log in logs:
        for line in open(log, errors="replace"):
            if "|ERROR|" in line:
                errors += 1
            elif "|WARN|" in line:
                warnings += 1
            if "invalid block" in line.lower() or "InvalidBlockException" in line:
                invalid.append(line.strip()[:240])
    out = {
        "started_utc": started.isoformat(),
        "caught_up_utc": caught and caught["utc"],
        "time_to_head_s": caught and (ts(caught["utc"]) - started).total_seconds(),
        "head_at_catch_up": caught and int(caught["el_head"]),
        "last_sample": last,
        "peak_rss_mb": max(int(r["el_peak_rss_mb"] or 0) for r in rows),
        "el_db_mb": int(last["el_db_mb"] or 0),
        "golemdb_and_arkiv_mb": int(last["golemdb_mb"] or 0),
        "cl_db_mb": int(last["cl_db_mb"] or 0),
        "log_errors": errors,
        "log_warnings": warnings,
        "invalid_block_lines": invalid[:20],
        "progress_hourly": [{"utc": r["utc"], "el_head": int(r["el_head"])} for r in hourly],
    }
    print(json.dumps(out, indent=2))


if __name__ == "__main__":
    main()
