#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise sync metrics against log fixtures, reusable jobs and retried runs."""

import importlib.util
import io
import json
import os
import re
import shutil
import subprocess
import tempfile
import unittest
from contextlib import redirect_stdout, redirect_stderr
from pathlib import Path
from unittest.mock import patch, MagicMock

ROOT = Path(__file__).resolve().parents[2]
SCRIPTS = ROOT / "scripts/sync-metrics"


def load_module(name):
    spec = importlib.util.spec_from_file_location(f"sync_metrics_{name}", SCRIPTS / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


collect = load_module("collect")
publish = load_module("publish")


def log_line(minute, text, prefix=""):
    return f"{prefix}2026-09-16T12:{minute:02}:00.123456789Z {text}\n"


class CollectTests(unittest.TestCase):
    def test_stages_counters_and_duplicate_markers(self):
        for prefix in ("", "job\tstep\t"):
            with self.subTest(prefix=prefix):
                lines = [log_line(minute, text, prefix) for minute, text in (
                    (0, "Starting snap sync."), (9, "Starting snap sync."),
                    (10, "Snap sync completed."), (12, "STATE SYNC FINISHED"),
                    (13, "Collecting trie stats"), (20, "Waiting for storage verification workers"),
                    (23, "Verification complete. Accounts=400000000, Slots=1600000000"),
                )]
                found, counters = collect.parse_log(lines)
                self.assertEqual(10, collect.minutes_between(found, "snap_start", "snap_end"))
                self.assertEqual(2, collect.minutes_between(found, "snap_end", "state_finished"))
                self.assertEqual(3, collect.minutes_between(found, "drain_start", "verify_end_flat"))
                self.assertEqual({"accounts": 400000000, "slots": 1600000000}, counters)

    def test_missing_and_out_of_order_markers_do_not_produce_negative_timings(self):
        found, _ = collect.parse_log([
            "untimestamped Starting snap sync.\n", log_line(5, "Snap sync completed."),
            log_line(10, "Starting snap sync."), "2026-09-16T12:00:00Z irrelevant\n",
        ])
        self.assertIsNone(collect.minutes_between(found, "snap_start", "snap_end"))
        self.assertIsNone(collect.minutes_between(found, "verify_start", "verify_end_flat"))

    def test_cli_supports_both_layouts_and_partial_logs(self):
        for mode, ending, expect_verify in (
            ("Flat", "Verification complete. Accounts=4, Slots=8", True),
            ("HalfPath", "Stats after finishing state", True),
            ("Flat", "node stopped before verification", False),
        ):
            with self.subTest(mode=mode, ending=ending), tempfile.TemporaryDirectory() as tmp:
                log = Path(tmp) / "node.log"
                output = Path(tmp) / "metrics.json"
                log.write_text(log_line(0, "Collecting trie stats") + log_line(5, ending))
                env = dict(NETWORK="gnosis", SYNC_MODE=mode, DOCKER_IMAGE="test-image", LOG_FILE=str(log))
                with patch.dict(os.environ, env), patch("sys.argv", ["collect.py", str(output)]), redirect_stdout(io.StringIO()), redirect_stderr(io.StringIO()):
                    collect.main()
                record = json.loads(output.read_text())
                self.assertEqual(mode, record["mode"])
                self.assertEqual("test-image", record["image"])
                self.assertEqual(expect_verify, "verify_min" in record)
                self.assertNotIn("drain_min", record)

    def test_failed_docker_read_does_not_publish_a_record(self):
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "metrics.json"
            process = MagicMock()
            process.__enter__.return_value = process
            process.stdout = io.StringIO(log_line(0, "Starting snap sync."))
            process.returncode = 1
            with patch.dict(os.environ, {"NETWORK": "mainnet", "SYNC_MODE": "Flat"}, clear=True), patch("sys.argv", ["collect.py", str(output)]), patch.object(collect.subprocess, "Popen", return_value=process):
                with self.assertRaises(SystemExit):
                    collect.main()
            self.assertFalse(output.exists())


class PublishTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.metrics = Path(self.tmp.name) / "metrics"
        self.metrics.mkdir()
        self.history = Path(self.tmp.name) / "history.jsonl"
        self.record = {"network": "mainnet", "mode": "Flat", "snap_min": 10}
        (self.metrics / "mainnet.json").write_text(json.dumps(self.record))
        self.run = {"created_at": "2026-09-16T12:00:00Z", "event": "workflow_run", "head_sha": "a" * 40, "run_attempt": 2}

    def job(self, name, conclusion="success", end="2026-09-16T12:20:00Z"):
        return {"name": name, "conclusion": conclusion, "started_at": "2026-09-16T12:00:00Z", "completed_at": end}

    def execute(self, pages, trigger_sha=""):
        env = {"GITHUB_REPOSITORY": "owner/repo", "GITHUB_RUN_ID": "123", "GITHUB_RUN_ATTEMPT": "2", "TRIGGER_SHA": trigger_sha}
        with patch.dict(os.environ, env), patch("sys.argv", ["publish.py", str(self.metrics), str(self.history)]), patch.object(publish, "api", side_effect=[self.run, *pages]) as api, redirect_stdout(io.StringIO()):
            publish.main()
        return api.call_args_list

    def test_reusable_and_legacy_names_exclude_runner_lifecycle(self):
        for name in ("Sync mainnet (Flat)", "Sync mainnet (Flat) / sync"):
            with self.subTest(name=name):
                self.execute([{"jobs": [self.job(name), self.job("Sync mainnet (Flat) / destroy_runner", end="2026-09-16T12:59:00Z")]}])
                row = json.loads(self.history.read_text())
                self.assertEqual(20, row["job_min"])
                self.assertEqual(2, row["run_attempt"])

    def test_commit_is_the_triggering_sha_not_the_later_master_tip(self):
        self.execute([{"jobs": []}], trigger_sha="b" * 40)
        self.assertEqual("b" * 10, json.loads(self.history.read_text())["commit"])
        self.execute([{"jobs": []}])
        self.assertEqual("a" * 10, json.loads(self.history.read_text())["commit"])

    def test_job_api_is_paginated_and_scoped_to_current_attempt(self):
        calls = self.execute([
            {"jobs": [self.job("irrelevant") for _ in range(100)]},
            {"jobs": [self.job("Sync mainnet (Flat) / sync")]},
        ])
        self.assertIn("/attempts/2/jobs?per_page=100&page=1", calls[1].args[0])
        self.assertIn("page=2", calls[2].args[0])
        self.assertEqual(20, json.loads(self.history.read_text())["job_min"])

    def test_failed_incomplete_and_reversed_jobs_omit_job_duration(self):
        for job in (self.job("Sync mainnet (Flat) / sync", "failure"), self.job("Sync mainnet (Flat) / sync", end=None), self.job("Sync mainnet (Flat) / sync", end="2026-09-16T11:00:00Z")):
            with self.subTest(job=job):
                self.execute([{"jobs": [job]}])
                self.assertNotIn("job_min", json.loads(self.history.read_text()))

    def test_retry_replaces_only_matching_run_network_and_mode(self):
        retained = [{"run_id": 122, **self.record}, {"run_id": 123, **self.record, "mode": "HalfPath"}]
        old = {"run_id": 123, **self.record, "snap_min": 99}
        self.history.write_text("\n".join(json.dumps(row) for row in [*retained, old]) + "\n")
        self.execute([{"jobs": []}])
        once = self.history.read_text()
        self.execute([{"jobs": []}])
        self.assertEqual(once, self.history.read_text())
        rows = [json.loads(line) for line in once.splitlines()]
        self.assertEqual(retained, rows[:2])
        self.assertEqual(10, rows[2]["snap_min"])

    def test_empty_or_invalid_artifacts_leave_history_unchanged(self):
        for contents in (None, "invalid json"):
            with self.subTest(contents=contents):
                source = self.metrics / "mainnet.json"
                if contents is None:
                    source.unlink()
                else:
                    source.write_text(contents)
                self.history.write_text('{"run_id": 1}\n')
                if contents is None:
                    self.execute([{"jobs": []}])
                else:
                    with self.assertRaises(json.JSONDecodeError):
                        self.execute([{"jobs": []}])
                self.assertEqual('{"run_id": 1}\n', self.history.read_text())


@unittest.skipUnless(shutil.which("node"), "node is required to exercise dashboard JavaScript")
class DashboardTests(unittest.TestCase):
    def test_pinned_master_images_stay_in_default_trend(self):
        html = (SCRIPTS / "index.html").read_text()
        script = re.findall(r"<script>(.*?)</script>", html, re.S)[0].replace("init();", "")
        cases = [
            ({"event": "workflow_run", "image": image}, False)
            for image in ("nethermindeth/nethermind:master", "nethermindeth/nethermind:master-abcdef1", "nethermindeth/nethermind:master-" + "a" * 40)
        ] + [({}, False), ({"event": "workflow_dispatch"}, True), ({"image": "nethermindeth/nethermind:experiment"}, True)]
        script += "\nfor (const [row, expected] of " + json.dumps(cases) + ") { if (isExperimental(row) !== expected) throw new Error(JSON.stringify(row)); }"
        subprocess.run(["node", "-e", script], check=True, capture_output=True, text=True)


if __name__ == "__main__":
    unittest.main()
