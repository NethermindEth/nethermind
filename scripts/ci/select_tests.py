#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""History-based test selection for pull requests: EEST tests and NUnit test projects.

collect          Append the finished test runs to the journal: changed files and test-level failures.
inventory        Write the cost of every EEST unit and NUnit test project from one full master run.
model            Replay the journal into the selector's state.
select-eest      For one pull request, write a nethtest --filterFile per lane and the reduced EEST matrix.
select-projects  For one pull request, write the NUnit matrices of a workflow with only the selected projects.
replay           Report what the selector would have run and hidden on the recorded pull request runs.

The journal is the source of truth; the model is derived from it and can always be rebuilt. Whenever the
selection cannot be trusted (no model, stale or young model, build or harness change), every test runs.
"""

from __future__ import annotations

import argparse
import gzip
import hashlib
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import threading
import time
from collections import defaultdict
from collections.abc import Callable, Iterable
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SRC = "src/Nethermind"
# The workflows whose runs feed the journal: the main suite and its backend and build variants.
WORKFLOWS = ("nethermind-tests.yml", "nethermind-tests-checked.yml", "nethermind-tests-trie.yml",
             "nethermind-tests-single-proc.yml")
# Artifacts that a run uploads when it used the selection.
SELECTION_ARTIFACTS = frozenset({"eest-selection", "test-selection"})
ALL = "*"

# Changes to these projects can alter any EEST result.
HARNESS = frozenset({f"{SRC}/Ethereum.Test.Base", f"{SRC}/Nethermind.Test.Runner"})
# Paths outside every project that cannot change a test result. Any other such path touches everything.
INERT = re.compile(r"(\.md$|^docs/|^LICENSE|^\.github/(ISSUE_TEMPLATE|CODEOWNERS)|^tools/)")

ANSI_ESCAPE = re.compile(r"\x1b\[[0-9;]*m")
NETHTEST_CASE = re.compile(r"(test_\w+)\[fork_(\w+?)[-\]]")
NETHTEST_FAILURE = re.compile(r"Z (?:FAIL|EXCEPTION) (?:\[\d+(?:/\d+)?\] )?" + NETHTEST_CASE.pattern)
MTP_FAILURE = re.compile(r"Z\s+failed ([\w.]+)")



# ------------------------------------------------------------------------------------------------ GitHub boundary

class GitHub:
    """The GitHub REST API through gh. Rate limits are waited out, never turned into empty results: a lost
    log would record real failures as infra. The token's hourly budget is shared with other jobs, so calls
    pause while less than RESERVE_SHARE of it remains."""

    RESERVE_SHARE = 0.3
    BUDGET_CHECK_INTERVAL = 50
    ATTEMPTS = 4
    # The list-runs API returns at most this many runs per filtered query, even with pagination.
    LIST_RUNS_CAP = 1000

    def __init__(self, repo: str):
        self.repo = repo
        self._calls = 0
        self._lock = threading.Lock()

    def pages(self, path: str) -> list[dict]:
        return json.loads(self._call(["--paginate", "--slurp", path]))

    def object(self, path: str) -> dict:
        return json.loads(self._call([path]))

    def text(self, path: str) -> str:
        return self._call([path])

    def runs(self, since: datetime, until: datetime) -> list[dict]:
        """Completed runs of WORKFLOWS created in [since, until], one query per day, split further at the cap."""
        runs = []
        for workflow in WORKFLOWS:
            start = since
            while start < until:
                end = min(start + timedelta(days=1), until)
                runs.extend(self._runs_in(workflow, start, end))
                start = end + timedelta(seconds=1)
        return runs

    def jobs(self, run_id: int) -> list[dict]:
        return [job for page in self._job_pages(run_id) for job in page["jobs"]]

    def failed_jobs(self, run_id: int) -> list[dict]:
        return [job for job in self.jobs(run_id) if job["conclusion"] == "failure"]

    def artifact_names(self, run_id: int) -> set[str]:
        pages = self.pages(f"repos/{self.repo}/actions/runs/{run_id}/artifacts?per_page=100")
        return {artifact["name"] for page in pages for artifact in page["artifacts"]}

    def _runs_in(self, workflow: str, since: datetime, until: datetime) -> list[dict]:
        window = f"{since:%Y-%m-%dT%H:%M:%SZ}..{until:%Y-%m-%dT%H:%M:%SZ}"
        path = f"repos/{self.repo}/actions/workflows/{workflow}/runs?status=completed&created={window}&per_page=100"
        runs = [run for page in self.pages(path) for run in page["workflow_runs"]]
        if len(runs) < self.LIST_RUNS_CAP or until - since <= timedelta(minutes=30):
            return runs
        middle = since + (until - since) / 2
        return self._runs_in(workflow, since, middle) + self._runs_in(workflow, middle + timedelta(seconds=1), until)

    def _job_pages(self, run_id: int) -> list[dict]:
        # Large pages sometimes fail with HTTP 502 on runs with hundreds of jobs; small pages then succeed.
        try:
            return self.pages(f"repos/{self.repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=100")
        except GitHubError:
            return self.pages(f"repos/{self.repo}/actions/runs/{run_id}/jobs?filter=latest&per_page=30")

    def _call(self, arguments: list[str]) -> str:
        self._wait_for_budget()
        error = ""
        for attempt in range(self.ATTEMPTS):
            result = subprocess.run(["gh", "api", *arguments], capture_output=True, text=True)
            if result.returncode == 0:
                return result.stdout
            error = result.stderr.strip()
            time.sleep(self._backoff(error.lower(), attempt))
        raise GitHubError(f"gh api {arguments[-1]}: {error}")

    def _backoff(self, error: str, attempt: int) -> float:
        if "secondary rate limit" in error:
            return 60 * (attempt + 1)
        if "rate limit" in error:
            return self._seconds_until_reset()
        return 5 * (attempt + 1)

    def _wait_for_budget(self) -> None:
        with self._lock:
            self._calls += 1
            due = self._calls % self.BUDGET_CHECK_INTERVAL == 0
        if not due:
            return
        core = self._core_limit()
        if core["remaining"] < core["limit"] * self.RESERVE_SHARE:
            time.sleep(self._seconds_until(core["reset"]))

    def _seconds_until_reset(self) -> float:
        return self._seconds_until(self._core_limit()["reset"])

    @staticmethod
    def _seconds_until(reset: float) -> float:
        return max(reset - time.time(), 0) + 5

    @staticmethod
    def _core_limit() -> dict:
        result = subprocess.run(["gh", "api", "rate_limit"], capture_output=True, text=True, check=True)
        return json.loads(result.stdout)["resources"]["core"]


class GitHubError(RuntimeError):
    pass


# --------------------------------------------------------------------------------------------- Git boundary

class Git:
    """Changed files of a run from the local clone, fetching missing commits on demand."""

    def __init__(self, github: GitHub):
        self.github = github
        self._fetch_lock = threading.Lock()

    def changed_files(self, run: dict) -> list[str]:
        """A pull request against its merge base with master; a master push, one squash merge, against its parent."""
        head = run["head_sha"]
        try:
            base = self._parent(head) if run["event"] == "push" else self._merge_base(head)
            return self._git("diff", "--name-only", base, head).split()
        except subprocess.CalledProcessError:
            return self._changed_files_from_api(run)

    def _parent(self, sha: str) -> str:
        self._ensure(sha)
        return self._git("rev-parse", f"{sha}^").strip()

    def _merge_base(self, sha: str) -> str:
        self._ensure(sha)
        return self._git("merge-base", "origin/master", sha).strip()

    def _ensure(self, sha: str) -> None:
        if subprocess.run(["git", "cat-file", "-e", f"{sha}^{{commit}}"], cwd=ROOT, capture_output=True).returncode:
            with self._fetch_lock:
                self._git("fetch", "--quiet", "--no-tags", "--filter=blob:none", "origin", sha)

    def _changed_files_from_api(self, run: dict) -> list[str]:
        repo = self.github.repo
        head = run["head_sha"]
        if run["event"] == "push":
            base = self.github.object(f"repos/{repo}/commits/{head}")["parents"][0]["sha"]
            compare = self.github.object(f"repos/{repo}/compare/{base}...{head}")
        else:
            owner = run["head_repository"]["owner"]["login"]
            compare = self.github.object(f"repos/{repo}/compare/master...{owner}:{head}")
        files = [entry["filename"] for entry in compare.get("files", [])]
        # The compare API lists at most 300 files; a longer diff is treated as touching everything.
        return [ALL] if len(files) >= 300 else files

    @staticmethod
    def _git(*arguments: str) -> str:
        return subprocess.run(["git", *arguments], cwd=ROOT, capture_output=True, text=True, check=True).stdout


# ------------------------------------------------------------------------------------------------- Journal

@dataclass
class Failures:
    """Test-level failures of one run (EEST as 'EEST|type|setup|test|fork', NUnit as 'Project|test'), and the
    units whose failed jobs showed no test-level failure."""

    tests: list[str] = field(default_factory=list)
    infra: list[str] = field(default_factory=list)

    def eest(self) -> set[tuple[str, str]]:
        return {(test.split("|")[3], test.split("|")[4]) for test in self.tests if test.startswith("EEST|")}

    def projects(self) -> set[str]:
        return {test.split("|")[0] for test in self.tests if not test.startswith(("EEST|", "Hive|"))}


@dataclass
class Entry:
    run: int
    at: datetime
    event: str
    branch: str
    files: list[str]
    failed: Failures | None
    # Whether the run used the selection, so it can only fail in the tests it selected.
    selected: bool = False

    def eest_failures(self) -> set[tuple[str, str]]:
        return self.failed.eest() if self.failed else set()

    def failed_projects(self) -> set[str]:
        return self.failed.projects() if self.failed else set()

    def to_json(self) -> dict:
        return {**asdict(self), "at": self.at.isoformat()}

    @classmethod
    def from_json(cls, data: dict) -> Entry:
        failed = data.get("failed")
        return cls(
            run=data["run"], at=datetime.fromisoformat(data["at"]), event=data["event"], branch=data["branch"],
            files=data["files"], failed=Failures(**failed) if failed else None, selected=data.get("selected", False),
        )


def read_journal(path: Path) -> list[Entry]:
    if not path.is_file():
        return []
    with open_text(path) as source:
        return sorted((Entry.from_json(json.loads(line)) for line in source if line.strip()), key=lambda e: e.at)


def write_journal(path: Path, entries: Iterable[Entry]) -> None:
    write_atomically(path, "".join(json.dumps(entry.to_json()) + "\n" for entry in entries))


def read_json(path: Path) -> dict:
    with open_text(path) as source:
        return json.load(source)


def write_json(path: Path, data: dict) -> None:
    write_atomically(path, json.dumps(data))


def open_text(path: Path):
    return gzip.open(path, "rt") if path.suffix == ".gz" else open(path)


def write_atomically(path: Path, text: str) -> None:
    """A run stopped mid-write (timeout, cancellation) must leave the previous file, not a truncated one."""
    temporary = path.with_name(path.name + ".tmp")
    with (gzip.open(temporary, "wt") if path.suffix == ".gz" else open(temporary, "w")) as sink:
        sink.write(text)
    os.replace(temporary, path)


# ------------------------------------------------------------------------------------------ Failures in logs

# When many jobs of a group fail at once, the first ones decide whether it was a runner or checkout problem.
INFRA_SAMPLE = 5


def unit_of(job_name: str) -> str | None:
    if job_name.startswith("EEST "):
        return "EEST"
    if job_name.startswith("Hive "):
        return "Hive"
    match = re.match(r"Run ((?:Nethermind|Ethereum)\.[\w.]+)", job_name)
    return match.group(1) if match else None


def nethtest_lane(job_name: str) -> str | None:
    """'type|setup' of a nethtest job. nethermind-tests.yml names it 'EEST nethtest / nethtest <type> <setup>
    <chunk>'; run-nethtest.yml alone drops the prefix."""
    words = job_name.split(" / ", 1)[-1].split()
    return f"{words[1]}|{words[2]}" if len(words) >= 3 and words[0] == "nethtest" else None


def test_failures(log: str, job_name: str) -> set[str]:
    log = ANSI_ESCAPE.sub("", log)
    unit = unit_of(job_name)
    if unit == "EEST":
        lane = nethtest_lane(job_name)
        return {f"EEST|{lane}|{name}|{fork}" for name, fork in NETHTEST_FAILURE.findall(log)}
    return {f"{unit}|{name}" for name in MTP_FAILURE.findall(log)}


def classify(jobs: list[dict], read_log: Callable[[dict], str]) -> Failures:
    """EEST jobs and the other jobs are judged apart, so unit-test failures never hide EEST ones."""
    tests: set[str] = set()
    infra: set[str] = set()
    for group in ([j for j in jobs if unit_of(j["name"]) == "EEST"], [j for j in jobs if unit_of(j["name"]) != "EEST"]):
        found = read_group(group, read_log)
        tests.update(test for found_in_job in found.values() for test in found_in_job)
        infra.update(unit_of(job["name"]) for job in group if not found.get(job["id"]))
    return Failures(sorted(tests), sorted(infra))


def read_group(group: list[dict], read_log: Callable[[dict], str]) -> dict[int, set[str]]:
    """Test-level failures per job id. When many jobs of a group fail, a sample without any marks the rest as
    infra (a runner or checkout problem) without reading their logs."""
    sample = group if len(group) <= INFRA_SAMPLE * 3 else group[:INFRA_SAMPLE]
    found = {job["id"]: test_failures(read_log(job), job["name"]) for job in sample}
    if any(found.values()):
        found.update({job["id"]: test_failures(read_log(job), job["name"]) for job in group[len(sample):]})
    return found


# -------------------------------------------------------------------------------------------------- Projects

class Projects:
    """The source projects of the repository and their references, for mapping changed files to projects and
    projects to the test projects that use them."""

    def __init__(self, references: dict[str, set[str]]):
        self.references = references
        # Longest first, so a file maps to the most specific project directory.
        self.directories = sorted(references, key=len, reverse=True)

    @classmethod
    def of_repository(cls) -> Projects:
        return cls({
            csproj.parent.relative_to(ROOT).as_posix(): project_references(csproj)
            for csproj in (ROOT / SRC).glob("*/*.csproj")
        })

    def changed(self, files: Iterable[str]) -> set[str]:
        """Projects that the files belong to, or {ALL} when a file can affect every project."""
        changed = set()
        for name in files:
            owner = next((d for d in self.directories if name.startswith(d + "/")), None)
            if owner:
                changed.add(owner)
            elif name == ALL or not INERT.search(name):
                return {ALL}
        return changed

    def direct_tests(self, changed: set[str]) -> set[str]:
        """Test projects that changed, or that reference a changed project directly."""
        return {
            directory.rsplit("/", 1)[-1] for directory, references in self.references.items()
            if directory.endswith(".Test") and (directory in changed or references & changed)
        }


def project_references(csproj: Path) -> set[str]:
    return {
        (csproj.parent / include.replace("\\", "/")).resolve().parent.relative_to(ROOT.resolve()).as_posix()
        for include in re.findall(r'ProjectReference\s+Include="([^"]+)"', csproj.read_text())
    }


# -------------------------------------------------------------------------------------------------- Selector

SAMPLE_RATE = 0.02


def sampled(key: str, rate: float = SAMPLE_RATE) -> bool:
    """A deterministic draw for key, so a run's random choices are the same wherever they are made."""
    return int.from_bytes(hashlib.blake2b(key.encode(), digest_size=8).digest(), "big") / 2**64 < rate


@dataclass(frozen=True)
class Selection:
    """What one pull request run executes: EEST tests by (test function, fork) and NUnit test projects."""

    run: str
    tests: frozenset[str]
    forks: frozenset[str]
    projects: frozenset[str]

    def keeps_test(self, name: str, fork: str) -> bool:
        return name in self.tests or fork in self.forks or sampled(f"{self.run}|{name}|{fork}")

    def keeps_project(self, project: str) -> bool:
        return project in self.projects or sampled(f"{self.run}|{project}")


class Selector:
    """Decides which tests a pull request runs, from the failures that past runs recorded.

    An EEST test runs when it failed before together with a changed project, or when a changed project broke at
    least FORK_LINK_MIN_TESTS tests of its fork in the last FORK_LINK_WINDOW (a fork under active development
    breaks in new places). An NUnit test project runs when it or a project it references directly changed, or
    when it failed before together with a changed project. Anything else runs when it falls in a deterministic
    SAMPLE_RATE sample. Harness and build-wide changes, and a HOLDOUT_RATE share of runs, run everything: the
    holdout runs keep the history unbiased by the selection itself.

    Runs that break more than MASS_TESTS EEST tests or MASS_PROJECTS test projects teach no links: they would
    tie the changed projects to everything, and the direct references and the sample catch such changes."""

    MASS_TESTS = 500
    MASS_PROJECTS = 20
    FORK_LINK_WINDOW = timedelta(days=14)
    FORK_LINK_MIN_TESTS = 5
    HOLDOUT_RATE = 0.05

    def __init__(self):
        self.test_links: dict[str, set[str]] = defaultdict(set)
        self.project_links: dict[str, set[str]] = defaultdict(set)
        # (project, fork) -> [(time, test)] for the tests of a fork that broke together with a project.
        self.fork_links: dict[tuple[str, str], list[tuple[datetime, str]]] = defaultdict(list)

    def decide(self, run: int | str, at: datetime, changed: set[str], direct_tests: set[str]) -> Selection | None:
        """What the run executes, or None when it runs everything."""
        if ALL in changed or changed & HARNESS or sampled(f"holdout|{run}", self.HOLDOUT_RATE):
            return None
        return Selection(
            run=str(run),
            tests=frozenset(self._linked(self.test_links, changed)),
            forks=frozenset(self._linked_forks(at, changed)),
            projects=frozenset(direct_tests | self._linked(self.project_links, changed)),
        )

    def observe(self, at: datetime, changed: set[str], tests: set[tuple[str, str]], projects: set[str]) -> None:
        changed = changed - {ALL}
        if tests and len({name for name, _ in tests}) <= self.MASS_TESTS:
            for project in changed:
                for name, fork in tests:
                    self.test_links[project].add(name)
                    self.fork_links[(project, fork)].append((at, name))
        if projects and len(projects) <= self.MASS_PROJECTS:
            for project in changed:
                self.project_links[project] |= projects

    @staticmethod
    def _linked(links: dict[str, set[str]], changed: set[str]) -> set[str]:
        return set().union(*(links.get(project, set()) for project in changed))

    def _linked_forks(self, at: datetime, changed: set[str]) -> set[str]:
        since = at - self.FORK_LINK_WINDOW
        return {
            fork for (project, fork), events in self.fork_links.items()
            if project in changed and len({name for time, name in events if time >= since}) >= self.FORK_LINK_MIN_TESTS
        }

    def to_json(self) -> dict:
        return {
            "test_links": {project: sorted(names) for project, names in self.test_links.items()},
            "project_links": {project: sorted(names) for project, names in self.project_links.items()},
            "fork_links": [
                [project, fork, [[time.isoformat(), name] for time, name in events]]
                for (project, fork), events in self.fork_links.items()
            ],
        }

    @classmethod
    def from_json(cls, data: dict) -> Selector:
        selector = cls()
        for project, names in data["test_links"].items():
            selector.test_links[project] = set(names)
        for project, names in data["project_links"].items():
            selector.project_links[project] = set(names)
        for project, fork, events in data["fork_links"]:
            selector.fork_links[(project, fork)] = [(datetime.fromisoformat(time), name) for time, name in events]
        return selector

    @classmethod
    def trained(cls, entries: Iterable[Entry], projects: Projects) -> Selector:
        selector = cls()
        for entry in entries:
            selector.observe(entry.at, projects.changed(entry.files), entry.eest_failures(), entry.failed_projects())
        return selector



# ------------------------------------------------------------------------------------------------- Inventory

class Inventory:
    """Cost in runner seconds of every EEST unit (lane 'type|setup', test function, fork) and every NUnit test
    project, from one full run."""

    def __init__(self, eest: dict[str, dict[str, dict[str, float]]], projects: dict[str, float]):
        self.eest = eest
        self.projects = projects
        self._test_costs = self._costs_by_test()

    @classmethod
    def load(cls, path: Path) -> Inventory:
        data = read_json(path)
        return cls(data["eest"], data["projects"])

    def save(self, path: Path) -> None:
        write_json(path, {"eest": self.eest, "projects": self.projects})

    def expected_eest_share(self, selection: Selection) -> float:
        """Share of the EEST cost the selection runs, counting the random sample by its expected share instead of
        drawing it for every test: the replay asks this for thousands of runs."""
        total = sum(self._test_costs.values())
        linked = sum(
            cost for (name, fork), cost in self._test_costs.items()
            if name in selection.tests or fork in selection.forks
        )
        return (linked + SAMPLE_RATE * (total - linked)) / total

    def project_share(self, selection: Selection) -> float:
        total = sum(self.projects.values())
        return sum(cost for project, cost in self.projects.items() if selection.keeps_project(project)) / total

    def lanes(self, selection: Selection) -> dict[str, LaneSelection]:
        lanes: dict[str, LaneSelection] = defaultdict(LaneSelection)
        for lane, tests in self.eest.items():
            for name, forks in tests.items():
                for fork, cost in forks.items():
                    lanes[lane].add(name, fork, cost, selection.keeps_test(name, fork))
        return lanes

    def _costs_by_test(self) -> dict[tuple[str, str], float]:
        costs: dict[tuple[str, str], float] = defaultdict(float)
        for tests in self.eest.values():
            for name, forks in tests.items():
                for fork, cost in forks.items():
                    costs[(name, fork)] += cost
        return costs


@dataclass
class LaneSelection:
    cost: float = 0.0
    picked: float = 0.0
    forks: dict[str, list[str]] = field(default_factory=lambda: defaultdict(list))

    def add(self, name: str, fork: str, cost: float, kept: bool) -> None:
        self.cost += cost
        if kept:
            self.picked += cost
            self.forks[name].append(fork)

    def filter_lines(self) -> list[str]:
        """nethtest --filterFile lines; each matches the test name prefix 'test_x[fork_Y-' or 'test_x[fork_Y]'."""
        return [
            rf"{re.escape(name)}\[fork_(?:{'|'.join(re.escape(fork) for fork in sorted(forks))})[-\]]"
            for name, forks in sorted(self.forks.items())
        ]


def shrink_nethtest_matrix(matrix: dict, lanes: dict[str, LaneSelection]) -> dict:
    """Keeps, per EEST lane, the share of its chunks that matches the share of its cost selected; lanes without
    a selected test go. Lanes the inventory does not know are left whole."""
    by_lane: dict[str, list[dict]] = defaultdict(list)
    for entry in matrix["include"]:
        by_lane[f"{entry['test_type']}|{entry['test_setup']}"].append(entry)
    include = []
    for lane, entries in by_lane.items():
        if lane not in lanes:
            include.extend(entries)
            continue
        chunks = math.ceil(len(entries) * lanes[lane].picked / lanes[lane].cost) if lanes[lane].picked else 0
        include.extend({**entry, "chunk": f"{i}of{chunks}"} for i, entry in enumerate(entries[:chunks], 1))
    # GitHub rejects an empty matrix; running everything is the safe answer.
    return {"include": include} if include else matrix


# Matrix keys whose values name test projects: 'project' strings, 'test' objects, and include/exclude entries.
PROJECT_KEYS = ("project", "test", "include", "exclude")


def filter_test_matrix(matrix: dict, keeps_project: Callable[[str], bool]) -> dict | None:
    """The NUnit matrix with only the selected test projects, or None when none remains. Every include entry of
    these matrices adds its own combination, so filtering by project keeps GitHub's expansion intact."""
    filtered = {
        key: [value for value in values if key not in PROJECT_KEYS or keeps(project_of(value), keeps_project)]
        for key, values in matrix.items()
    }
    if all(values for key, values in filtered.items() if key not in ("include", "exclude")):
        return filtered
    # Without the product, an include entry that names no project would start a job of its own.
    include = [entry for entry in filtered.get("include", []) if project_of(entry)]
    return {"include": include} if include else None


def keeps(project: str | None, keeps_project: Callable[[str], bool]) -> bool:
    return project is None or keeps_project(project)


def project_of(value: str | dict) -> str | None:
    return value if isinstance(value, str) else value.get("project") or value.get("test", {}).get("project")


# -------------------------------------------------------------------------------------------------- Commands

COLLECT_OVERLAP = timedelta(hours=6)
BACKFILL_STEP = timedelta(days=3)
RETENTION = timedelta(days=60)
COLLECT_WORKERS = 4
WARMUP = timedelta(days=7)


def collect(args: argparse.Namespace) -> None:
    """Appends the runs of the last COLLECT_OVERLAP before the newest entry, and grows a young journal back
    BACKFILL_STEP per call until it spans RETENTION. Runs that cannot be read are left for a later call."""
    github = GitHub(args.repo)
    git = Git(github)
    journal = read_journal(args.journal)
    known = {entry.run for entry in journal}
    runs = [run for run in collect_windows(github, journal) if is_collectable(run) and run["id"] not in known]
    with ThreadPoolExecutor(max_workers=COLLECT_WORKERS) as pool:
        journal.extend(entry for entry in pool.map(lambda run: read_run(github, git, run), runs) if entry)
    cutoff = datetime.now(timezone.utc) - RETENTION
    write_journal(args.journal, sorted((e for e in journal if e.at >= cutoff), key=lambda e: e.at))
    print(f"collected {len(runs)} runs")


def collect_windows(github: GitHub, journal: list[Entry]) -> list[dict]:
    now = datetime.now(timezone.utc)
    if not journal:
        return github.runs(now - BACKFILL_STEP, now)
    newest, oldest = journal[-1].at, journal[0].at
    runs = github.runs(newest - COLLECT_OVERLAP, now)
    if oldest > now - RETENTION:
        runs += github.runs(max(now - RETENTION, oldest - BACKFILL_STEP), oldest)
    return runs


def is_collectable(run: dict) -> bool:
    return run["conclusion"] in ("success", "failure") and run["event"] in ("pull_request", "push")


def read_run(github: GitHub, git: Git, run: dict) -> Entry | None:
    try:
        failed = None
        if run["conclusion"] == "failure":
            jobs = [job for job in github.failed_jobs(run["id"]) if unit_of(job["name"])]
            failed = classify(jobs, lambda job: github.text(f"repos/{github.repo}/actions/jobs/{job['id']}/logs"))
        return Entry(
            run=run["id"], at=datetime.fromisoformat(run["created_at"]), event=run["event"],
            branch=run["head_branch"], files=git.changed_files(run), failed=failed,
            selected=bool(SELECTION_ARTIFACTS & github.artifact_names(run["id"])),
        )
    except GitHubError as error:
        print(f"skipping run {run['id']}: {error}", file=sys.stderr)
        return None


def inventory(args: argparse.Namespace) -> None:
    """Costs from one full run of nethermind-tests.yml: EEST from its result artifacts and step times, NUnit
    test projects from their job times."""
    github = GitHub(args.repo)
    jobs = github.jobs(args.run)
    with tempfile.TemporaryDirectory() as directory:
        subprocess.run(
            ["gh", "run", "download", str(args.run), "-R", github.repo, "-p", "nethtest-results-*", "-D", directory],
            check=True, capture_output=True,
        )
        cases = nethtest_cases(Path(directory))
    Inventory(eest_costs(cases, nethtest_seconds(jobs)), project_seconds(jobs)).save(args.out)


def nethtest_cases(directory: Path) -> dict[str, dict[str, dict[str, int]]]:
    cases: dict[str, dict[str, dict[str, int]]] = defaultdict(lambda: defaultdict(lambda: defaultdict(int)))
    for results in directory.glob("*/nethtest-results.json"):
        # Artifact names are nethtest-results-<type>-<setup>-<chunk>.
        lane = "|".join(results.parent.name.removeprefix("nethtest-results-").split("-")[:2])
        for case in json.loads(results.read_text()):
            name, fork = case_key(case)
            cases[lane][name][fork] += 1
    return cases


def case_key(case: dict) -> tuple[str, str]:
    match = NETHTEST_CASE.match(case["name"])
    return (match.group(1), match.group(2)) if match else (case["name"].split("[")[0], case.get("fork", ""))


def nethtest_seconds(jobs: Iterable[dict]) -> dict[str, float]:
    seconds: dict[str, float] = defaultdict(float)
    for job in jobs:
        lane = nethtest_lane(job["name"])
        step = next((s for s in job.get("steps", []) if s["name"] == "Run nethtest and parse results"), None)
        if lane and step and step.get("completed_at"):
            seconds[lane] += duration(step)
    return seconds


def project_seconds(jobs: Iterable[dict]) -> dict[str, float]:
    seconds: dict[str, float] = defaultdict(float)
    for job in jobs:
        unit = unit_of(job["name"])
        if unit not in (None, "EEST", "Hive") and job.get("completed_at"):
            seconds[unit] += duration(job)
    return seconds


def duration(span: dict) -> float:
    return (datetime.fromisoformat(span["completed_at"]) - datetime.fromisoformat(span["started_at"])).total_seconds()


def eest_costs(cases: dict, seconds: dict[str, float]) -> dict:
    """Spreads each lane's measured step time evenly over its cases."""
    costs = {}
    for lane, tests in cases.items():
        per_case = seconds[lane] / sum(count for forks in tests.values() for count in forks.values())
        costs[lane] = {name: {fork: count * per_case for fork, count in forks.items()} for name, forks in tests.items()}
    return costs


def model(args: argparse.Namespace) -> None:
    journal = read_journal(args.journal)
    selector = Selector.trained(journal, Projects.of_repository())
    write_json(args.out, {
        "built_at": datetime.now(timezone.utc).isoformat(),
        "history_from": journal[0].at.isoformat() if journal else None,
        **selector.to_json(),
    })
    print(f"model from {len(journal)} runs")


def select_eest(args: argparse.Namespace) -> None:
    """Writes one nethtest --filterFile per EEST lane and the reduced nethtest matrix for one pull request."""
    matrix = read_json(args.matrix)
    selection, reason = pull_request_selection(args)
    if selection is None:
        write_json(args.out_matrix, matrix)
        print(f"running every EEST test: {reason}")
        return
    lanes = Inventory.load(args.inventory).lanes(selection)
    write_filters(args.filters, lanes)
    reduced = shrink_nethtest_matrix(matrix, lanes)
    write_json(args.out_matrix, reduced)
    share = sum(lane.picked for lane in lanes.values()) / sum(lane.cost for lane in lanes.values())
    print(f"selected {100 * share:.1f}% of the EEST cost: {len(reduced['include'])} of {len(matrix['include'])} jobs")


def select_projects(args: argparse.Namespace) -> None:
    """Writes the NUnit matrices of one workflow as a 'matrices' step output: each job's matrix with only the
    selected test projects, or null when the job has nothing to run. Every matrix stays whole outside pull
    requests and whenever the selection cannot be trusted."""
    matrices = read_json(args.matrices)[args.workflow]
    projects = all_projects(matrices)
    selection, reason = pull_request_selection(args) if args.event == "pull_request" else (None, "not a pull request")
    if selection is not None:
        matrices = {job: filter_test_matrix(matrix, selection.keeps_project) for job, matrix in matrices.items()}
        kept = sorted(project for project in projects if selection.keeps_project(project))
        print(f"running {len(kept)} test projects: {', '.join(kept)}")
    else:
        print(f"running every test project: {reason}")
    with open(args.output, "a") as output:
        output.write(f"matrices={json.dumps(matrices)}\n")
        output.write(f"any={str(any(matrix is not None for matrix in matrices.values())).lower()}\n")


def all_projects(matrices: dict) -> set[str]:
    return {
        project for matrix in matrices.values() for key in PROJECT_KEYS
        for value in matrix.get(key, []) if (project := project_of(value))
    }


def pull_request_selection(args: argparse.Namespace) -> tuple[Selection | None, str]:
    problem = model_problem(args.model, args.min_history_days, args.max_model_age_hours)
    if problem:
        return None, problem
    projects = Projects.of_repository()
    changed = projects.changed(args.changed_files.read_text().split())
    selector = Selector.from_json(read_json(args.model))
    selection = selector.decide(args.run, datetime.now(timezone.utc), changed, projects.direct_tests(changed))
    return selection, "the change touches the build or the test harness, or the run is a holdout"


def model_problem(path: Path, min_history_days: int, max_age_hours: int) -> str | None:
    """Why the model cannot be trusted, or None."""
    if not path.is_file():
        return "no selection model"
    data = read_json(path)
    now = datetime.now(timezone.utc)
    if now - datetime.fromisoformat(data["built_at"]) > timedelta(hours=max_age_hours):
        return f"the model was built at {data['built_at']}"
    history_from = data["history_from"]
    if history_from is None or now - datetime.fromisoformat(history_from) < timedelta(days=min_history_days):
        return f"the model holds history from {history_from} only"
    return None


LANE = re.compile(r"\w+\|\w+")


def write_filters(directory: Path, lanes: dict[str, LaneSelection]) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for lane, selection in lanes.items():
        if not LANE.fullmatch(lane):
            raise ValueError(f"invalid lane {lane!r}")
        if selection.forks:
            (directory / f"{lane.replace('|', '-')}.txt").write_text("\n".join(selection.filter_lines()) + "\n")


def replay(args: argparse.Namespace) -> None:
    journal = read_journal(args.journal)
    if not journal:
        print("the journal is empty")
        return
    report = Replay(Inventory.load(args.inventory), Projects.of_repository()).run(journal, journal[0].at + WARMUP)
    args.report.write_text(report.markdown(args.repo, journal))


@dataclass
class Suite:
    """Replay results of one kind of test: EEST tests or NUnit test projects."""

    selected_cost: float = 0.0
    failing: list[Entry] = field(default_factory=list)
    hidden: list[tuple[Entry, list[str]]] = field(default_factory=list)

    def measure(self, entry: Entry, share: float, failures: list[str], hidden: bool) -> None:
        self.selected_cost += share
        if failures:
            self.failing.append(entry)
            if hidden:
                self.hidden.append((entry, failures))

    def row(self, label: str, measured: int) -> str:
        share = 100 * self.selected_cost / measured if measured else 0.0
        branches = len({entry.branch for entry in self.failing})
        return f"| {label} | {share:.1f}% | {len(self.failing)} ({branches} branches) | {len(self.hidden)} |"


@dataclass
class ReplayReport:
    measured: int = 0
    eest: Suite = field(default_factory=Suite)
    nunit: Suite = field(default_factory=Suite)

    def markdown(self, repo: str, journal: list[Entry]) -> str:
        lines = [
            "## Test selection replay",
            "",
            f"{len(journal)} runs from {journal[0].at:%Y-%m-%d} to {journal[-1].at:%Y-%m-%d}, {self.measured} pull "
            f"request runs measured. The first {WARMUP.days} days only build history. Runs that used the selection "
            "are not measured: they could only fail in the tests they ran.",
            "",
            "| Tests | Cost the selector would run | Runs with failures | Runs it would report green |",
            "|---|---|---|---|",
            self.eest.row("EEST", self.measured),
            self.nunit.row("NUnit test projects", self.measured),
        ]
        hidden = self.eest.hidden + self.nunit.hidden
        if hidden:
            lines += ["", "| Run | Branch | Failed tests |", "|---|---|---|"]
            lines += [hidden_row(repo, entry, failures) for entry, failures in hidden]
        return "\n".join(lines) + "\n"


def hidden_row(repo: str, entry: Entry, failures: list[str]) -> str:
    link = f"[{entry.run}](https://github.com/{repo}/actions/runs/{entry.run})"
    tests = ", ".join(failures[:3]) + (" ..." if len(failures) > 3 else "")
    return f"| {link} | {markdown_cell(entry.branch)} | {markdown_cell(tests)} |"


def markdown_cell(text: str) -> str:
    """Branch names and test names come from pull requests; keep them from breaking or injecting markup."""
    return text.replace("|", "\\|").replace("`", "'").replace("<", "&lt;")


class Replay:
    """Decides for every recorded pull request run what the selector would have run with only the history
    before it, and which of its failures it would have hidden."""

    def __init__(self, inventory: Inventory, projects: Projects):
        self.inventory = inventory
        self.projects = projects

    def run(self, journal: list[Entry], measure_from: datetime) -> ReplayReport:
        report = ReplayReport()
        selector = Selector()
        for entry in journal:
            changed = self.projects.changed(entry.files)
            tests, failed_projects = entry.eest_failures(), entry.failed_projects()
            if entry.event == "pull_request" and not entry.selected and entry.at >= measure_from:
                selection = selector.decide(entry.run, entry.at, changed, self.projects.direct_tests(changed))
                self._measure(report, selection, entry, tests, failed_projects)
            selector.observe(entry.at, changed, tests, failed_projects)
        return report

    def _measure(self, report: ReplayReport, selection: Selection | None, entry: Entry,
                 tests: set[tuple[str, str]], failed_projects: set[str]) -> None:
        report.measured += 1
        everything = selection is None
        report.eest.measure(
            entry, 1.0 if everything else self.inventory.expected_eest_share(selection),
            sorted(f"{name} ({fork})" for name, fork in tests),
            not everything and not any(selection.keeps_test(name, fork) for name, fork in tests),
        )
        report.nunit.measure(
            entry, 1.0 if everything else self.inventory.project_share(selection), sorted(failed_projects),
            not everything and not any(selection.keeps_project(project) for project in failed_projects),
        )


# ------------------------------------------------------------------------------------------------------ CLI

def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "NethermindEth/nethermind"))
    commands = parser.add_subparsers(dest="command", required=True)

    command = commands.add_parser("collect")
    command.add_argument("--journal", type=Path, required=True)
    command.set_defaults(handler=collect)

    command = commands.add_parser("inventory")
    command.add_argument("--run", type=int, required=True, help="a full run of nethermind-tests.yml")
    command.add_argument("--out", type=Path, required=True)
    command.set_defaults(handler=inventory)

    command = commands.add_parser("model")
    command.add_argument("--journal", type=Path, required=True)
    command.add_argument("--out", type=Path, required=True)
    command.set_defaults(handler=model)

    command = commands.add_parser("select-eest")
    add_selection_arguments(command)
    command.add_argument("--inventory", type=Path, required=True)
    command.add_argument("--matrix", type=Path, required=True, help="the nethtest matrix, {include: [...]}")
    command.add_argument("--out-matrix", type=Path, required=True)
    command.add_argument("--filters", type=Path, required=True, help="directory for one --filterFile per lane")
    command.set_defaults(handler=select_eest)

    command = commands.add_parser("select-projects")
    add_selection_arguments(command)
    command.add_argument("--matrices", type=Path, required=True, help="scripts/ci/test-matrices.json")
    command.add_argument("--workflow", required=True, help="the workflow whose matrices to write")
    command.add_argument("--event", required=True, help="github.event_name; only pull requests are selected")
    command.add_argument("--output", type=Path, required=True, help="the step output file, $GITHUB_OUTPUT")
    command.set_defaults(handler=select_projects)

    command = commands.add_parser("replay")
    command.add_argument("--journal", type=Path, required=True)
    command.add_argument("--inventory", type=Path, required=True)
    command.add_argument("--report", type=Path, required=True)
    command.set_defaults(handler=replay)

    args = parser.parse_args()
    args.handler(args)


def add_selection_arguments(command: argparse.ArgumentParser) -> None:
    command.add_argument("--model", type=Path, required=True)
    command.add_argument("--changed-files", type=Path, required=True, help="one changed path per line")
    command.add_argument("--run", required=True, help="the workflow run id; seeds the sample and the holdout")
    command.add_argument("--min-history-days", type=int, default=28)
    command.add_argument("--max-model-age-hours", type=int, default=24)


if __name__ == "__main__":
    main()
