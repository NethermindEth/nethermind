#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import os
import shutil
import subprocess
import textwrap
from pathlib import Path

import pytest
import yaml


WORKFLOW = (
    Path(__file__).parents[2]
    / ".github"
    / "workflows"
    / "run-expb-reproducible-benchmarks.yml"
)
START_MARKER = "# BEGIN EXPB_DELAY_RENDER"
END_MARKER = "# END EXPB_DELAY_RENDER"


def render_scripts() -> list[str]:
    text = WORKFLOW.read_text()
    sections = []
    remaining = text
    while START_MARKER in remaining:
        _, remaining = remaining.split(START_MARKER, 1)
        body, remaining = remaining.split(END_MARKER, 1)
        sections.append(textwrap.dedent(body))
    return sections


def shell_path(path: str, cygpath: str | None) -> str:
    if cygpath is None:
        return path
    return subprocess.run(
        [cygpath, "-u", path], capture_output=True, text=True, check=True
    ).stdout.strip()


@pytest.fixture(scope="module")
def render_dependencies():
    bash = shutil.which("bash")
    yq = os.environ.get("PRIORITY_YQ") or shutil.which("yq")
    if bash is None or yq is None:
        pytest.skip(
            "actual delay-render fixture execution requires Git Bash and mikefarah/yq; "
            "install both or set PRIORITY_YQ to the Go yq executable"
        )
    version = subprocess.run(
        [yq, "--version"], capture_output=True, text=True, check=True
    )
    version_text = f"{version.stdout}\n{version.stderr}".lower()
    if "mikefarah" not in version_text:
        pytest.skip(
            f"delay-render fixtures require mikefarah/yq, found: {version_text.strip()}"
        )
    cygpath = shutil.which("cygpath")
    return bash, shell_path(yq, cygpath), cygpath


def run_render(
    render_script: str,
    source: str,
    delay: str,
    render_dependencies,
    tmp_path: Path,
):
    bash, yq, cygpath = render_dependencies
    source_path = tmp_path / "source.yaml"
    rendered_path = tmp_path / "rendered.yaml"
    source_path.write_text(source)
    environment = os.environ.copy()
    environment.update(
        {
            "YQ": yq,
            "SOURCE_CONFIG_FILE": shell_path(str(source_path), cygpath),
            "RENDERED_CONFIG_FILE": shell_path(str(rendered_path), cygpath),
            "DOCKER_TAG": "priority-fixture",
            "DELAY_SECONDS": delay,
            "AMOUNT": "",
            "EXPB_DATA_DIR": "/expb-data",
            "FLAT_SNAPSHOT_DIR": "/snapshots/flat",
            "FLAT_SNAPSHOT_BLOCK_DIR": "/snapshots/block",
            "SCENARIO_NAME": "nethermind",
        }
    )
    command = render_script + '\nprintf \'effective_payload_delay=%s\\n\' "${effective_delay}"\n'
    return subprocess.run(
        [bash, "-euo", "pipefail", "-c", command],
        env=environment,
        capture_output=True,
        text=True,
    ), rendered_path


@pytest.mark.parametrize(
    ("scenario", "delay", "expected_delay"),
    [
        ("delay: 2\n    warmup_delay: 3", "0", 0),
        ("delay: <<DELAY>>\n    warmup_delay: 3", "0.25", 0.25),
        ("payloads: /fixtures/payloads.jsonl\n    warmup_delay: 3", "0.25", 0.25),
        (
            "delay: 2\n    payloads_delay: 2\n    warmup_delay: 3\n    payloads_warmup_delay: 4",
            "0.25",
            0.25,
        ),
    ],
)
def test_actual_workflow_delay_render_fixtures(
    scenario, delay, expected_delay, render_dependencies, tmp_path
):
    source = f"scenarios:\n  nethermind:\n    {scenario}\n"

    for render_script in render_scripts():
        result, rendered_path = run_render(
            render_script, source, delay, render_dependencies, tmp_path
        )
        assert result.returncode == 0, result.stdout + result.stderr
        assert f"effective_payload_delay={expected_delay}" in result.stdout
        rendered_scenario = yaml.safe_load(rendered_path.read_text())["scenarios"][
            "nethermind"
        ]
        assert rendered_scenario.get("delay") == expected_delay
        if "payloads_delay" in rendered_scenario:
            assert rendered_scenario["payloads_delay"] == expected_delay
        assert rendered_scenario.get("warmup_delay") == 3 or rendered_scenario.get(
            "payloads_warmup_delay"
        ) == 3
        if "payloads_warmup_delay" in rendered_scenario:
            assert rendered_scenario["payloads_warmup_delay"] == 4


def test_actual_workflow_delay_render_rejects_missing_scenario(
    render_dependencies, tmp_path
):
    source = "scenarios:\n  other:\n    delay: 2\n"

    for render_script in render_scripts():
        result, _ = run_render(
            render_script, source, "0", render_dependencies, tmp_path
        )
        assert result.returncode != 0
        assert "Rendered scenario 'nethermind' is missing" in (
            result.stdout + result.stderr
        )
