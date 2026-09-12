#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import os
import shutil
import subprocess
from pathlib import Path

import pytest
import yaml


WORKFLOW = (
    Path(__file__).parents[2]
    / ".github"
    / "workflows"
    / "run-expb-reproducible-benchmarks.yml"
)


def quality_gate_scripts() -> list[str]:
    workflow = yaml.safe_load(WORKFLOW.read_text())
    scripts = []
    for job in workflow["jobs"].values():
        for step in job.get("steps", []):
            if step.get("name") == "Enforce run quality gates":
                scripts.append(step["run"])
    assert len(scripts) == 2
    return scripts


@pytest.fixture(scope="module")
def bash_executable():
    bash = shutil.which("bash")
    if os.name == "nt":
        git_bash = Path(os.environ.get("ProgramFiles", "C:\\Program Files")) / "Git" / "bin" / "bash.exe"
        if git_bash.exists():
            bash = str(git_bash)
    if bash is None:
        pytest.skip("quality-gate shell tests require bash")
    return bash


@pytest.mark.parametrize(
    ("outcome", "exception_found", "invalid_block_found", "should_fail", "message"),
    [
        ("success", "false", "false", False, ""),
        ("success", "true", "false", True, "Exceptions were detected"),
        ("success", "false", "true", True, "Invalid block lines were detected"),
        ("failure", "false", "false", True, "did not finish successfully"),
    ],
)
def test_quality_gates_execute_all_failure_cases(
    bash_executable,
    outcome,
    exception_found,
    invalid_block_found,
    should_fail,
    message,
    tmp_path,
):
    exception_file = tmp_path / "exceptions.log"
    invalid_block_file = tmp_path / "invalid-blocks.log"
    exception_file.write_text("Exception fixture\n")
    invalid_block_file.write_text("Invalid Block fixture\n")
    environment = os.environ.copy()
    environment.update(
        {
            "RUN_EXPB_OUTCOME": outcome,
            "EXCEPTION_FOUND": exception_found,
            "EXCEPTION_LINES_FILE": str(exception_file),
            "INVALID_BLOCK_FOUND": invalid_block_found,
            "INVALID_BLOCK_LINES_FILE": str(invalid_block_file),
            "TAG": "fixture",
            "RUN": "1",
        }
    )

    for script in quality_gate_scripts():
        result = subprocess.run(
            [bash_executable, "-euo", "pipefail", "-c", script],
            env=environment,
            capture_output=True,
            text=True,
        )
        assert (result.returncode != 0) is should_fail, result.stdout + result.stderr
        if message:
            assert message in result.stdout
        if not should_fail:
            assert "Run quality gates failed" not in result.stdout
