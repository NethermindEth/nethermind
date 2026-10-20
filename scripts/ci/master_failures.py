#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Turns a red master test run into one issue assigned to the author of the pull request that caused it.

A pull request runs only the tests its change can affect (select_tests.py), so a regression can reach master.
When a master run of a test workflow fails, this finds the test failures that the environment does not
explain, the pull requests merged since the last green run, and the one whose change the failing tests depend
on. It opens one issue per workflow, or comments on the open one, and closes it when master is green again.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path

import select_tests as st

LABEL = "master-broken"
MAX_LISTED_TESTS = 30


@dataclass(frozen=True)
class Merge:
    sha: str
    number: int | None
    author: str | None
    files: tuple[str, ...]


@dataclass(frozen=True)
class Suspect:
    merge: Merge
    reason: str


# ----------------------------------------------------------------------------------------------- Attribution

def regressions(failed: st.Failures, journal: list[st.Entry], since: datetime) -> list[str]:
    """Test failures of a master run that the flakiness before the merges does not explain. Failures on other
    branches after the merges are left in: a broken master breaks the pull requests based on it."""
    flaky_from = since - st.ENVIRONMENT_WINDOW
    branches: dict[str, set[str]] = {}
    for entry in journal:
        if entry.failed and entry.branch != "master" and flaky_from <= entry.at <= since:
            for test in entry.failed.tests:
                branches.setdefault(test, set()).add(entry.branch)
    return sorted(
        test for test in failed.tests
        if not test.startswith("Hive|") and len(branches.get(test, ())) < st.ENVIRONMENT_BRANCHES
    )


def suspects(merges: list[Merge], tests: list[str], projects: st.Projects, selector: st.Selector) -> list[Suspect]:
    """The merges with the strongest link to the failures (see REASONS). A single merge is the suspect on its
    own."""
    failing_projects = {test.split("|", 1)[0] for test in tests if not test.startswith("EEST|")}
    failing_eest = {test.split("|")[3] for test in tests if test.startswith("EEST|")}
    ranked = [
        (order, merge) for merge in merges
        if (order := rank(projects.changed(merge.files), failing_projects, failing_eest, projects, selector))
        is not None
    ]
    if not ranked:
        return [Suspect(merges[0], "the only merge since the last green run")] if len(merges) == 1 else []
    best = min(order for order, _ in ranked)
    return [Suspect(merge, REASONS[order]) for order, merge in ranked if order == best]


REASONS = (
    "changes the failing tests or a project they reference",
    "the history links its projects to the failures",
    "changes a project the failing tests depend on",
    "changes the whole build",
)


def rank(changed: set[str], failing_projects: set[str], failing_eest: set[str], projects: st.Projects,
         selector: st.Selector) -> int | None:
    """Index into REASONS of the strongest link from the changed projects to the failures, or None."""
    if st.ALL in changed:
        return 3
    if failing_projects & projects.direct_tests(changed) or failing_eest and changed & st.HARNESS:
        return 0
    linked_projects, linked_tests = selector.linked(changed)
    if failing_projects & linked_projects or failing_eest & linked_tests:
        return 1
    depended = set().union(*(projects.dependencies(f"{st.SRC}/{project}") for project in failing_projects))
    # EEST runs the whole client, so any changed project can break it.
    if changed & depended or failing_eest and changed:
        return 2
    return None


# ------------------------------------------------------------------------------------------------------ Issue

def marker(workflow: str) -> str:
    return f"<!-- {LABEL}: {workflow} -->"


def issue_body(workflow: str, run: dict, since_sha: str, tests: list[str], found: list[Suspect]) -> str:
    listed = "\n".join(f"- `{test}`" for test in tests[:MAX_LISTED_TESTS])
    more = f"\n- and {len(tests) - MAX_LISTED_TESTS} more" if len(tests) > MAX_LISTED_TESTS else ""
    blame = "\n".join(
        f"- #{s.merge.number} by @{s.merge.author}: {s.reason}" if s.merge.number else f"- {s.merge.sha}: {s.reason}"
        for s in found
    ) or "- No merge since the last green run explains the failures."
    return f"""{marker(workflow)}
[{workflow}]({run['html_url']}) fails on master at {run['head_sha'][:10]}. It was green at {since_sha[:10]}.

Pull requests run only the tests their change can affect, so this regression was not run before the merge.

**Failing tests**
{listed}{more}

**Likely cause**
{blame}

Please fix or revert. This issue closes when the workflow is green on master again.
"""


class Issues:
    """The repository's issues through gh."""

    def __init__(self, repo: str):
        self.repo = repo

    def open_for(self, workflow: str) -> int | None:
        found = json.loads(self._gh("issue", "list", "--label", LABEL, "--state", "open", "--limit", "100",
                                    "--json", "number,body"))
        return next((issue["number"] for issue in found if marker(workflow) in issue["body"]), None)

    def create(self, title: str, body: str, assignees: list[str]) -> None:
        self._gh("label", "create", LABEL, "--color", "B60205", "--description", "A test fails on master", "--force")
        arguments = ["issue", "create", "--title", title, "--body", body, "--label", LABEL]
        try:
            self._gh(*arguments, *(f"--assignee={author}" for author in assignees))
        except subprocess.CalledProcessError:
            self._gh(*arguments)  # An author that cannot be assigned, such as a bot, must not lose the issue.

    def comment(self, number: int, body: str) -> None:
        self._gh("issue", "comment", str(number), "--body", body)

    def close(self, number: int, body: str) -> None:
        self._gh("issue", "close", str(number), "--comment", body)

    def _gh(self, *arguments: str) -> str:
        return subprocess.run(["gh", *arguments, "-R", self.repo], check=True, capture_output=True, text=True).stdout


# --------------------------------------------------------------------------------------------------- Command

def report(args: argparse.Namespace) -> None:
    github, issues = st.GitHub(args.repo), Issues(args.repo)
    run = github.object(f"repos/{args.repo}/actions/runs/{args.run}")
    workflow = run["name"]
    open_issue = issues.open_for(workflow)
    if run["conclusion"] == "success":
        if open_issue:
            issues.close(open_issue, f"[{workflow}]({run['html_url']}) is green on master at {run['head_sha'][:10]}.")
        return
    green = last_green(github, args.repo, run)
    if green is None:
        print("no green master run to compare with")
        return
    failed = st.read_failures(github, run) or st.Failures()
    tests = regressions(failed, st.read_journal(args.journal), datetime.fromisoformat(green["created_at"]))
    if not tests:
        print("no failure that a change caused: infra, Hive or tests that were already flaky")
        return
    if open_issue:
        issues.comment(open_issue, f"Still failing in [{run['id']}]({run['html_url']}) at {run['head_sha'][:10]}: "
                                   f"{len(tests)} tests.")
        return
    merges = merged_between(github, args.repo, green["head_sha"], run["head_sha"])
    selector = st.Selector.from_json(st.read_json(args.model)) if args.model.is_file() else st.Selector()
    found = suspects(merges, tests, st.Projects.of_repository(), selector)
    more = f" and {len(tests) - 1} more" if len(tests) > 1 else ""
    title = f"{workflow} fails on master: {tests[0].replace('|', ' ')}{more}"
    authors = sorted({s.merge.author for s in found if s.merge.author})
    issues.create(title, issue_body(workflow, run, green["head_sha"], tests, found), authors)
    print(f"opened an issue for {len(tests)} tests, assigned to {', '.join(authors) or 'nobody'}")


def last_green(github: st.GitHub, repo: str, run: dict) -> dict | None:
    path = (f"repos/{repo}/actions/workflows/{run['workflow_id']}/runs?branch=master&event=push&status=success"
            f"&per_page=20")
    earlier = [r for r in github.object(path)["workflow_runs"] if r["created_at"] < run["created_at"]]
    return earlier[0] if earlier else None


def merged_between(github: st.GitHub, repo: str, base: str, head: str) -> list[Merge]:
    commits = github.object(f"repos/{repo}/compare/{base}...{head}")["commits"]
    merges = []
    for commit in commits:
        sha = commit["sha"]
        pulls = github.object(f"repos/{repo}/commits/{sha}/pulls")
        files = st.changed_paths([f for page in github.pages(f"repos/{repo}/commits/{sha}") for f in page["files"]])
        pull = pulls[0] if pulls else None
        merges.append(Merge(
            sha=sha, number=pull["number"] if pull else None, author=pull["user"]["login"] if pull else None,
            files=tuple(files),
        ))
    return merges


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", "NethermindEth/nethermind"))
    parser.add_argument("--run", type=int, required=True, help="a completed master run of a test workflow")
    parser.add_argument("--journal", type=Path, required=True)
    parser.add_argument("--model", type=Path, required=True)
    report(parser.parse_args())


if __name__ == "__main__":
    main()
