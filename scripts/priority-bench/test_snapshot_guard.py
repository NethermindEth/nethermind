#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import os
import re
import shlex
import shutil
import subprocess
import textwrap
from pathlib import Path

import pytest


WORKFLOW = (
    Path(__file__).parents[2]
    / ".github"
    / "workflows"
    / "run-expb-reproducible-benchmarks.yml"
)
START_MARKER = "# BEGIN EXPB_ARM_SNAPSHOT_CHECK"
END_MARKER = "# END EXPB_ARM_SNAPSHOT_CHECK"


def snapshot_guard_scripts() -> list[str]:
    workflow = WORKFLOW.read_text()
    scripts = []
    remaining = workflow
    while START_MARKER in remaining:
        _, remaining = remaining.split(START_MARKER, 1)
        body, remaining = remaining.split(END_MARKER, 1)
        scripts.append(textwrap.dedent(body))
    assert len(scripts) == 2
    return scripts


def shell_path(path: str) -> str:
    cygpath = shutil.which("cygpath")
    if cygpath is None:
        git_cygpath = Path("C:/Program Files/Git/usr/bin/cygpath.exe")
        cygpath = str(git_cygpath) if git_cygpath.exists() else None
    if cygpath is None:
        return path
    return subprocess.run(
        [cygpath, "-u", path], capture_output=True, text=True, check=True
    ).stdout.strip()


def run_guard(script: str, snapshot_source: str, runner_label: str, bash_path: str):
    environment = os.environ.copy()
    command = (
        f"export RUNNER_LABEL={shlex.quote(runner_label)} "
        f"snapshot_source={shlex.quote(shell_path(snapshot_source))}\n"
        f"{script}\necho benchmark-started\n"
    )
    return subprocess.run(
        [bash_path, "-euo", "pipefail", "-c", command],
        env=environment,
        capture_output=True,
        text=True,
    )


@pytest.fixture(scope="module")
def bash_available():
    git_bash = Path("C:/Program Files/Git/bin/bash.exe")
    if git_bash.exists():
        return str(git_bash)
    bash = shutil.which("bash")
    if bash is None:
        pytest.skip("snapshot guard fixtures require Bash")
    return bash


def test_arm_snapshot_mappings_are_distinct():
    workflow = WORKFLOW.read_text()
    assert '.scenarios.[strenv(SK)].snapshot_source // "<missing>"' in workflow
    resolve_case = re.search(
        r'case "\$\{arch\}" in(?P<body>.*?)\n\s+esac', workflow, re.DOTALL
    )
    assert resolve_case is not None
    arm_case = re.search(
        r"\n\s+arm64\)(?P<body>.*?)\n\s+;;",
        resolve_case.group("body"),
        re.DOTALL,
    )
    amd_case = re.search(
        r"\n\s+amd64\)(?P<body>.*?)\n\s+;;",
        resolve_case.group("body"),
        re.DOTALL,
    )
    assert arm_case is not None and amd_case is not None

    path_pattern = r'flat_snapshot(?:_block)?_dir="([^"]+)"'
    arm_paths = re.findall(path_pattern, arm_case.group("body"))
    amd_paths = re.findall(path_pattern, amd_case.group("body"))
    assert arm_paths == [
        "/data/nethermind/nethermind-flat-snapshot",
        "/data/nethermind/nethermind-flat-25490000",
    ]
    assert amd_paths == [
        "/mnt/sda/nethermind-flat-snapshot",
        "/mnt/sda/nethermind-flat-25490000",
    ]
    assert arm_paths[0] != arm_paths[1]


@pytest.mark.parametrize("snapshot_name", ["legacy", "fusaka-25490000"])
def test_existing_arm_snapshot_passes_before_benchmark(
    snapshot_name, bash_available, tmp_path
):
    snapshot = tmp_path / snapshot_name
    snapshot.mkdir()

    for script in snapshot_guard_scripts():
        result = run_guard(
            script, str(snapshot), "reproducible-benchmarks-arm", bash_available
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert "EXPB ARM snapshot_source exists" in result.stdout
        assert "benchmark-started" in result.stdout


def test_missing_arm_snapshot_fails_before_benchmark(bash_available, tmp_path):
    missing = tmp_path / "missing-snapshot"

    for script in snapshot_guard_scripts():
        result = run_guard(
            script, str(missing), "reproducible-benchmarks-arm", bash_available
        )
        assert result.returncode != 0
        assert "ARM snapshot_source directory is missing" in (
            result.stdout + result.stderr
        )
        assert "benchmark-started" not in result.stdout


def test_amd64_keeps_snapshot_guard_disabled(bash_available, tmp_path):
    missing = tmp_path / "missing-snapshot"

    for script in snapshot_guard_scripts():
        result = run_guard(
            script, str(missing), "reproducible-benchmarks", bash_available
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert "benchmark-started" in result.stdout
