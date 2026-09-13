#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Checks the client policy in the EXPB workflow's resolver.

The resolver is the boundary that prevents a client/image/snapshot combination from reaching a
long running self-hosted job. Execute the checked-in Bash body so these cases cover the same
validation and path selection used by Actions.

Run with: python -m unittest scripts.ci.test_expb_multiclient_workflow
"""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path


REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github/workflows/run-expb-reproducible-benchmarks.yml"


def extract_resolver(path):
    """Extract the Resolve branch and configuration step's run block."""
    lines = path.read_bytes().decode("utf-8-sig").replace("\r\n", "\n").replace("\r", "\n").split("\n")
    for index, line in enumerate(lines):
        if not re.match(r"^\s*- name: Resolve branch and configuration\s*$", line):
            continue
        run_index = next(
            i for i in range(index + 1, len(lines)) if re.match(r"^\s+run:\s*\|\s*$", lines[i])
        )
        indent = len(re.match(r"^(\s+)", lines[run_index]).group(1)) + 2
        body = []
        for candidate in lines[run_index + 1 :]:
            if candidate and len(candidate) - len(candidate.lstrip(" ")) < indent:
                break
            body.append(candidate[indent:] if candidate else "")
        return "\n".join(body) + "\n"
    raise AssertionError("resolver step was not found")


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
    """Convert a native Windows path to the form accepted by Git Bash."""
    if os.name != "nt":
        return str(path)
    text = str(path).replace("\\", "/")
    return "/" + text[0].lower() + text[2:] if len(text) > 1 and text[1] == ":" else text


class ExpbClientPolicy(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if cls.bash is None:  # pragma: no cover - environment guard
            raise unittest.SkipTest("a POSIX bash is required to execute the workflow resolver")
        cls.body = extract_resolver(WORKFLOW)

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
            "DISPATCH_EXPB_BRANCH": "fbe0bc94cb9fa2bd176416d8e2aed2c1c172817d",
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
        env = dict(os.environ)
        env.update(values)
        with tempfile.TemporaryDirectory(prefix="expb-resolver-test-") as temp_dir:
            temp_path = Path(temp_dir)
            output = temp_path / "github-output"
            output.touch()
            env["GITHUB_OUTPUT"] = str(output)
            script = temp_path / "resolver.sh"
            script.write_bytes(self.body.encode())
            proc = subprocess.run(
                [self.bash, "--noprofile", "--norc", "-eo", "pipefail", to_bash(script)],
                env=env,
                cwd=REPO,
                capture_output=True,
                text=True,
            )
            output_text = output.read_text(encoding="utf-8")
        combined = proc.stdout + proc.stderr
        values = {}
        lines = output_text.splitlines()
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
        return proc.returncode, combined, values

    def test_reth_amd64_selects_snapshot_and_common_timing(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth")
        self.assertEqual(0, code, log)
        self.assertEqual("reth", output["client"])
        self.assertEqual("engine-api", output["measurement_source"])
        self.assertEqual("/mnt/sda/reth-25490000", output["client_snapshot_dir"])
        self.assertEqual("/execution-data", output["snapshot_mount_path"])
        self.assertEqual("false", output["rebuild_docker"])

    def test_geth_uses_nested_snapshot_mount(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="geth")
        self.assertEqual(0, code, log)
        self.assertEqual("/mnt/sda/geth-25490000", output["client_snapshot_dir"])
        self.assertEqual("/execution-data/geth", output["snapshot_mount_path"])

    def test_arm_reth_selects_reth_snapshot(self):
        code, log, output = self.run_resolver(DISPATCH_CLIENT="reth", DISPATCH_ARCH="arm64")
        self.assertEqual(0, code, log)
        self.assertEqual("reproducible-benchmarks-arm", output["runner_label"])
        self.assertEqual("/data/reth/reth-25490000", output["client_snapshot_dir"])

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

    def test_non_nethermind_rejects_nethermind_only_inputs(self):
        cases = (
            ("DISPATCH_ADDITIONAL_EXTRA_FLAGS", "--Sync.FastSync=false", "additional_extra_flags"),
            ("DISPATCH_CLIENT_ENV", "DOTNET_GCServer=true", "client_env"),
            ("DISPATCH_EXPB_ENV", "EXPB_EVM_WARMUP=1", "EXPB_EVM_WARMUP"),
            ("DISPATCH_TRACE_BLOCKS", "25490001", "trace_blocks"),
            ("DISPATCH_DOTTRACE", "sampling", "dottrace"),
        )
        for variable, value, expected in cases:
            with self.subTest(variable=variable):
                code, log, _ = self.run_resolver(DISPATCH_CLIENT="reth", **{variable: value})
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


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
