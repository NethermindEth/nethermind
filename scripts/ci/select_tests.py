#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""History-based EEST test selection for pull requests.

collect:   append one journal line per completed run of nethermind-tests.yml (changed files, failed tests).
inventory: write the cost of every EEST unit (test type, setup, test function, fork) from one full run.
model:     replay the journal into the selector's state (test-project links, recently failing tests).
select:    for one pull request, write a nethtest --filterFile per lane and shrink the EEST matrix to match.
replay:    decide for every recorded pull request run what the selector would have run with the history before
           it, and report the share of EEST cost and the failures it would have hidden.
"""

import argparse
import hashlib
import json
import math
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


# The core API budget is shared with every other job and tool on the token; collecting backs off when less
# than this share of the hourly limit remains, so it never spends the whole hour on its own.
API_RESERVE_SHARE = 0.3
_calls = 0


def wait_for_budget():
    global _calls
    _calls += 1
    if _calls % 50:
        return
    core = json.loads(subprocess.run(["gh", "api", "rate_limit"], capture_output=True, text=True).stdout)["resources"]["core"]
    if core["remaining"] < core["limit"] * API_RESERVE_SHARE:
        time.sleep(max(core["reset"] - time.time(), 0) + 5)


def api(path, paginate=True, raw=False, attempts=4):
    """One GitHub API call through gh. Rate limits are waited out, never turned into empty results: a lost
    log would record its failures as infra and understate misses. Persistent errors raise."""
    command = ["gh", "api", "--paginate", "--slurp", path] if paginate and not raw else ["gh", "api", path]
    wait_for_budget()
    for attempt in range(attempts):
        result = subprocess.run(command, capture_output=True, text=True)
        if result.returncode == 0:
            if raw:
                return result.stdout
            pages = json.loads(result.stdout)
            return pages if paginate else [pages]
        error = result.stderr.lower()
        if "secondary rate limit" in error:
            time.sleep(60 * (attempt + 1))
        elif "rate limit" in error:
            limits = json.loads(subprocess.run(["gh", "api", "rate_limit"], capture_output=True, text=True).stdout)
            time.sleep(max(limits["resources"]["core"]["reset"] - time.time(), 0) + 5)
        else:
            time.sleep(5 * (attempt + 1))
    raise RuntimeError(f"gh api {path}: {result.stderr.strip()}")


def gh(path, paginate=True, attempts=4):
    return api(path, paginate=paginate, attempts=attempts)


def gh_text(path):
    return api(path, raw=True)


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
    """Failed tests of a run; units whose jobs show no test-level failure are recorded as infra.

    EEST jobs and the other jobs are judged apart, so unit-test failures never hide EEST ones. When many
    jobs of a group fail, a sample decides: no test-level failure in it marks the rest of the group as infra
    (a runner or checkout problem), otherwise every job of the group is read."""
    tests = set()
    infra = set()

    def read(job):
        found = job_failures(gh_text(f"repos/{repo}/actions/jobs/{job['id']}/logs"), job["name"])
        tests.update(found)
        if not found:
            infra.add(unit_of(job["name"]))
        return bool(found)

    for group in (
        [job for job in jobs if unit_of(job["name"]) == "EEST"],
        [job for job in jobs if unit_of(job["name"]) != "EEST"],
    ):
        sample = group if len(group) <= SAMPLE_WHEN_WIDESPREAD * 3 else group[:SAMPLE_WHEN_WIDESPREAD]
        sample_failed = [read(job) for job in sample]
        rest = group[len(sample):]
        if any(sample_failed):
            for job in rest:
                read(job)
        else:
            infra.update(unit_of(job["name"]) for job in rest)
    return {"tests": sorted(tests), "infra": sorted(infra)}


def run_jobs(repo, run_id):
    # Large pages sometimes fail with HTTP 502 on runs with hundreds of jobs; small pages then succeed.
    try:
        return gh(f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100", attempts=2)
    except RuntimeError:
        return gh(f"repos/{repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=30")


def changed_files(repo, run):
    """Files the run changes: a pull request against its merge base with master, a master push (one squash
    merge) against its parent. Local git first, the API otherwise."""
    sha = run["head_sha"]
    push = run["event"] == "push"
    base = (
        subprocess.run(["git", "rev-parse", f"{sha}^"], capture_output=True, text=True, cwd=ROOT) if push
        else subprocess.run(["git", "merge-base", "origin/master", sha], capture_output=True, text=True, cwd=ROOT)
    )
    if base.returncode == 0:
        diff = subprocess.run(
            ["git", "diff", "--name-only", base.stdout.strip(), sha],
            check=True, capture_output=True, text=True, cwd=ROOT,
        )
        return sorted(diff.stdout.split())
    if push:
        parent = gh(f"repos/{repo}/commits/{sha}", paginate=False)[0]["parents"][0]["sha"]
        compare_path = f"repos/{repo}/compare/{parent}...{sha}"
    else:
        compare_path = f"repos/{repo}/compare/master...{run['head_repository']['owner']['login']}:{sha}"
    # Pagination walks the commits; the file list is complete on the first page.
    compare = gh(compare_path, paginate=False)
    files = sorted({entry["filename"] for page in compare for entry in page.get("files", [])})
    return [ALL] if len(files) >= COMPARE_FILE_LIMIT else files


def write_atomically(path, text):
    """A run stopped mid-write (job timeout, cancellation) must leave the previous journal, not an empty one."""
    temporary = Path(f"{path}.tmp")
    temporary.write_text(text)
    os.replace(temporary, path)


def read_journal(path):
    path = Path(path)
    return [json.loads(line) for line in path.read_text().splitlines() if line] if path.exists() else []


# The list-runs API returns at most 1,000 runs per filtered query, even with pagination.
LIST_RUNS_CAP = 1000
# Runs are found by creation time; a run can complete hours after later runs, so each collect re-reads
# this much before the newest recorded run. De-duplication by run id keeps the overlap safe.
COLLECT_OVERLAP = timedelta(hours=6)


def list_runs(repo, since, until):
    pages = gh(
        f"repos/{repo}/actions/workflows/{WORKFLOW}/runs"
        f"?status=completed&created={since:%Y-%m-%dT%H:%M:%SZ}..{until:%Y-%m-%dT%H:%M:%SZ}&per_page=100"
    )
    runs = [run for page in pages for run in page["workflow_runs"]]
    if len(runs) >= LIST_RUNS_CAP and until - since > timedelta(minutes=30):
        middle = since + (until - since) / 2
        return list_runs(repo, since, middle) + list_runs(repo, middle + timedelta(seconds=1), until)
    return runs


def list_runs_by_day(repo, since, until):
    runs = []
    start = since
    while start < until:
        end = min(start + timedelta(days=1), until)
        runs.extend(list_runs(repo, start, end - timedelta(seconds=1) if end < until else end))
        start = end
    return runs


def collect(args):
    repo = args.repo
    entries = read_journal(args.journal)
    until = datetime.now(timezone.utc) if args.until is None else datetime.fromisoformat(args.until)
    since = until - timedelta(days=args.days) if args.since is None else datetime.fromisoformat(args.since)
    windows = [(since, until)]
    if entries and not args.full and args.since is None:
        newest = datetime.fromisoformat(max(entry["at"] for entry in entries))
        oldest = datetime.fromisoformat(min(entry["at"] for entry in entries))
        windows = [(max(since, newest - COLLECT_OVERLAP), until)]
        # A young journal grows backwards a few days per collect, so history builds up without one run
        # spending hours of the API budget.
        target = until - timedelta(days=args.backfill_days)
        if oldest > target:
            windows.append((max(target, oldest - timedelta(days=args.backfill_step)), oldest))
    if args.rescan_failed:
        rescan(repo, entries, args.workers)
    seen = {entry["run"] for entry in entries}
    runs = [
        run for start, end in windows for run in list_runs_by_day(repo, start, end)
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
    cutoff = datetime.now(timezone.utc) - timedelta(days=args.retention_days)
    entries = sorted((e for e in entries if datetime.fromisoformat(e["at"]) >= cutoff), key=lambda e: e["at"])
    write_atomically(args.journal, "".join(json.dumps(e) + "\n" for e in entries))
    print(f"collected {len(runs)} runs, journal holds {len(entries)}")


def rescan(repo, entries, workers):
    """Classify the failures of recorded failed runs again, for a journal written by an older classifier."""

    def update(entry):
        if not entry.get("failed"):
            return
        try:
            jobs = [
                job for page in run_jobs(repo, entry["run"]) for job in page["jobs"]
                if job["conclusion"] == "failure" and unit_of(job["name"])
            ]
            entry["failed"] = classify(repo, jobs)
        except RuntimeError as error:
            print(f"keeping the recorded failures of run {entry['run']}: {error}", file=sys.stderr)

    with ThreadPoolExecutor(max_workers=workers) as pool:
        list(pool.map(update, entries))


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
    fork named in the changed files; plus a deterministic random sample. A changed project that recently
    broke several tests of one fork runs every test of that fork: a fork under active development breaks in
    new places. Runs everything on harness and build-wide changes. Runs that break more than MASS_FAILURES
    tests teach no links: they would tie the changed projects to every test, and the sample already catches
    changes that break that many."""

    MASS_FAILURES = 500
    UNSTABLE_WINDOW = timedelta(days=14)
    UNSTABLE_MIN_FAILURES = 2
    FORK_LINK_WINDOW = timedelta(days=14)
    FORK_LINK_MIN_TESTS = 5
    SAMPLE_RATE = 0.02

    def __init__(self, forks):
        self.forks = [fork for fork in forks if len(fork) > 3]
        self.learned = defaultdict(set)
        self.failures = defaultdict(list)
        # (project, fork) -> [(time, test)] for the tests of a fork that broke together with a project.
        self.fork_links = defaultdict(list)

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
        for (project, fork), events in self.fork_links.items():
            if project in changed and len({name for t, name in events if at - t <= self.FORK_LINK_WINDOW}) >= self.FORK_LINK_MIN_TESTS:
                forks.add(fork)
        return lambda name, fork: name in names or fork in forks or sampled(run, name, fork, self.SAMPLE_RATE)

    def observe(self, at, changed, failures):
        """failures: the (test function, fork) pairs that failed in one run."""
        names = {name for name, _ in failures}
        if not names or len(names) > self.MASS_FAILURES:
            return
        for name in names:
            self.failures[name].append(at)
            for project in changed - {ALL}:
                self.learned[project].add(name)
        for project in changed - {ALL}:
            for name, fork in failures:
                self.fork_links[(project, fork)].append((at, name))

    def to_json(self):
        return {
            "forks": self.forks,
            "learned": {project: sorted(names) for project, names in self.learned.items()},
            "failures": {name: [t.isoformat() for t in times] for name, times in self.failures.items()},
            "fork_links": [
                [project, fork, [[t.isoformat(), name] for t, name in events]]
                for (project, fork), events in self.fork_links.items()
            ],
        }

    @classmethod
    def from_json(cls, data):
        selector = cls(data["forks"])
        for project, names in data["learned"].items():
            selector.learned[project] = set(names)
        for name, times in data["failures"].items():
            selector.failures[name] = [datetime.fromisoformat(t) for t in times]
        for project, fork, events in data.get("fork_links", []):
            selector.fork_links[(project, fork)] = [(datetime.fromisoformat(t), name) for t, name in events]
        return selector


def sampled(run, name, fork, rate):
    digest = hashlib.blake2b(f"{run}|{name}|{fork}".encode(), digest_size=8).digest()
    return int.from_bytes(digest, "big") % 10_000 < rate * 10_000


def eest_failures(entry):
    """(test function, fork) pairs that failed in the run's EEST jobs."""
    tests = (entry.get("failed") or {}).get("tests", [])
    return {(test.split("|")[3], test.split("|")[4]) for test in tests if test.startswith("EEST|")}


# Below this much history the selector knows too few links, so a pull request runs every EEST test.
MIN_HISTORY_DAYS = 28
# An older model misses the failures of the last day, so a pull request runs every EEST test.
MAX_MODEL_AGE_HOURS = 24


def model(args):
    entries = sorted(read_journal(args.journal), key=lambda e: e["at"])
    forks = {unit.split("|")[4] for unit in json.loads(Path(args.inventory).read_text())}
    projects = sorted(project_graph(), key=len, reverse=True)
    selector = Selector(forks)
    for entry in entries:
        selector.observe(
            datetime.fromisoformat(entry["at"]),
            changed_projects(entry["files"], projects),
            eest_failures(entry),
        )
    data = {
        "built_at": datetime.now(timezone.utc).isoformat(),
        "history_from": entries[0]["at"] if entries else None,
        "history_to": entries[-1]["at"] if entries else None,
        **selector.to_json(),
    }
    write_atomically(args.out, json.dumps(data))
    print(f"model from {len(entries)} runs: {len(selector.learned)} projects with links, {len(selector.failures)} failing tests")


def lane_of(entry):
    return f"{entry['test_type']}|{entry['test_setup']}"


def select(args):
    """Writes the filter files and the reduced matrix for one pull request, or the matrix unchanged with the
    reason when every test has to run."""
    matrix = json.loads(Path(args.matrix).read_text())
    now = datetime.now(timezone.utc)
    reason = None
    data = json.loads(Path(args.model).read_text()) if Path(args.model).is_file() else None
    if data is None or not Path(args.inventory).is_file():
        reason = "no selection model"
    elif now - datetime.fromisoformat(data["built_at"]) > timedelta(hours=args.max_model_age_hours):
        reason = f"the model was built at {data['built_at']}"
    elif data["history_from"] is None or now - datetime.fromisoformat(data["history_from"]) < timedelta(days=args.min_history_days):
        reason = f"the model holds history from {data['history_from']} only"

    keep = None
    if reason is None:
        files = [line for line in Path(args.changed_files).read_text().splitlines() if line]
        changed = changed_projects(files, sorted(project_graph(), key=len, reverse=True))
        keep = Selector.from_json(data).decide(args.run, now, changed, files)
        if keep is None:
            reason = "the change touches the build or the EEST harness"
    if keep is None:
        write_atomically(args.out_matrix, json.dumps(matrix))
        print(f"running every EEST test: {reason}")
        return

    selected = defaultdict(lambda: defaultdict(list))
    cost = defaultdict(float)
    picked = defaultdict(float)
    for unit, unit_cost in json.loads(Path(args.inventory).read_text()).items():
        _, test_type, setup, name, fork = unit.split("|")
        lane = f"{test_type}|{setup}"
        cost[lane] += unit_cost
        if keep(name, fork):
            picked[lane] += unit_cost
            if fork not in selected[lane][name]:
                selected[lane][name].append(fork)

    filters = Path(args.filters)
    filters.mkdir(parents=True, exist_ok=True)
    for lane, names in selected.items():
        lines = [
            f"{re.escape(name)}\\[fork_(?:{'|'.join(re.escape(fork) for fork in sorted(forks))})[-\\]]"
            for name, forks in sorted(names.items())
        ]
        (filters / f"{lane.replace('|', '-')}.txt").write_text("\n".join(lines) + "\n")

    by_lane = defaultdict(list)
    for entry in matrix["include"]:
        by_lane[lane_of(entry)].append(entry)
    include = []
    for lane, entries in by_lane.items():
        if lane not in cost:
            include.extend(entries)
            continue
        chunks = math.ceil(len(entries) * picked[lane] / cost[lane]) if picked[lane] else 0
        include.extend({**entry, "chunk": f"{i}of{chunks}"} for i, entry in enumerate(entries[:chunks], 1))
    write_atomically(args.out_matrix, json.dumps({"include": include}))
    share = 100 * sum(picked.values()) / sum(cost.values())
    print(f"selected {share:.1f}% of the EEST cost; {len(include)} of {len(matrix['include'])} jobs")


def markdown(text):
    return str(text).replace("|", "\\|").replace("`", "'").replace("<", "&lt;")


def replay(args):
    if not Path(args.inventory).is_file():
        print(f"no inventory at {args.inventory}; run the inventory command first")
        return
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
        selector.observe(at, changed, failures)

    share = 100 * selected / measured if measured else 0.0
    lines = [
        "## EEST test selection replay",
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
    collector.add_argument("--since", help="ISO start of the window (default: --days before --until)")
    collector.add_argument("--until", help="ISO end of the window (default: now)")
    collector.add_argument("--rescan-failed", action="store_true", help="classify recorded failed runs again")
    collector.add_argument("--retention-days", type=int, default=60, help="drop journal entries older than this")
    collector.add_argument("--backfill-days", type=int, default=60, help="grow a younger journal back to this age")
    collector.add_argument("--backfill-step", type=int, default=3, help="days of backfill per collect")
    inventory_parser = commands.add_parser("inventory")
    inventory_parser.add_argument("--run", type=int, required=True)
    inventory_parser.add_argument("--out", required=True)
    model_parser = commands.add_parser("model")
    model_parser.add_argument("--journal", required=True)
    model_parser.add_argument("--inventory", required=True)
    model_parser.add_argument("--out", required=True)
    select_parser = commands.add_parser("select")
    select_parser.add_argument("--model", required=True)
    select_parser.add_argument("--inventory", required=True)
    select_parser.add_argument("--changed-files", required=True, help="one changed path per line")
    select_parser.add_argument("--run", required=True, help="seed of the random sample, the workflow run id")
    select_parser.add_argument("--matrix", required=True, help="the nethtest matrix, {include: [...]}")
    select_parser.add_argument("--out-matrix", required=True)
    select_parser.add_argument("--filters", required=True, help="directory for one --filterFile per lane")
    select_parser.add_argument("--min-history-days", type=int, default=MIN_HISTORY_DAYS)
    select_parser.add_argument("--max-model-age-hours", type=int, default=MAX_MODEL_AGE_HOURS)
    replay_parser = commands.add_parser("replay")
    replay_parser.add_argument("--journal", required=True)
    replay_parser.add_argument("--inventory", required=True)
    replay_parser.add_argument("--warmup-days", type=int, default=7)
    replay_parser.add_argument("--report")
    args = parser.parse_args()
    {"collect": collect, "inventory": inventory, "model": model, "select": select, "replay": replay}[args.command](args)


if __name__ == "__main__":
    sys.exit(main())
