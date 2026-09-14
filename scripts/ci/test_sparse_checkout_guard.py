#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression suite for the sparse-checkout guards in run-rpc-benchmarks.yml.

The benchmark job runs on a self-hosted runner whose workspace persists between jobs and is shared
with every other workflow on the box. A workflow that checks out sparsely leaves core.sparseCheckout
and skip-worktree bits behind; skip-worktree then keeps `git status` clean while most of the tree is
absent, so the next job that needs a full checkout fails far from the cause.

The suite extracts both step bodies from the shipped YAML rather than re-implementing them, and
drives them against real git repositories that have been poisoned the same way, so the restore is
exercised rather than argued about.

The bodies run under the options GitHub gives a `shell: bash` step - `--noprofile --norc -eo
pipefail` - because `set -e` is on there and a body cannot turn it off.

Run with: python -m unittest discover -s scripts/ci -p 'test*.py'
"""

import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

from test_reap_stale_overlays import extract_step_bodies

REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github/workflows/run-rpc-benchmarks.yml"
CLEAR_STEP = "Clear inherited sparse-checkout state"
CHMOD_STEP = "Make scripts executable"

# The tree the benchmark job needs; `scripts/expb` stands in for what an expb job narrows the
# checkout down to, and is the only path that survives the poisoning.
TRACKED = [
    "scripts/expb/run.sh",
    "scripts/rpc-bench/start-node.sh",
    "scripts/rpc-bench/lib.sh",
    "scripts/ci/test_expb_workflow.py",
    "src/Nethermind/Nethermind.Core/Block.cs",
]


@unittest.skipIf(shutil.which("git") is None, "git is required")
class SparseCheckoutGuardTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.bash = shutil.which("bash")
        if cls.bash is None:
            raise unittest.SkipTest("bash is required")
        cls.clear_body = cls._single_body(CLEAR_STEP)
        cls.chmod_body = cls._single_body(CHMOD_STEP)

    @staticmethod
    def _single_body(step_name):
        bodies = extract_step_bodies(WORKFLOW, step_name)
        if len(bodies) != 1:
            raise AssertionError(f"expected exactly one '{step_name}' step, found {len(bodies)}")
        return bodies[0]

    def setUp(self):
        self.box = Path(tempfile.mkdtemp(prefix="sparse-guard-"))
        self.addCleanup(shutil.rmtree, self.box, ignore_errors=True)
        self.work = self.box / "workspace"
        self.work.mkdir()

    def git(self, *args, cwd=None):
        return subprocess.run(
            ["git", "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", *args],
            cwd=str(cwd or self.work), capture_output=True, text=True, check=True,
        )

    def make_repo(self):
        self.git("init", "-q", "-b", "main")
        for rel in TRACKED:
            path = self.work / rel
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(f"# {rel}\n")
        self.git("add", "-A")
        self.git("commit", "-qm", "seed")

    def poison(self):
        """Leave the workspace the way an expb job's sparse checkout leaves it."""
        self.git("sparse-checkout", "set", "--no-cone", "scripts/expb")

    def run_body(self, body, cwd=None):
        return subprocess.run(
            [self.bash, "--noprofile", "--norc", "-eo", "pipefail", "-c", body, "guard"],
            cwd=str(cwd or self.work), capture_output=True, text=True,
        )

    def sparse_checkout_setting(self):
        """`git sparse-checkout disable` leaves the key set to false rather than removing it."""
        proc = subprocess.run(["git", "config", "--get", "core.sparseCheckout"],
                              cwd=str(self.work), capture_output=True, text=True)
        return proc.stdout.strip()

    def skip_worktree_count(self):
        out = self.git("ls-files", "-v").stdout.splitlines()
        return sum(1 for line in out if line.startswith("S"))

    def test_poisoning_hides_the_tree_from_git_status(self):
        """The premise: skip-worktree makes the damage invisible to the usual check."""
        self.make_repo()
        self.poison()
        self.assertFalse((self.work / "scripts/rpc-bench").exists())
        self.assertEqual("", self.git("status", "--porcelain").stdout.strip())
        self.assertEqual(len(TRACKED) - 1, self.skip_worktree_count())

    def test_poisoned_workspace_is_restored(self):
        self.make_repo()
        self.poison()
        proc = self.run_body(self.clear_body)
        self.assertEqual(0, proc.returncode, proc.stderr)
        for rel in TRACKED:
            self.assertTrue((self.work / rel).exists(), f"{rel} was not restored")
        self.assertEqual(0, self.skip_worktree_count())
        self.assertNotEqual("true", self.sparse_checkout_setting())

    def test_clean_workspace_is_left_alone(self):
        self.make_repo()
        head = self.git("rev-parse", "HEAD").stdout.strip()
        proc = self.run_body(self.clear_body)
        self.assertEqual(0, proc.returncode, proc.stderr)
        for rel in TRACKED:
            self.assertTrue((self.work / rel).exists(), f"{rel} went missing")
        self.assertEqual(head, self.git("rev-parse", "HEAD").stdout.strip())

    def test_workspace_without_a_checkout_is_a_noop(self):
        """First job on a fresh runner: no .git yet, and the step must not fail the build."""
        proc = self.run_body(self.clear_body)
        self.assertEqual(0, proc.returncode, proc.stderr)

    def test_chmod_guard_rejects_a_partial_tree(self):
        self.make_repo()
        self.poison()
        proc = self.run_body(self.chmod_body)
        self.assertEqual(1, proc.returncode)
        self.assertIn("::error::", proc.stdout)
        self.assertIn("scripts/rpc-bench", proc.stdout)

    def test_chmod_guard_passes_a_complete_tree(self):
        self.make_repo()
        proc = self.run_body(self.chmod_body)
        self.assertEqual(0, proc.returncode, proc.stderr)
        self.assertNotIn("::error::", proc.stdout)


if __name__ == "__main__":
    unittest.main()
