#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""History-based EEST test selection, evaluated in shadow mode.

collect:   append one journal line per completed run of nethermind-tests.yml (changed files, failed tests).
inventory: write the cost of every EEST unit (test type, setup, test function, fork) from one full run.
shadow:    replay the journal in time order, decide for every pull request run what the selector would
           run with the history before it, and report the share of EEST cost and the failures it misses.

Nothing here changes what CI runs.
"""

import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import time
from collections import defaultdict, deque
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = "src/Nethermind"
WORKFLOW = "nethermind-tests.yml"
ALL = "*"

# GitHub's compare API lists at most 300 files; a longer diff is treated as touching everything.
COMPARE_FILE_LIMIT = 300
JOURNAL_RETENTION = timedelta(days=60)
# A run whose jobs mostly fail at once is a runner or checkout problem; its first jobs decide.
SAMPLE_WHEN_WIDESPREAD = 5

# Projects whose change can alter any EEST result, so the selector runs everything.
HARNESS = {f"{SRC}/Ethereum.Test.Base", f"{SRC}/Nethermind.Test.Runner"}
# Files outside any project that cannot change test results.
INERT = re.compile(r"(\.md$|^docs/|^LICENSE|^\.github/(ISSUE_TEMPLATE|CODEOWNERS)|^tools/)")

ANSI = re.compile(r"\x1b\[[0-9;]*m")
NETHTEST_FAILURE = re.compile(r"Z (?:FAIL|EXCEPTION) (?:\[\d+(?:/\d+)?\] )?(test_\w+)\[fork_(\w+?)[-\]]")
NETHTEST_CASE = re.compile(r"(test_\w+)\[fork_(\w+?)[-\]]")
MTP_FAILURE = re.compile(r"Z\s+failed ([\w.]+)")


def gh(path, paginate=True, attempts=3):
    command = ["gh", "api", "--paginate", "--slurp", path] if paginate else ["gh", "api", path]
    for attempt in range(attempts):
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode == 0:
            pages = json.loads(result.stdout)
            return pages if paginate else [pages]
        if "rate limit" in result.stderr.lower():
            # Skipping a run would drop its failures and understate misses, so wait instead.
            limits = json.loads(subprocess.run(["gh", "api", "rate_limit"], capture_output=True, text=True).stdout)
            time.sleep(max(limits["resources"]["core"]["reset"] - time.time(), 0) + 5)
            continue
        time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"gh api {path}: {result.stderr.strip()}")


def gh_text(path):
    result = subprocess.run(["gh", "api", path], capture_output=True, text=True)
    return result.stdout if result.returncode == 0 else ""


def unit_of(job_name):
    if job_name.startswith("EEST "):
        return "EEST"
    if job_name.startswith("Hive "):
        return "Hive"
    match = re.match(r"Run ((?:Nethermind|Ethereum)\.[\w.]+)", job_name)
    return match.group(1) if match else None


def nethtest_lane(job_name):
    """'type|setup' of a nethtest job, named 'EEST nethtest / nethtest <type> <setup> <chunk>' when
    nethermind-tests.yml calls run-nethtest.yml, and without the prefix when run-nethtest.yml runs alone."""
    words = job_name.split(" / ", 1)[-1].split()
    return f"{words[1]}|{words[2]}" if len(words) >= 3 and words[0] == "nethtest" else None


def job_failures(log, job_name):
    """Test-level failures in one failed job's log; empty when the job failed outside the tests."""
    log = ANSI.sub("", log)
    unit = unit_of(job_name)
    if unit == "EEST":
        lane = nethtest_lane(job_name)
        return sorted({f"EEST|{lane}|{name}|{fork}" for name, fork in NETHTEST_FAILURE.findall(log)})
    return sorted({f"{unit}|{name}" for name in MTP_FAILURE.findall(log)})


def classify(repo, jobs):
    """Failed tests of a run; units whose jobs show no test-level failure are recorded as infra."""
    sampled = jobs if len(jobs) <= SAMPLE_WHEN_WIDESPREAD * 3 else jobs[:SAMPLE_WHEN_WIDESPREAD]
    tests = set()
    infra = set()
    for job in sampled:
        found = job_failures(gh_text(f"repos/{repo}/actions/jobs/{job['id']}/logs"), job["name"])
        tests.update(found)
        if not found:
            infra.add(unit_of(job["name"]))
    if len(sampled) < len(jobs) and not tests:
        infra.update(unit_of(job["name"]) for job in jobs[len(sampled):])
    return {"tests": sorted(tests), "infra": sorted(infra)}


def run_jobs(repo, run_id):
    # Large pages sometimes fail with HTTP 502 on runs with hundreds of jobs; small pages then succeed.
    try:
        return gh(f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100", attempts=2)
    except RuntimeError:
        return gh(f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=30")


def changed_files(repo, run):
    """Files the run's head changes against its merge base with master; local git first, the API otherwise."""
    sha = run["head_sha"]
    base = subprocess.run(["git", "merge-base", "origin/master", sha], capture_output=True, text=True, cwd=ROOT)
    if base.returncode == 0:
        diff = subprocess.run(
            ["git", "diff", "--name-only", base.stdout.strip(), sha],
            check=True, capture_output=True, text=True, cwd=ROOT,
        )
        return sorted(diff.stdout.split())
    head = f"{run['head_repository']['owner']['login']}:{sha}"
    # Pagination walks the commits; the file list is complete on the first page.
    compare = gh(f"repos/{repo}/compare/master...{head}", paginate=False)
    files = sorted({entry["filename"] for page in compare for entry in page.get("files", [])})
    return [ALL] if len(files) >= COMPARE_FILE_LIMIT else files


def read_journal(path):
    path = Path(path)
    return [json.loads(line) for line in path.read_text().splitlines() if line] if path.exists() else []


def collect(args):
    repo = args.repo
    entries = read_journal(args.journal)
    seen = {entry["run"] for entry in entries}
    until = datetime.now(timezone.utc)
    since = until - timedelta(days=args.days)
    if entries and not args.full:
        # Overlap the last hour so runs that were still in progress at the previous collect are picked up.
        since = max(since, datetime.fromisoformat(max(entry["at"] for entry in entries)) - timedelta(hours=1))
    pages = gh(
        f"repos/{repo}/actions/workflows/{WORKFLOW}/runs"
        f"?status=completed&created={since:%Y-%m-%dT%H:%M:%SZ}..{until:%Y-%m-%dT%H:%M:%SZ}&per_page=100"
    )
    runs = [
        run for page in pages for run in page["workflow_runs"]
        if run["id"] not in seen and run["conclusion"] in ("success", "failure")
        and run["event"] in ("pull_request", "push")
    ]

    def entry(run):
        try:
            failed = None
            if run["conclusion"] == "failure":
                jobs = [
                    job for page in run_jobs(repo, run["id"]) for job in page["jobs"]
                    if job["conclusion"] == "failure" and unit_of(job["name"])
                ]
                failed = classify(repo, jobs)
            return {
                "run": run["id"], "at": run["created_at"], "event": run["event"],
                "branch": run["head_branch"], "files": changed_files(repo, run), "failed": failed,
            }
        except RuntimeError as error:
            print(f"skipping run {run['id']}: {error}", file=sys.stderr)
            return None

    with ThreadPoolExecutor(max_workers=args.workers) as pool:
        entries.extend(line for line in pool.map(entry, runs) if line)
    cutoff = until - JOURNAL_RETENTION
    entries = sorted((e for e in entries if datetime.fromisoformat(e["at"]) >= cutoff), key=lambda e: e["at"])
    Path(args.journal).write_text("".join(json.dumps(e) + "\n" for e in entries))
    print(f"collected {len(runs)} runs, journal holds {len(entries)}")


def inventory(args):
    """Cost in nethtest step seconds of every EEST unit, from the artifacts and job times of one run."""
    repo = args.repo
    seconds = defaultdict(float)
    for page in run_jobs(repo, args.run):
        for job in page["jobs"]:
            lane = nethtest_lane(job["name"])
            if not lane:
                continue
            for step in job.get("steps", []):
                if step["name"] == "Run nethtest and parse results" and step.get("completed_at"):
                    took = datetime.fromisoformat(step["completed_at"]) - datetime.fromisoformat(step["started_at"])
                    seconds[lane] += took.total_seconds()
    cases = defaultdict(int)
    lane_cases = defaultdict(int)
    with tempfile.TemporaryDirectory() as directory:
        subprocess.run(
            ["gh", "run", "download", str(args.run), "-R", repo, "-p", "nethtest-results-*", "-D", directory],
            check=True, capture_output=True,
        )
        for results in Path(directory).glob("*/nethtest-results.json"):
            # nethtest-results-<type>-<setup>-<chunk>[-<suffix>]
            test_type, setup = results.parent.name[len("nethtest-results-"):].split("-")[:2]
            for case in json.loads(results.read_text()):
                match = NETHTEST_CASE.match(case["name"])
                name, fork = (match.group(1), match.group(2)) if match else (case["name"].split("[")[0], case.get("fork", ""))
                cases[f"EEST|{test_type}|{setup}|{name}|{fork}"] += 1
                lane_cases[f"{test_type}|{setup}"] += 1
    costs = {
        unit: count * seconds[lane] / lane_cases[lane]
        for unit, count in cases.items()
        for lane in [unit.split("|", 3)[1] + "|" + unit.split("|", 3)[2]]
    }
    Path(args.out).write_text(json.dumps(costs))
    print(f"{len(costs)} units, {sum(lane_cases.values())} cases, {sum(costs.values()):.0f} step seconds")


def project_graph():
    references = {}
    for csproj in (ROOT / SRC).glob("*/*.csproj"):
        directory = csproj.parent.relative_to(ROOT).as_posix()
        references[directory] = {
            (csproj.parent / include.replace("\\", "/")).resolve().parent.relative_to(ROOT).as_posix()
            for include in re.findall(r'ProjectReference\s+Include="([^"]+)"', csproj.read_text())
        }
    return references


def changed_projects(files, projects):
    changed = set()
    for name in files:
        if name == ALL:
            return {ALL}
        owner = next((p for p in projects if name.startswith(p + "/")), None)
        if owner:
            changed.add(owner)
        elif not INERT.search(name):
            return {ALL}
    return changed


def closure(references, start):
    seen = {start}
    queue = deque([start])
    while queue:
        for dependency in references.get(queue.popleft(), ()):
            if dependency not in seen:
                seen.add(dependency)
                queue.append(dependency)
    return seen


class Selector:
    """Runs a test when it failed recently, failed before together with a changed project, or belongs to a
    fork named in the changed files; plus a deterministic random sample. Runs everything on harness and
    build-wide changes. Runs that break more than MASS_FAILURES tests teach no links: they would tie the
    changed projects to every test, and the sample already catches changes that break that many."""

    MASS_FAILURES = 500
    UNSTABLE_WINDOW = timedelta(days=14)
    UNSTABLE_MIN_FAILURES = 2
    SAMPLE_RATE = 0.02

    def __init__(self, forks):
        self.forks = [fork for fork in forks if len(fork) > 3]
        self.learned = defaultdict(set)
        self.failures = defaultdict(list)

    def decide(self, run, at, changed, files):
        """A predicate over (test function, fork), or None when every test runs."""
        if ALL in changed or changed & HARNESS:
            return None
        names = {
            name for name, times in self.failures.items()
            if sum(1 for t in times if at - t <= self.UNSTABLE_WINDOW) >= self.UNSTABLE_MIN_FAILURES
        }
        for project in changed:
            names |= self.learned[project]
        forks = {fork for fork in self.forks for name in files if fork.lower() in name.lower()}
        return lambda name, fork: name in names or fork in forks or sampled(run, name, fork, self.SAMPLE_RATE)

    def observe(self, at, changed, failed_names):
        if not failed_names or len(failed_names) > self.MASS_FAILURES:
            return
        for name in failed_names:
            self.failures[name].append(at)
            for project in changed - {ALL}:
                self.learned[project].add(name)


def sampled(run, name, fork, rate):
    digest = hashlib.blake2b(f"{run}|{name}|{fork}".encode(), digest_size=8).digest()
    return int.from_bytes(digest, "big") % 10_000 < rate * 10_000


def eest_failures(entry):
    """(test function, fork) pairs that failed in the run's EEST jobs."""
    tests = (entry.get("failed") or {}).get("tests", [])
    return {(test.split("|")[3], test.split("|")[4]) for test in tests if test.startswith("EEST|")}


def markdown(text):
    return str(text).replace("|", "\\|").replace("`", "'").replace("<", "&lt;")


def shadow(args):
    costs = json.loads(Path(args.inventory).read_text())
    by_pair = defaultdict(float)
    for unit, cost in costs.items():
        _, _, _, name, fork = unit.split("|")
        by_pair[(name, fork)] += cost
    total = sum(by_pair.values())
    projects = sorted(project_graph(), key=len, reverse=True)
    selector = Selector({fork for _, fork in by_pair})
    entries = sorted(read_journal(args.journal), key=lambda e: e["at"])
    if not entries:
        print("journal is empty")
        return
    start = datetime.fromisoformat(entries[0]["at"]) + timedelta(days=args.warmup_days)

    measured = 0
    selected = 0.0
    failing = []
    missed = []
    for entry in entries:
        at = datetime.fromisoformat(entry["at"])
        changed = changed_projects(entry["files"], projects)
        failures = eest_failures(entry)
        if entry["event"] == "pull_request" and at >= start:
            keep = selector.decide(entry["run"], at, changed, entry["files"])
            measured += 1
            selected += 1.0 if keep is None else sum(c for (n, f), c in by_pair.items() if keep(n, f)) / total
            if failures:
                failing.append(entry)
                if keep is not None and not any(keep(name, fork) for name, fork in failures):
                    missed.append((entry, changed, failures))
        selector.observe(at, changed, {name for name, _ in failures})

    share = 100 * selected / measured if measured else 0.0
    lines = [
        "## EEST test selection (shadow mode)",
        "",
        f"Journal: {len(entries)} runs from {entries[0]['at'][:10]} to {entries[-1]['at'][:10]}; "
        f"the first {args.warmup_days} days only build history.",
        "",
        "| Pull request runs measured | EEST cost the selector would run | Runs with EEST failures | Runs it would report green |",
        "|---|---|---|---|",
        f"| {measured} | {share:.1f}% | {len(failing)} ({len({e['branch'] for e in failing})} branches) | {len(missed)} |",
    ]
    if missed:
        lines += ["", "### Missed runs", "", "| Run | Branch | Changed projects | Failed EEST tests |", "|---|---|---|---|"]
        for entry, changed, failures in missed:
            names = ", ".join(f"{name} ({fork})" for name, fork in sorted(failures)[:3])
            projects_text = ", ".join(sorted(p.rsplit("/", 1)[-1] for p in changed))[:200]
            lines.append(
                f"| [{entry['run']}](https://github.com/{args.repo}/actions/runs/{entry['run']}) | {markdown(entry['branch'])} "
                f"| {markdown(projects_text)} | {markdown(names)}{' ...' if len(failures) > 3 else ''} |"
            )
    report = "\n".join(lines) + "\n"
    print(report)
    if args.report:
        Path(args.report).write_text(report)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "NethermindEth/nethermind"))
    commands = parser.add_subparsers(dest="command", required=True)
    collector = commands.add_parser("collect")
    collector.add_argument("--journal", required=True)
    collector.add_argument("--days", type=int, default=7)
    collector.add_argument("--full", action="store_true", help="re-scan the whole window, not only new runs")
    collector.add_argument("--workers", type=int, default=4)
    inventory_parser = commands.add_parser("inventory")
    inventory_parser.add_argument("--run", type=int, required=True)
    inventory_parser.add_argument("--out", required=True)
    shadow_parser = commands.add_parser("shadow")
    shadow_parser.add_argument("--journal", required=True)
    shadow_parser.add_argument("--inventory", required=True)
    shadow_parser.add_argument("--warmup-days", type=int, default=7)
    shadow_parser.add_argument("--report")
    args = parser.parse_args()
    {"collect": collect, "inventory": inventory, "shadow": shadow}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
