#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise the client policy and image matrix in the EXPB workflow.

The tests execute the checked-in Bash bodies so resolver and matrix cases cover the same
validation and output construction used by GitHub Actions.

Run with: python -m unittest scripts.ci.test_expb_multiclient_workflow
"""

import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github/workflows/run-expb-reproducible-benchmarks.yml"


def extract_step(path, step_name):
    """Extract the dedented ``run: |`` body for a named workflow step."""
    lines = path.read_bytes().decode("utf-8-sig").replace("\r\n", "\n").split("\n")
    for index, line in enumerate(lines):
        header = re.match(r"^(?P<indent>\s*)- name: (?P<name>.*?)\s*$", line)
        if not header or header["name"] != step_name:
            continue
        run_index = next(
            i
            for i in range(index + 1, len(lines))
            if re.match(r"^\s+run:\s*\|\s*$", lines[i])
        )
        run_indent = len(re.match(r"^(\s+)", lines[run_index]).group(1))
        body_indent = run_indent + 2
        body = []
        for candidate in lines[run_index + 1 :]:
            if candidate and len(candidate) - len(candidate.lstrip(" ")) < body_indent:
                break
            body.append(candidate[body_indent:] if candidate else "")
        return "\n".join(body) + "\n"
    raise AssertionError("workflow step {!r} was not found".format(step_name))


def find_bash():
    if os.name != "nt":
        return shutil.which("bash")
    git = shutil.which("git")
    if git:
        candidate = Path(git).parent.parent / "bin" / "bash.exe"
        if candidate.exists():
            return str(candidate)
    bash = shutil.which("bash")
    return bash if bash and "System32" not in bash else None


def to_bash(path):
    """Convert a native path to the form accepted by Git Bash."""
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


def parse_output(path):
    values = {}
    lines = path.read_text(encoding="utf-8").splitlines()
    index = 0
    while index < len(lines):
        line = lines[index]
        if line.endswith("<<EOF"):
            key = line[: -len("<<EOF")]
            index += 1
            multiline = []
            while index < len(lines) and lines[index] != "EOF":
                multiline.append(lines[index])
                index += 1
            values[key] = "\n".join(multiline)
        elif "=" in line:
            key, value = line.split("=", 1)
            values[key] = value
        index += 1
    return values


class ExpbWorkflowTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if cls.bash is None:  # pragma: no cover - environment guard
            raise unittest.SkipTest("a POSIX bash is required to execute workflow steps")
        cls.resolver = extract_step(WORKFLOW, "Resolve branch and configuration")
        cls.matrix = extract_step(WORKFLOW, "Resolve Docker images")
        cls.summary = extract_step(WORKFLOW, "Build comparison table")

    def run_body(self, body, values, output_name="github-output"):
        with tempfile.TemporaryDirectory(prefix="expb-workflow-test-") as temp_dir:
            temp_path = Path(temp_dir)
            output = temp_path / output_name
            output.touch()
            env = dict(os.environ)
            env.update(values)
            env["GITHUB_OUTPUT"] = to_bash(output)
            script = temp_path / "workflow-step.sh"
            script.write_text(body, encoding="utf-8")
            proc = subprocess.run(
                [self.bash, "--noprofile", "--norc", "-eo", "pipefail", to_bash(script)],
                env=env,
                cwd=REPO,
                capture_output=True,
                text=True,
            )
            return proc, parse_output(output), temp_path

    def run_resolver(self, **overrides):
        values = {
            "EVENT_NAME": "workflow_dispatch",
            "PUSH_BRANCH": "feature/test-client",
            "DISPATCH_CLIENT": "nethermind",
            "DISPATCH_MEASUREMENT_SOURCE": "auto",
            "DISPATCH_STATE_LAYOUT": "flat",
            "DISPATCH_ARCH": "amd64",
            "DISPATCH_PAYLOAD_SET": "fusaka",
            "DISPATCH_EXPB_REPO": "NethermindEth/execution-payloads-benchmarks",
            "DISPATCH_EXPB_BRANCH": "main",
            "DISPATCH_DELAY_SECONDS": "0",
            "DISPATCH_AMOUNT": "25",
            "DISPATCH_ADDITIONAL_EXTRA_FLAGS": "",
            "DISPATCH_FLAT_WRITE_BUFFER_FLOOR": "off",
            "DISPATCH_EXPB_ENV": "",
            "DISPATCH_REBUILD_DOCKER": "true",
            "DISPATCH_RUN_COUNT": "1",
            "DISPATCH_DOCKER_IMAGES": "vendor/client:latest",
            "DISPATCH_ENABLE_RETROSPECTIVE": "false",
            "DISPATCH_RETROSPECTIVE_LAST": "100",
            "DISPATCH_RETROSPECTIVE_STEP": "10",
            "DISPATCH_DOTTRACE": "false",
            "DISPATCH_PERF": "false",
            "DISPATCH_TRACE_BLOCKS": "",
            "DISPATCH_CLIENT_ENV": "",
        }
        values.update(overrides)
        proc, output, _ = self.run_body(self.resolver, values)
        return proc.returncode, proc.stdout + proc.stderr, output

    def run_matrix(self, docker_images):
        proc, output, _ = self.run_body(
            self.matrix,
            {
                "DOCKER_IMAGES": docker_images,
                "LAST": "100",
                "STEP": "10",
                "RUN_COUNT": "1",
            },
        )
        return proc.returncode, proc.stdout + proc.stderr, output

    def test_reth_amd64_selects_snapshot_and_common_timing(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth")
        self.assertEqual(0, code, log)
        self.assertEqual("reth", output["client"])
        self.assertEqual("engine-api", output["measurement_source"])
        self.assertEqual("/mnt/sda/reth-25490000", output["client_snapshot_dir"])
        self.assertEqual("/execution-data", output["snapshot_mount_path"])
        self.assertEqual("false", output["rebuild_docker"])

    def test_arm_reth_selects_reth_snapshot(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth", DISPATCH_ARCH="arm64")
        self.assertEqual(0, code, log)
        self.assertEqual("reproducible-benchmarks-arm", output["runner_label"])
        self.assertEqual("/data/reth/reth-25490000", output["client_snapshot_dir"])

    def test_reference_clients_require_flat_layout(self):
        for client in ("reth", "geth"):
            with self.subTest(client=client):
                code, log, _ = self.run_resolver(
                    DISPATCH_CLIENT=client, DISPATCH_STATE_LAYOUT="halfpath"
                )
                self.assertNotEqual(0, code)
                self.assertIn("requires state_layout=flat", log)

    def test_arm_geth_is_rejected(self):
        code, log, _ = self.run_resolver(DISPATCH_CLIENT="geth", DISPATCH_ARCH="arm64")
        self.assertNotEqual(0, code)
        self.assertIn("client=geth requires arch=amd64", log)

    def test_non_nethermind_requires_fusaka_and_explicit_images(self):
        cases = (
            ({"DISPATCH_PAYLOAD_SET": "realblocks"}, "payload_set=fusaka only"),
            ({"DISPATCH_DOCKER_IMAGES": ""}, "requires explicit docker_images"),
        )
        for overrides, expected in cases:
            with self.subTest(expected=expected):
                code, log, _ = self.run_resolver(DISPATCH_CLIENT="reth", **overrides)
                self.assertNotEqual(0, code)
                self.assertIn(expected, log)

    def test_nethermind_default_preserves_sse_and_flatdb_behavior(self):
        code, log, output = self.run_resolver(
            DISPATCH_CLIENT="nethermind",
            DISPATCH_FLAT_WRITE_BUFFER_FLOOR="67108864",
        )
        self.assertEqual(0, code, log)
        self.assertEqual("nethermind", output["client"])
        self.assertEqual("auto", output["measurement_source"])
        self.assertEqual("", output["client_snapshot_dir"])
        self.assertEqual("", output["snapshot_mount_path"])
        self.assertIn("FlatDb.PersistenceWriteBufferFloor=67108864", output["additional_extra_flags"])

    def test_empty_explicit_image_list_is_rejected_before_matrix_output(self):
        code, log, output = self.run_matrix(" , , ")
        self.assertNotEqual(0, code)
        self.assertIn("must contain at least one non-empty image reference", log)
        self.assertNotIn("matrix", output)

    def test_duplicate_tags_keep_full_images_in_ordered_matrix(self):
        code, log, output = self.run_matrix("a/x:latest,b/x:latest")
        self.assertEqual(0, code, log)
        ordered = json.loads(output["ordered_tags"])
        self.assertEqual(
            [
                {"tag": "latest-0", "date": "n/a", "image": "a/x:latest"},
                {"tag": "latest-1", "date": "n/a", "image": "b/x:latest"},
            ],
            ordered,
        )

    def test_summary_maps_disambiguated_tags_to_full_images(self):
        code, log, matrix = self.run_matrix("a/x:latest,b/x:latest")
        self.assertEqual(0, code, log)
        with tempfile.TemporaryDirectory(prefix="expb-summary-test-") as temp_dir:
            temp_path = Path(temp_dir)
            metrics = temp_path / "metrics"
            for tag in ("latest-0", "latest-1"):
                artifact = metrics / "expb-metrics-{}-run1".format(tag)
                artifact.mkdir(parents=True)
                (artifact / "metrics.env").write_text(
                    "AVG=1\nMEDIAN=1\nP90=1\nP95=1\nP99=1\nMIN=1\nMAX=1\n",
                    encoding="utf-8",
                )
            summary = temp_path / "summary.md"
            env = {
                "METRICS_DIR": to_bash(metrics),
                "ORDERED_TAGS": matrix["ordered_tags"],
                "RUN_COUNT": "1",
                "RUNNER_TEMP": to_bash(temp_path),
                "GITHUB_STEP_SUMMARY": to_bash(summary),
            }
            proc, _, _ = self.run_body(self.summary, env)
            self.assertEqual(0, proc.returncode, proc.stdout + proc.stderr)
            # Git Bash's Windows jq can leave CR characters in mapfile values. The workflow runs
            # this step on Ubuntu, but normalize them here so the test remains portable.
            text = summary.read_bytes().decode("utf-8").replace("\r", "")
            self.assertIn("`latest-0` → `a/x:latest`", text)
            self.assertIn("`latest-1` → `b/x:latest`", text)


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
