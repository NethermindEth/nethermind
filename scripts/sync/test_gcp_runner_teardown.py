#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Coverage for the reclaim timestamp destroy.sh reports.

sync-master-validation drops a Slack page when a reclaim can be shown to have caused the
failure, and treats 0 as "unknown, so page anyway". An unknown time that is not 0 therefore
silences a genuine regression, which is what `date -d ""` produced: GNU date reads an empty
string as today at 00:00:00 and exits 0, so it landed before every job in the run.
"""

import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ACTION = REPO / ".github" / "actions" / "gcp-runner"

# Distinguishes the two `operations list` calls by the --format destroy.sh asks each for.
GCLOUD_STUB = """#!/usr/bin/env bash
case "$*" in
  *"instances list"*) printf '%s' "${INSTANCE_ZONE:-}"; exit "${LOOKUP_STATUS:-0}" ;;
  *"operations list"*insertTime*) printf '%s' "${INSERT_TIME:-}" ;;
  *"operations list"*) printf '%s\\n' "${OPERATIONS:-}" ;;
  *"instances delete"*) exit 0 ;;
esac
exit 0
"""

GH_STUB = """#!/usr/bin/env bash
exit 0
"""


def gnu_date():
    return subprocess.run(
        ["date", "-d", "2020-01-01T00:00:00Z", "+%s"], capture_output=True
    ).returncode == 0


class DestroyTimestampTest(unittest.TestCase):
    def destroy(self, **env):
        """Runs destroy.sh against stubs and returns its GITHUB_OUTPUT as a dict."""
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            binaries = tmp / "bin"
            binaries.mkdir()
            for name, body in (("gcloud", GCLOUD_STUB), ("gh", GH_STUB)):
                stub = binaries / name
                stub.write_text(textwrap.dedent(body))
                stub.chmod(0o755)

            output = tmp / "output"
            output.touch()
            run = subprocess.run(
                ["bash", str(ACTION / "destroy.sh")],
                capture_output=True,
                text=True,
                stdin=subprocess.DEVNULL,
                env={
                    **os.environ,
                    "PATH": f"{binaries}:{os.environ['PATH']}",
                    "RUNNER_LABEL": "f-1-master-mainnet",
                    "PROJECT_ID": "p",
                    "ZONES": "europe-west1-b",
                    "GITHUB_REPOSITORY": "o/r",
                    "GITHUB_OUTPUT": str(output),
                    "GITHUB_STEP_SUMMARY": str(tmp / "summary"),
                    "GH_TOKEN": "",
                    "EXPECT_INSTANCE": "true",
                    "OPERATIONS": "compute.instances.preempted",
                    "INSTANCE_ZONE": "",
                    **env,
                },
            )
            parsed = dict(
                line.split("=", 1)
                for line in output.read_text().splitlines()
                if "=" in line
            )
            return run.returncode, parsed

    def test_a_reclaim_with_no_readable_time_reports_zero(self):
        # The regression: an empty insertTime used to become today's midnight, which the
        # notify gate reads as "before the job failed" and so as explaining the failure.
        code, out = self.destroy(INSERT_TIME="")
        self.assertEqual(code, 0)
        self.assertEqual(out["preempted"], "true")
        self.assertEqual(out["preempted_at"], "0")

    def test_an_unparseable_time_reports_zero(self):
        code, out = self.destroy(INSERT_TIME="not-a-timestamp")
        self.assertEqual(code, 0)
        self.assertEqual(out["preempted_at"], "0")

    @unittest.skipUnless(gnu_date(), "needs GNU date -d, as on the runner")
    def test_a_readable_time_is_reported_as_epoch_seconds(self):
        code, out = self.destroy(INSERT_TIME="2026-09-08T21:47:04.630-07:00")
        self.assertEqual(code, 0)
        self.assertEqual(out["preempted_at"], "1788929224")

    def test_a_failed_lookup_reports_zero_rather_than_nothing(self):
        # This path refuses to claim the VM was reaped, so it must not leave the caller
        # reading an empty timestamp either.
        code, out = self.destroy(LOOKUP_STATUS="1")
        self.assertEqual(code, 1)
        self.assertEqual(out["terminated_by"], "lookup-failed")
        self.assertEqual(out["preempted_at"], "0")

    def test_no_reclaim_reports_zero(self):
        code, out = self.destroy(OPERATIONS="delete")
        self.assertEqual(code, 0)
        self.assertEqual(out["preempted"], "false")
        self.assertEqual(out["preempted_at"], "0")


if __name__ == "__main__":
    unittest.main()
