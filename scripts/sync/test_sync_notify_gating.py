#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Coverage for the Slack gating in sync-master-validation's notify-on-failure job.

The step body is read out of the workflow so the shipped code is what runs here.
"""

import os
import subprocess
import sys
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
WORKFLOW = REPO / ".github" / "workflows" / "sync-master-validation.yml"

# Imported rather than copied so a fix to the parser covers both callers.
sys.path.insert(0, str(REPO / "scripts" / "ci"))
from test_reap_stale_overlays import extract_step_bodies  # noqa: E402

MATRIX = (
    '[{"network":"mainnet","mode":"Flat","runner_label":"f-1-master-mainnet"},'
    '{"network":"mainnet","mode":"HalfPath","runner_label":"hp-1-master-mainnet"},'
    '{"network":"gnosis","mode":"Flat","runner_label":"f-1-master-gnosis"}]'
)

GH_STUB = """#!/usr/bin/env bash
for a in "$@"; do
  case "$a" in
    */jobs) exec cat "$JOBS_FIXTURE" ;;
    */artifacts) exec cat "$ARTIFACTS_FIXTURE" ;;
  esac
done
"""


class NotifyGatingTest(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        bodies = extract_step_bodies(WORKFLOW, "Collect failed jobs")
        assert len(bodies) == 1, f"expected one step, found {len(bodies)}"
        cls.body = "set -euo pipefail\n" + bodies[0]

    def collect(self, failed_jobs, preempted_labels, matrix=MATRIX):
        """Runs the shipped step and returns what it wrote to GITHUB_OUTPUT."""
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            binaries = tmp / "bin"
            binaries.mkdir()
            stub = binaries / "gh"
            stub.write_text(GH_STUB)
            stub.chmod(0o755)

            (tmp / "jobs").write_text("".join(f"{n}\n" for n in failed_jobs))
            (tmp / "artifacts").write_text(
                "".join(f"{label}\n" for label in preempted_labels)
            )
            (tmp / "step.sh").write_text(self.body)
            output = tmp / "output"
            output.touch()

            subprocess.run(
                ["bash", str(tmp / "step.sh")],
                check=True,
                capture_output=True,
                text=True,
                env={
                    **os.environ,
                    "PATH": f"{binaries}:{os.environ['PATH']}",
                    "GH_TOKEN": "x",
                    "REPO": "o/r",
                    "RUN_ID": "1",
                    "MATRIX": matrix,
                    "GITHUB_OUTPUT": str(output),
                    "GITHUB_STEP_SUMMARY": str(tmp / "summary"),
                    "JOBS_FIXTURE": str(tmp / "jobs"),
                    "ARTIFACTS_FIXTURE": str(tmp / "artifacts"),
                },
            )
            return dict(
                line.split("=", 1)
                for line in output.read_text().splitlines()
                if "=" in line
            )

    def test_a_real_failure_pages_and_names_the_job(self):
        out = self.collect(["Sync gnosis (Flat) / sync"], [])
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "Sync gnosis (Flat) / sync")

    def test_a_preempted_cell_alone_does_not_page(self):
        out = self.collect(["Sync mainnet (Flat) / sync"], ["f-1-master-mainnet"])
        self.assertEqual(out["should_page"], "false")
        self.assertNotIn("failed_jobs", out)

    def test_every_cell_preempted_does_not_page(self):
        out = self.collect(
            ["Sync mainnet (Flat) / sync", "Sync gnosis (Flat) / sync"],
            ["f-1-master-mainnet", "f-1-master-gnosis"],
        )
        self.assertEqual(out["should_page"], "false")

    def test_a_real_failure_beside_a_preemption_still_pages_alone(self):
        out = self.collect(
            ["Sync mainnet (Flat) / sync", "Sync gnosis (Flat) / sync"],
            ["f-1-master-mainnet"],
        )
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "Sync gnosis (Flat) / sync")

    def test_every_job_of_a_preempted_cell_is_covered(self):
        out = self.collect(
            ["Sync mainnet (Flat) / sync", "Sync mainnet (Flat) / destroy_runner"],
            ["f-1-master-mainnet"],
        )
        self.assertEqual(out["should_page"], "false")

    def test_the_two_modes_of_one_network_are_not_confused(self):
        out = self.collect(
            ["Sync mainnet (HalfPath) / sync"], ["f-1-master-mainnet"]
        )
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "Sync mainnet (HalfPath) / sync")

    def test_a_marker_for_an_unknown_label_silences_nothing(self):
        out = self.collect(["Sync gnosis (Flat) / sync"], ["f-9-master-sepolia"])
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "Sync gnosis (Flat) / sync")

    def test_no_failed_jobs_still_pages_with_a_placeholder(self):
        # An empty list means the API told us nothing, not that the run was healthy.
        out = self.collect([], [])
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "(check run for details)")

    def test_an_empty_list_pages_even_when_another_cell_was_preempted(self):
        # The job only runs because something failed, so an unreadable list plus a reclaim
        # elsewhere must not be taken as "every failure was a reclaim".
        out = self.collect([], ["f-1-master-mainnet"])
        self.assertEqual(out["should_page"], "true")
        self.assertEqual(out["failed_jobs"], "(check run for details)")

    def test_long_job_names_are_truncated_for_slack(self):
        name = "Sync gnosis (Flat) / " + "x" * 100
        out = self.collect([name], [])
        self.assertEqual(len(out["failed_jobs"]), 60)


if __name__ == "__main__":
    unittest.main()
