#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise RPC workflow setup with a sparse checkout left by another job."""

import os
import re
import shlex
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from test_reap_stale_overlays import extract_step_bodies, find_bash, to_bash

WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/run-rpc-benchmarks.yml"


class RpcRunnerWorkspaceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = find_bash()
        if not cls.bash or not shutil.which("git"):
            raise unittest.SkipTest("bash and git are required")
        cls.job = WORKFLOW.read_text().split("\n  benchmark:\n", 1)[1].split(
            "\n  generate-dottrace-reports:\n", 1
        )[0]
        cls.setup_body, = extract_step_bodies(WORKFLOW, "Set up run paths")
        cls.summary_body, = extract_step_bodies(WORKFLOW, "Publish step summary")

    def step(self, name):
        return self.job.split(f"      - name: {name}\n", 1)[1].split("\n      - name:", 1)[0]

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="rpc runner ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name).resolve()
        self.workspace = self.root / "workspace"
        self.workspace.mkdir()
        self.runner_temp = self.root / "runner-temp"
        self.runner_temp.mkdir()
        self.env = dict(os.environ, GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull)
        self.env.update(
            GITHUB_WORKSPACE=to_bash(self.workspace),
            RUNNER_TEMP=to_bash(self.runner_temp),
            GITHUB_ENV=to_bash(self.root / "github-env"),
            GITHUB_STEP_SUMMARY=to_bash(self.root / "summary.md"),
            DOTNET_TRACE="false",
        )
        self.git(self.workspace, "init", "-q")
        for path in ("scripts/expb/driver.sh", "scripts/rpc-bench/start-node.sh", "Dockerfile"):
            target = self.workspace / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text("fixture\n")
        self.git(self.workspace, "add", ".")
        self.git(self.workspace, "-c", "user.name=Test", "-c", "user.email=test@example.com",
                 "commit", "-qm", "fixture")

    def git(self, cwd, *args):
        return subprocess.run(["git", *args], cwd=cwd, env=self.env, text=True,
                              capture_output=True, check=True).stdout.strip()

    def run_body(self, body, cwd, expected=0):
        result = subprocess.run([self.bash, "--noprofile", "--norc", "-eo", "pipefail", "-c", body],
                                cwd=cwd, env=self.env, text=True, capture_output=True)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        return result

    def prepare(self):
        setup = self.step("Set up run paths")
        self.assertRegex(setup, r"(?m)^        working-directory: \.$")
        self.assertLess(self.job.index("- name: Set up run paths"),
                        self.job.index("- name: Check out repository"))
        self.run_body(self.setup_body, self.workspace)
        for line in (self.root / "github-env").read_text().splitlines():
            key, value = line.split("=", 1)
            self.env[key] = value
        path = re.search(r"(?m)^          path: (.+)$", self.step("Check out repository"))
        self.assertIsNotNone(path, "RPC checkout must have its own path")
        run_cwd = re.search(r"(?m)^        working-directory: (.+)$", self.job.split("    steps:", 1)[0])
        self.assertIsNotNone(run_cwd, "script steps must run in the RPC checkout")
        self.assertEqual(path[1], run_cwd[1])
        self.assertNotEqual(".", path[1])
        return self.workspace / path[1]

    def checkout(self, target):
        # Match checkout's init/fetch/checkout sequence inside an existing parent repository.
        self.git(target, "init", "-q")
        if not self.git(target, "remote"):
            self.git(target, "remote", "add", "origin", self.workspace.as_uri())
        self.git(target, "fetch", "--depth=1", "origin", "HEAD")
        self.git(target, "checkout", "--force", "--detach", "FETCH_HEAD")
        chmod = re.search(r"(?m)^        run: (.+)$", self.step("Make scripts executable"))[1]
        self.run_body(chmod, target)

    def test_checkout_ignores_parent_sparse_state(self):
        self.git(self.workspace, "sparse-checkout", "set", "--no-cone", "/scripts/expb/")
        self.assertFalse((self.workspace / "scripts/rpc-bench/start-node.sh").exists())
        # Also cover orphaned skip-worktree bits after sparseCheckout was disabled in config.
        for sparse_enabled in (True, False):
            with self.subTest(sparse_enabled=sparse_enabled):
                if not sparse_enabled:
                    self.git(self.workspace, "config", "--worktree", "core.sparseCheckout", "false")
                parent_index = self.git(self.workspace, "ls-files", "-v")
                self.assertIn("S scripts/rpc-bench/start-node.sh", parent_index)
                self.assertEqual("", self.git(self.workspace, "status", "--porcelain", "--untracked-files=no"))
                target = self.prepare()
                self.checkout(target)
                self.assertEqual("fixture\n", (target / "scripts/rpc-bench/start-node.sh").read_text())
                self.assertTrue((target / "Dockerfile").is_file())
                self.assertEqual(self.git(self.workspace, "rev-parse", "HEAD"), self.git(target, "rev-parse", "HEAD"))
                self.assertEqual(parent_index, self.git(self.workspace, "ls-files", "-v"))
                self.assertFalse((self.workspace / "scripts/rpc-bench/start-node.sh").exists())

    def test_summary_survives_checkout_failure(self):
        target = self.prepare()
        self.assertTrue(target.is_dir(), "always() steps need a cwd even when checkout fails")
        self.assertFalse((target / ".git").exists())
        # No checkout or benchmark scripts ran. Render the actual summary with ordinary inputs.
        body = re.sub(r"\$\{\{.*?\}\}", "false", self.summary_body)
        self.run_body(body, target)
        self.assertIn("# RPC Benchmark", (self.root / "summary.md").read_text())

    def test_script_teardown_requires_successful_setup(self):
        for name in ("Stop reference node and verify DB integrity", "Stop node and verify DB integrity",
                     "Collect perf profile", "Stage private corpus results", "Defensive cleanup"):
            with self.subTest(step=name):
                self.assertIn("if: always() && steps.scripts.outcome == 'success'", self.step(name))
        self.assertIn("id: scripts", self.step("Make scripts executable"))
        for name, start in (("Stop node and verify DB integrity", "start-node"),
                            ("Stop reference node and verify DB integrity", "start-reference")):
            with self.subTest(step=name):
                self.assertIn(f"steps.{start}.outcome != 'skipped'", self.step(name))

    def maintenance_body(self, name, root_gb=100, docker_gb=100, containerd_gb=100):
        runner = self.root / "actions-runner"
        docker_root = self.root / "docker"
        containerd_root = self.root / "containerd"
        docker_root.mkdir(exist_ok=True)
        containerd_root.mkdir(exist_ok=True)
        self.env.update(
            TEST_DOCKER_ROOT=to_bash(docker_root),
            TEST_ROOT_BYTES=str(int(root_gb * 1024**3)),
            TEST_DOCKER_BYTES=str(int(docker_gb * 1024**3)),
            TEST_CONTAINERD_BYTES=str(int(containerd_gb * 1024**3)),
            KEEP_IMAGES="",
            KEEP_TOOL_CONFIG="{}",
        )
        # Stub host maintenance, but run any file removals against this throwaway runner tree.
        preamble = """
docker() {
  if [[ "$1" == "info" ]]; then echo "$TEST_DOCKER_ROOT"; fi
  return 0
}
df() {
  case "${!#}" in
    /) printf 'Avail\\n%s\\n' "$TEST_ROOT_BYTES" ;;
    "$TEST_DOCKER_ROOT") printf 'Avail\\n%s\\n' "$TEST_DOCKER_BYTES" ;;
    *) printf 'Avail\\n%s\\n' "$TEST_CONTAINERD_BYTES" ;;
  esac
}
du() { :; }
find() { :; }
apt-get() { :; }
journalctl() { :; }
"""
        body, = extract_step_bodies(WORKFLOW, name)
        body = body.replace("/root/actions-runner", shlex.quote(to_bash(runner)))
        body = body.replace("/var/lib/containerd", shlex.quote(to_bash(containerd_root)))
        return preamble + body

    def test_disk_reclaim_preserves_active_job_files(self):
        temp = self.root / "actions-runner/_work/_temp"
        temp.mkdir(parents=True)
        for name in ("Reclaim root disk before pulling", "Reclaim root disk after the run"):
            with self.subTest(step=name):
                active_files = (temp / "git-credentials.config", temp / "node.env", temp / "summary.md")
                for path in active_files:
                    path.write_text("active job data\n")
                self.run_body(self.maintenance_body(name), self.workspace)
                for path in active_files:
                    self.assertTrue(path.is_file(), f"{name} deleted {path.name}")

    def test_disk_gate_checks_each_storage_filesystem(self):
        for root_gb, docker_gb, containerd_gb, expected in (
            (1.5, 968, 968, 0),
            (0.9, 968, 968, 1),
            (8, 5.9, 100, 1),
            (8, 100, 5.9, 1),
            (7, 7, 7, 0),
        ):
            with self.subTest(root=root_gb, docker=docker_gb, containerd=containerd_gb):
                body = self.maintenance_body("Reclaim root disk before pulling", root_gb, docker_gb, containerd_gb)
                self.run_body(body, self.workspace, expected=expected)

    @unittest.skipUnless(shutil.which("jq"), "jq is required to resolve workflow inputs")
    def test_snapshot_defaults_and_explicit_override(self):
        body, = extract_step_bodies(WORKFLOW, "Resolve configuration")
        body = re.sub(r"\$\{\{.*?\}\}", "0" * 40, body)
        output = self.root / "github-output"
        self.env.update(EVENT_NAME="workflow_dispatch", PUSH_BRANCH="master", IN_TOOL="jsonbench",
                        IN_DOCKER_IMAGE="nethermindeth/nethermind:master", GITHUB_OUTPUT=to_bash(output))
        for arch, block, expected in (
            ("arm64", "", "/data/nethermind/nethermind-flat-25490000"),
            ("amd64", "", "/mnt/sda/nethermind-flat-snapshot"),
            ("arm64", "12345", "/data/nethermind/nethermind-flat-12345"),
        ):
            with self.subTest(arch=arch, snapshot_block=block):
                output.write_text("")
                self.env.update(IN_ARCH=arch, IN_SNAPSHOT_BLOCK=block)
                self.run_body(body, self.workspace)
                self.assertIn(f"db_source={expected}\n", output.read_text())


if __name__ == "__main__":
    unittest.main()
