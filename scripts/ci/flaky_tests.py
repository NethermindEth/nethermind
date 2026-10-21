# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import argparse
import datetime
import json
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time

TIMESTAMP = re.compile(r"^\d{4}-\d\d-\d\dT[\d:.]+Z ?")
ANSI = re.compile(r"\x1b\[[0-9;]*m")
RUNNING = re.compile(r"Running all tests in .*/bin/(?P<project>[^/]+)/release/")
FAILED = re.compile(r"^failed (?P<name>.+?) \((?P<duration>[\dhmsµn ]+)\)$")
RESULT = re.compile(r"^(passed|failed|skipped) .+ \([\dhmsµn ]+\)$|^Test run summary|^\s*(total|failed|succeeded|skipped|duration):")
PROJECT = re.compile(r"^Nethermind(\.[A-Za-z0-9]+)+\.Test$")
METHOD = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
EXCERPT_LINES = 40
EXCERPT_CHARS = 4000
SOURCE_ROOT = Path("src/Nethermind")
ZERO_TESTS_RAN = 8
MIN_VERIFY_RUNS = 50
MAX_VERIFY_RUNS = 1000
CONFIDENCE_FACTOR = 3


def parse_failures(log):
    failures = []
    project = None
    current = None
    for raw in log.splitlines():
        line = ANSI.sub("", TIMESTAMP.sub("", raw)).rstrip()
        running = RUNNING.search(line)
        if running:
            project = running.group("project")
            current = None
            continue
        failed = FAILED.match(line)
        if failed:
            current = {"project": project, "test": failed.group("name"), "excerpt": []}
            failures.append(current)
            continue
        if current is None:
            continue
        if RESULT.match(line) or len(current["excerpt"]) >= EXCERPT_LINES:
            current = None
            continue
        current["excerpt"].append(line)
    for failure in failures:
        failure["method"] = failure["test"].split("(", 1)[0]
        failure["excerpt"] = "\n".join(failure["excerpt"]).strip()[:EXCERPT_CHARS]
    return failures


def is_eligible(failure):
    return bool(failure["project"] and PROJECT.match(failure["project"]) and METHOD.match(failure["method"]))


def find_source(project, method):
    pattern = re.compile(rf"\b{re.escape(method)}\s*\(")
    for path in sorted((SOURCE_ROOT / project).rglob("*.cs")):
        if pattern.search(path.read_text(encoding="utf-8", errors="replace")):
            return path.as_posix()
    return None


def gh_count(*args):
    output = subprocess.run(["gh", *args], check=True, capture_output=True, text=True).stdout
    return len(json.loads(output or "[]"))


def is_known(repo, method, since):
    searches = (
        ("search", "prs", method, "--repo", repo, "--state", "open", "--json", "number"),
        ("search", "issues", method, "--repo", repo, "--state", "open", "--json", "number"),
        ("search", "prs", method, "--repo", repo, "--merged-at", f">={since}", "--json", "number"),
    )
    return any(gh_count(*search) for search in searches)


def branch_name(method):
    return f"test/flaky-{method.lower().replace('_', '-')}"


def branch_exists(branch):
    result = subprocess.run(["git", "ls-remote", "--exit-code", "--heads", "origin", branch], capture_output=True)
    return result.returncode == 0


def opened_today(repo, label, today):
    return gh_count("pr", "list", "--repo", repo, "--state", "all", "--label", label,
                    "--search", f"created:>={today}", "--json", "number")


def select(log_dir, repo, label, daily_limit, lookback_days, today=None):
    today = today or datetime.date.today()
    if opened_today(repo, label, today.isoformat()) >= daily_limit:
        return None, "daily limit reached"
    since = (today - datetime.timedelta(days=lookback_days)).isoformat()
    seen = set()
    for log in sorted(Path(log_dir).glob("*.log")):
        for failure in parse_failures(log.read_text(encoding="utf-8", errors="replace")):
            key = (failure["project"], failure["method"])
            if key in seen or not is_eligible(failure):
                continue
            seen.add(key)
            source = find_source(failure["project"], failure["method"])
            branch = branch_name(failure["method"])
            if source is None or branch_exists(branch) or is_known(repo, failure["method"], since):
                continue
            return dict(failure, source=source, branch=branch), None
    return None, "no new flaky test candidate"


def required_runs(runs, failures):
    if failures <= 0:
        return MAX_VERIFY_RUNS
    return max(MIN_VERIFY_RUNS, min(MAX_VERIFY_RUNS, math.ceil(CONFIDENCE_FACTOR * runs / failures)))


def classify(runs, failures):
    if failures == 0:
        return "not-reproduced"
    if failures == runs:
        return "deterministic"
    return "flaky"


def start_cpu_load(processes):
    return [subprocess.Popen([sys.executable, "-c", "while True: pass"]) for _ in range(processes)]


def repeat(project, test_filter, runs, budget, stop_after, out_dir, host=None, load=0, run_timeout=600):
    workdir = SOURCE_ROOT / project
    host = host or f"../artifacts/bin/{project}/release/{project}"
    out = Path(out_dir)
    out.mkdir(parents=True, exist_ok=True)
    spinners = start_cpu_load(load)
    started = time.monotonic()
    done = failures = 0
    try:
        while done < runs and time.monotonic() - started < budget:
            try:
                result = subprocess.run([host, "--filter", test_filter], cwd=workdir, capture_output=True,
                                        text=True, timeout=run_timeout)
                code, output = result.returncode, result.stdout + result.stderr
            except subprocess.TimeoutExpired as expired:
                code, output = -1, f"timed out after {run_timeout}s\n{expired.stdout or ''}"
            if code == ZERO_TESTS_RAN:
                raise SystemExit(f"filter {test_filter!r} matched no tests in {project}")
            done += 1
            if code != 0:
                failures += 1
                if failures <= 3:
                    (out / f"failure-{failures}.log").write_text(output, encoding="utf-8")
                if stop_after and failures >= stop_after:
                    break
    finally:
        for spinner in spinners:
            spinner.kill()
    return {"runs": done, "failures": failures, "seconds": round(time.monotonic() - started),
            "verdict": classify(done, failures)}


def write_outputs(values):
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        return
    with open(path, "a", encoding="utf-8") as output:
        for key, value in values.items():
            text = str(value)
            if "\n" in text:
                output.write(f"{key}<<FLAKY_EOF\n{text}\nFLAKY_EOF\n")
            else:
                output.write(f"{key}={text}\n")


def main(argv=None):
    parser = argparse.ArgumentParser()
    commands = parser.add_subparsers(dest="command", required=True)

    select_parser = commands.add_parser("select")
    select_parser.add_argument("--log-dir", required=True)
    select_parser.add_argument("--repo", required=True)
    select_parser.add_argument("--label", default="flaky-test")
    select_parser.add_argument("--daily-limit", type=int, default=3)
    select_parser.add_argument("--lookback-days", type=int, default=14)
    select_parser.add_argument("--out", default="flaky-candidate.json")

    repeat_parser = commands.add_parser("repeat")
    repeat_parser.add_argument("--project", required=True)
    repeat_parser.add_argument("--filter", required=True)
    repeat_parser.add_argument("--runs", type=int, required=True)
    repeat_parser.add_argument("--budget", type=int, required=True)
    repeat_parser.add_argument("--stop-after", type=int, default=0)
    repeat_parser.add_argument("--out-dir", default="flaky-repeat")
    repeat_parser.add_argument("--load", type=int, default=max(1, (os.cpu_count() or 2) // 2))

    runs_parser = commands.add_parser("required-runs")
    runs_parser.add_argument("runs", type=int)
    runs_parser.add_argument("failures", type=int)

    args = parser.parse_args(argv)
    if args.command == "select":
        candidate, reason = select(args.log_dir, args.repo, args.label, args.daily_limit, args.lookback_days)
        if candidate is None:
            print(reason)
            write_outputs({"found": "false", "reason": reason})
            return 0
        print(json.dumps(candidate, indent=2))
        Path(args.out).write_text(json.dumps(candidate, indent=2), encoding="utf-8")
        write_outputs({key: candidate[key] for key in ("project", "method", "test", "source", "branch")} | {"found": "true"})
    elif args.command == "repeat":
        result = repeat(args.project, args.filter, args.runs, args.budget, args.stop_after, args.out_dir,
                        load=args.load)
        print(json.dumps(result))
        write_outputs(result)
    else:
        runs = required_runs(args.runs, args.failures)
        print(runs)
        write_outputs({"runs": runs})
    return 0


if __name__ == "__main__":
    sys.exit(main())
