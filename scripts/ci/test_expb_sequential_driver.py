#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression tests for the streaming EXPB campaign log parser."""

import contextlib
import io
import json
import os
import signal
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts" / "expb"))
import sequential_driver  # noqa: E402


class SequentialDriverParserTests(unittest.TestCase):
    def write_log(self, text):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        path = Path(directory.name) / "combined.log"
        path.write_text(text, encoding="utf-8")
        return path

    def test_collect_metrics_streams_one_file_and_preserves_health_signals(self):
        path = self.write_log(
            "\n".join(
                (
                    "\x1b[31m[payload-server] client_metric block_number=100 processing_ms=10.0\x1b[0m",
                    "[payload-server] client_metric block_number=101 processing_ms=14.0",
                    "| 100 | 200 | 11.0 |",
                    "| 101 | 200 | 13.0 |",
                    "[payload-server] warmup block=100 ok elapsed=1.0ms",
                    "[payload-server] warmup block=101 FAILED elapsed=1.0ms error=rpc",
                    "System.Exception: sample failure",
                    "Invalid Blocks: 101",
                    "ERROR: severe runtime signal",
                    "Nethermind is shut down",
                    "Cleanup completed",
                    "",
                )
            )
        )

        with patch.object(Path, "read_text", side_effect=AssertionError("whole log was read")):
            parsed, diagnostics = sequential_driver.collect_metrics(path)

        self.assertEqual("SSE", parsed["source"])
        self.assertEqual([100, 101], parsed["ids"])
        self.assertEqual([100, 101], parsed["payload_indices"])
        self.assertEqual(12.0, parsed["avg"])
        self.assertEqual([100], parsed["warm_ok"])
        self.assertEqual([101], parsed["warm_bad"])
        self.assertTrue(parsed["shutdown"])
        self.assertTrue(parsed["cleanup"])
        self.assertEqual((1, 1, 2), (parsed["exception_count"], parsed["invalid_count"], parsed["severe_count"]))
        self.assertEqual("System.Exception: sample failure", diagnostics["exceptions"][0])
        self.assertEqual("Invalid Blocks: 101", diagnostics["invalid"][0])

    def test_large_diagnostics_keep_counts_but_bound_retained_samples(self):
        long_exception = "Exception " + "x" * (sequential_driver.LIMIT * 4)
        long_invalid = "Invalid Block " + "y" * (sequential_driver.LIMIT * 4)
        long_error = "ERROR " + "z" * (sequential_driver.LIMIT * 4)
        path = self.write_log(
            "\n".join(
                [long_exception] * 100
                + [long_invalid] * 100
                + [long_error] * 100
                + ["Nethermind is shut down", "Cleanup completed", ""]
            )
        )

        parsed, diagnostics = sequential_driver.collect_metrics(path)

        self.assertEqual((100, 100, 100), (parsed["exception_count"], parsed["invalid_count"], parsed["severe_count"]))
        for samples in diagnostics.values():
            self.assertLessEqual(len(samples), sequential_driver.MAX_DIAGNOSTIC_LINES)
            self.assertTrue(all(len(sample) <= sequential_driver.LIMIT + 100 for sample in samples))

    def test_render_preserves_the_immutable_image_reference(self):
        image = "repo/image:tag@sha256:" + "a" * 64
        base = {"scenarios": {"nethermind": {"image": "mutable", "amount": 1}}}

        rendered, scenario = sequential_driver.render(base, {"id": "image-a", "image": image}, 1)

        self.assertEqual(image, rendered["scenarios"][scenario]["image"])

    def test_parse_pairs_validates_keys_and_preserves_equals_in_values(self):
        self.assertEqual({"ALPHA_1": "first=second", "_BETA": "value"}, sequential_driver.parse_pairs("ALPHA_1=first=second, _BETA=value"))
        for invalid in ("bad-key=value", "1BAD=value", "missing-separator"):
            with self.subTest(invalid=invalid), self.assertRaisesRegex(ValueError, "valid KEY=VALUE"):
                sequential_driver.parse_pairs(invalid)

    def test_render_rejects_non_positive_amount(self):
        base = {"scenarios": {"nethermind": {"amount": 1}}}
        image = {"id": "image-a", "image": "repo/image:tag@sha256:" + "a" * 64}
        for amount in ("0", "-1", "1.5"):
            with self.subTest(amount=amount), patch.dict(os.environ, {"AMOUNT": amount}, clear=False), self.assertRaisesRegex(ValueError, "positive integer"):
                sequential_driver.render(base, image, 1)

    def test_verify_clean_prunes_exited_expb_containers_but_rejects_running_ones(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = {"paths": {"work": "work"}}
            commands = []

            def docker_output(command, **_kwargs):
                commands.append(command)
                if command[1:3] == ["container", "prune"]:
                    return "deleted-container\n"
                return ""

            with patch.dict(os.environ, {"EXPB_DATA_DIR": str(root), "DOCKER_BIN": "docker"}, clear=False), patch.object(
                sequential_driver.subprocess, "check_output", side_effect=docker_output
            ), patch.object(Path, "read_text", return_value=""):
                sequential_driver.verify_clean(config)

            self.assertEqual(["docker", "container", "prune", "--force", "--filter", "label=expb"], commands[0])
            self.assertIn(["docker", "container", "ps", "-q", "--filter", "label=expb"], commands)

            def running_output(command, **_kwargs):
                if command[1:3] == ["container", "prune"]:
                    return ""
                if command[1:3] == ["container", "ps"]:
                    return "running-id\n"
                return ""

            with patch.dict(os.environ, {"EXPB_DATA_DIR": str(root), "DOCKER_BIN": "docker"}, clear=False), patch.object(
                sequential_driver.subprocess, "check_output", side_effect=running_output
            ), patch.object(Path, "read_text", return_value=""), self.assertRaisesRegex(RuntimeError, "benchmark containers remain"):
                sequential_driver.verify_clean(config)

    def test_signal_cleanup_ignores_process_exit_races(self):
        process = _RunningProcess()
        sequential_driver.current = process
        sequential_driver.watchdog = None
        with patch.object(sequential_driver.signal, "SIGKILL", 9, create=True), patch.object(
            sequential_driver.os, "killpg", side_effect=ProcessLookupError, create=True
        ):
            sequential_driver.force(process)
            sequential_driver.stop(signal.SIGTERM, None)
        self.assertIsNone(sequential_driver.watchdog)

    def test_summary_preserves_image_order_and_only_compares_compatible_complete_images(self):
        images = [
            {"id": "image-z", "image": "repo/z"},
            {"id": "image-a", "image": "repo/a"},
            {"id": "image-m", "image": "repo/m"},
            {"id": "image-f", "image": "repo/f"},
        ]
        samples = [
            self.sample("image-z", 100.0, run=1),
            self.sample("image-z", 100.0, run=2),
            self.sample("image-a", 110.0, run=1),
            self.sample("image-a", 110.0, run=2),
            self.sample("image-m", 120.0, run=1, ids=(200, 201)),
            self.sample("image-m", 121.0, run=2, ids=(100, 101)),
            self.sample("image-f", 130.0, run=1),
            self.sample("image-f", None, run=2, status="failed"),
        ]
        with tempfile.TemporaryDirectory() as directory:
            sequential_driver.write_summary(Path(directory), images, 2, samples)
            summary = (Path(directory) / "summary.md").read_text(encoding="utf-8")

        image_lines = [line for line in summary.splitlines() if line.startswith("Image ")]
        self.assertEqual(["image-z", "image-a", "image-m", "image-f"], [line.split()[1].rstrip(":") for line in image_lines])
        self.assertIn("Image image-a: mean AVG=110.0 ms; CV=0.00%; delta vs image-z=+10.00%", summary)
        self.assertIn("Image image-m: mean AVG=n/a ms; CV=unavailable; delta vs image-z=n/a", summary)
        self.assertIn("Image image-f: mean AVG=130.0 ms; CV=unavailable; delta vs image-z=n/a", summary)

    @staticmethod
    def sample(image_id, average, run, ids=(100, 101), status="success"):
        return {
            "sample_id": f"{image_id}-run{run}",
            "image_id": image_id,
            "status": status,
            "metrics": {"source": "SSE", "count": 2, "delivered": 2, "ids": list(ids), "avg": average},
            "sse_block_ids": list(ids),
        }

    def test_run_sample_compute_warm_requires_every_payload_to_warm_successfully(self):
        base = {"scenarios": {"nethermind": {"amount": 2}}}
        image = {"id": "image-a", "image": "repo/image:tag@sha256:" + "a" * 64}
        log_by_case = {
            "success": (
                "[payload-server] client_metric block_number=100 processing_ms=10.0\n"
                "[payload-server] client_metric block_number=101 processing_ms=14.0\n"
                "| 100 | 200 | 11.0 |\n"
                "| 101 | 200 | 13.0 |\n"
                "[payload-server] warmup block=100 ok\n"
                "[payload-server] warmup block=101 ok\n"
                "Nethermind is shut down\nCleanup completed\n"
            ),
            "missing": (
                "[payload-server] client_metric block_number=100 processing_ms=10.0\n"
                "[payload-server] client_metric block_number=101 processing_ms=14.0\n"
                "| 100 | 200 | 11.0 |\n"
                "| 101 | 200 | 13.0 |\n"
                "[payload-server] warmup block=100 ok\n"
                "Nethermind is shut down\nCleanup completed\n"
            ),
            "failed": (
                "[payload-server] client_metric block_number=100 processing_ms=10.0\n"
                "[payload-server] client_metric block_number=101 processing_ms=14.0\n"
                "| 100 | 200 | 11.0 |\n"
                "| 101 | 200 | 13.0 |\n"
                "[payload-server] warmup block=100 ok\n"
                "[payload-server] warmup block=101 FAILED\n"
                "Nethermind is shut down\nCleanup completed\n"
            ),
        }

        for case, expected_status in (("success", "success"), ("missing", "failed"), ("failed", "failed")):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                data_dir = root / "data"
                data_dir.mkdir()

                def fake_popen(*_args, **kwargs):
                    kwargs["stdout"].write(log_by_case[case].encode("utf-8"))
                    kwargs["stdout"].flush()
                    return _CompletedProcess()

                sequential_driver.current = None
                sequential_driver.cancelled = False
                sequential_driver.watchdog = None
                environment = {
                    "EXPB_DATA_DIR": str(data_dir),
                    "MEASUREMENT_MODE": "compute-warm",
                    "AMOUNT": "2",
                    "DELAY_SECONDS": "0",
                    "EXPB_ENV_PASSTHROUGH": "",
                    "ADDITIONAL_EXTRA_FLAGS": "",
                    "CLIENT_ENV": "",
                    "TRACE_BLOCKS": "",
                    "DOTTRACE": "false",
                    "PERF": "false",
                }
                with patch.dict(os.environ, environment, clear=False), patch.object(
                    sequential_driver.subprocess, "Popen", side_effect=fake_popen
                ), patch.object(sequential_driver, "verify_clean") as verify_clean, contextlib.redirect_stdout(
                    io.StringIO()
                ), contextlib.redirect_stderr(io.StringIO()), patch.object(
                    sequential_driver.platform, "machine", return_value="test"
                ), patch.object(sequential_driver.socket, "gethostname", return_value="test-host"):
                    result = sequential_driver.run_sample(base, image, 1, root)

                self.assertEqual(expected_status, result["status"])
                self.assertEqual([100, 101], result["metrics"]["payload_indices"])
                self.assertEqual(
                    case != "success",
                    "compute-warm warmup is missing or failed" in result["failure_reasons"],
                )
                verify_clean.assert_called_once()

    def test_run_sample_keeps_export_credentials_out_of_success_failure_and_cancel_artifacts(self):
        sentinel = "credential-sentinel-for-expb-test"
        base = {
            "export": {"prometheus_remote_write": {"basic_auth": {"password": sentinel}}},
            "scenarios": {"nethermind": {"amount": 1}},
        }
        image = {"id": "image-a", "image": "repo/image:tag@sha256:" + "a" * 64}
        log = (
            "[payload-server] client_metric block_number=100 processing_ms=10.0\n"
            "| 100 | 200 | 11.0 |\n"
            "Nethermind is shut down\nCleanup completed\n"
        ).encode("utf-8")

        for case, exit_code, cancellation_requested in (
            ("success", 0, False),
            ("failure", 7, False),
            ("cancelled", 143, True),
        ):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory) / "campaign"
                root.mkdir()
                data_dir = Path(directory) / "data"
                data_dir.mkdir()
                observed = {}
                runtime_paths = []

                class FakeChild:
                    pid = 1234

                    def poll(self):
                        return None if cancellation_requested else 0

                    def wait(self):
                        return exit_code

                def fake_popen(command, **kwargs):
                    runtime_path = Path(command[command.index("--config-file") + 1])
                    runtime_paths.append(runtime_path)
                    observed["config"] = json.loads(runtime_path.read_text(encoding="utf-8"))
                    kwargs["stdout"].write(log)
                    kwargs["stdout"].flush()
                    return FakeChild()

                environment = {
                    "EXPB_DATA_DIR": str(data_dir),
                    "MEASUREMENT_MODE": "standard",
                    "AMOUNT": "1",
                    "DELAY_SECONDS": "0",
                    "EXPB_ENV_PASSTHROUGH": "",
                    "ADDITIONAL_EXTRA_FLAGS": "",
                    "CLIENT_ENV": "",
                    "TRACE_BLOCKS": "",
                    "DOTTRACE": "false",
                    "PERF": "false",
                }
                with patch.object(sequential_driver, "current", None), patch.object(
                    sequential_driver, "cancelled", cancellation_requested
                ), patch.object(sequential_driver, "watchdog", None), patch.dict(
                    os.environ, environment, clear=False
                ), patch.object(
                    sequential_driver.subprocess, "Popen", side_effect=fake_popen
                ), patch.object(sequential_driver, "verify_clean"), patch.object(
                    sequential_driver.os, "killpg", create=True
                ) as killpg, contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                    result = sequential_driver.run_sample(base, image, 1, root)

                self.assertEqual(case == "success", result["status"] == "success")
                self.assertEqual(
                    sentinel,
                    observed["config"]["export"]["prometheus_remote_write"]["basic_auth"]["password"],
                )
                self.assertEqual(1, len(runtime_paths))
                self.assertNotIn(root, runtime_paths[0].parents)
                self.assertFalse(runtime_paths[0].exists())
                artifact_config = json.loads((root / "image-a-run1" / "config.json").read_text(encoding="utf-8"))
                self.assertNotIn("export", artifact_config)
                artifact_files = [path for path in root.rglob("*") if path.is_file()]
                self.assertTrue(artifact_files)
                self.assertTrue(
                    all(sentinel not in path.read_text(encoding="utf-8", errors="replace") for path in artifact_files)
                )
                if cancellation_requested:
                    killpg.assert_called_once()
                else:
                    killpg.assert_not_called()


class _CompletedProcess:
    pid = 0

    def poll(self):
        return 0

    def wait(self):
        return 0


class _RunningProcess(_CompletedProcess):
    def poll(self):
        return None

if __name__ == "__main__":  # pragma: no cover
    unittest.main()
