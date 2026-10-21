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
import tempfile
import time

TIMESTAMP = re.compile(r"^\d{4}-\d\d-\d\dT[\d:.]+Z ?")
ANSI = re.compile(r"\x1b\[[0-9;]*m")
RUNNING = re.compile(r"Running all tests in .*/bin/(?P<project>[^/]+)/release/")
FAILED = re.compile(r"^failed (?P<name>.+?) \((?P<duration>[\dhmsµn ]+)\)$")
RESULT = re.compile(r"^(passed|failed|skipped) .+ \([\dhmsµn ]+\)$|^Test run summary|^\s*(total|failed|succeeded|skipped|duration):")
PROJECT = re.compile(r"^Nethermind(\.[A-Za-z0-9]+)+\.Test$")
METHOD = re.compile(r"^[A-Za-z_][A-Za-z0-9_]*$")
NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)", re.MULTILINE)
CLASS = re.compile(r"^[ \t]*(?:(?:public|internal|private|protected|sealed|static|partial|abstract|file)\s+)*(?:record\s+)?class\s+(\w+)", re.MULTILINE)
PROTECTED_PATH = re.compile(r"^(\.github/|\.agents/|scripts/)|\.(props|targets|csproj)$|(^|/)(global\.json|nuget\.config|AGENTS\.md|CLAUDE\.md)$", re.IGNORECASE)
TRUSTED_ASSOCIATIONS = {"OWNER", "MEMBER", "COLLABORATOR"}
REVIEW_BOT = "claude"
REVIEW_THREADS = """
query($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      reviewThreads(first: 100) {
        nodes { isResolved path line comments(first: 50) { nodes { author { __typename login } authorAssociation body } } }
      }
      comments(last: 50) { nodes { author { __typename login } authorAssociation body } }
    }
  }
}
"""
EXCERPT_LINES = 40
EXCERPT_CHARS = 4000
SOURCE_ROOT = Path("src/Nethermind")
ZERO_TESTS_RAN = 8
MIN_VERIFY_RUNS = 50
MAX_VERIFY_RUNS = 1000
MIN_VERIFY_SECONDS = 3600
MAX_VERIFY_SECONDS = 5400
VERIFY_BUDGET_MARGIN = 1.5
CONFIDENCE_FACTOR = 3
SPINNER = "import os, sys\nparent = int(sys.argv[1])\nwhile os.getppid() == parent: pass"


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


def test_class(failure):
    trace = re.search(rf"\bat ([\w.]+)\.{re.escape(failure['method'])}\(", failure["excerpt"])
    if trace:
        return trace.group(1)
    if not failure.get("source"):
        return None
    text = Path(failure["source"]).read_text(encoding="utf-8", errors="replace")
    declaration = re.search(rf"\b{re.escape(failure['method'])}\s*\(", text)
    namespace = NAMESPACE.search(text)
    classes = CLASS.findall(text[:declaration.start()]) if declaration else []
    if not (namespace and classes):
        return None
    return f"{namespace.group(1)}.{classes[-1]}"


def test_filter(failure):
    name = f"Name={failure['method']}"
    owner = test_class(failure)
    return f"FullyQualifiedName~{owner}&{name}" if owner else name


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
            candidate = dict(failure, source=source, branch=branch)
            return dict(candidate, filter=test_filter(candidate)), None
    return None, "no new flaky test candidate"


def required_runs(runs, failures):
    if failures <= 0:
        return MAX_VERIFY_RUNS
    return max(MIN_VERIFY_RUNS, min(MAX_VERIFY_RUNS, math.ceil(CONFIDENCE_FACTOR * runs / failures)))


def verify_budget(baseline_runs, baseline_seconds, verify_runs):
    expected = math.ceil(baseline_seconds / max(baseline_runs, 1) * verify_runs * VERIFY_BUDGET_MARGIN)
    return max(MIN_VERIFY_SECONDS, min(MAX_VERIFY_SECONDS, expected))


def classify(runs, failures):
    if failures == 0:
        return "not-reproduced"
    if failures == runs:
        return "deterministic"
    return "flaky"


def start_cpu_load(processes):
    return [subprocess.Popen([sys.executable, "-c", SPINNER, str(os.getpid())]) for _ in range(processes)]


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
                partial = expired.stdout or b""
                code, output = -1, f"timed out after {run_timeout}s\n{partial.decode(errors='replace')}"
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


def blocked_paths(paths):
    return [path for path in paths if PROTECTED_PATH.search(path)]


def is_review_bot(comment):
    author = comment.get("author") or {}
    return author.get("__typename") == "Bot" and author.get("login") == REVIEW_BOT


def is_trusted(comment):
    return is_review_bot(comment) or comment.get("authorAssociation") in TRUSTED_ASSOCIATIONS


def trusted_feedback(pull_request):
    sections = []
    for thread in pull_request["reviewThreads"]["nodes"]:
        comments = [c for c in thread["comments"]["nodes"] if is_trusted(c)]
        if thread["isResolved"] or not comments:
            continue
        sections.append(f"### {thread['path']}:{thread['line']}\n\n" + "\n\n---\n\n".join(c["body"] for c in comments))
    comments = [c for c in pull_request["comments"]["nodes"] if is_trusted(c)]
    latest_summary = next((c for c in reversed(comments) if is_review_bot(c)), None)
    sections += [f"### PR comment\n\n{c['body']}" for c in comments if not is_review_bot(c) or c is latest_summary]
    return "\n\n".join(sections)


def fetch_feedback(repo, number):
    owner, name = repo.split("/")
    output = subprocess.run(["gh", "api", "graphql", "-f", f"query={REVIEW_THREADS}", "-f", f"owner={owner}",
                             "-f", f"name={name}", "-F", f"number={number}"],
                            check=True, capture_output=True, text=True).stdout
    return trusted_feedback(json.loads(output)["data"]["repository"]["pullRequest"])


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
    repeat_parser.add_argument("--out-dir", default=os.path.join(os.environ.get("RUNNER_TEMP", tempfile.gettempdir()), "flaky-repeat"))
    repeat_parser.add_argument("--load", type=int, default=max(1, (os.cpu_count() or 2) // 2))

    runs_parser = commands.add_parser("required-runs")
    runs_parser.add_argument("runs", type=int)
    runs_parser.add_argument("failures", type=int)
    runs_parser.add_argument("seconds", type=int)

    guard_parser = commands.add_parser("guard")
    guard_parser.add_argument("paths", nargs="*")

    feedback_parser = commands.add_parser("feedback")
    feedback_parser.add_argument("--repo", required=True)
    feedback_parser.add_argument("--pr", type=int, required=True)
    feedback_parser.add_argument("--out", required=True)

    args = parser.parse_args(argv)
    if args.command == "select":
        candidate, reason = select(args.log_dir, args.repo, args.label, args.daily_limit, args.lookback_days)
        if candidate is None:
            print(reason)
            write_outputs({"found": "false", "reason": reason})
            return 0
        print(json.dumps(candidate, indent=2))
        Path(args.out).write_text(json.dumps(candidate, indent=2), encoding="utf-8")
        write_outputs({key: candidate[key] for key in ("project", "method", "test", "source", "branch", "filter")} | {"found": "true"})
    elif args.command == "repeat":
        result = repeat(args.project, args.filter, args.runs, args.budget, args.stop_after, args.out_dir,
                        load=args.load)
        print(json.dumps(result))
        write_outputs(result)
    elif args.command == "guard":
        blocked = blocked_paths(args.paths)
        for path in blocked:
            print(f"::error::The fix may not change {path}")
        return 1 if blocked else 0
    elif args.command == "feedback":
        Path(args.out).write_text(fetch_feedback(args.repo, args.pr), encoding="utf-8")
    else:
        runs = required_runs(args.runs, args.failures)
        budget = verify_budget(args.runs, args.seconds, runs)
        print(json.dumps({"runs": runs, "budget": budget}))
        write_outputs({"runs": runs, "budget": budget})
    return 0


if __name__ == "__main__":
    sys.exit(main())
