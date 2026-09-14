#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression tests for the streaming EXPB campaign log parser."""

import contextlib
import io
import os
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


class _CompletedProcess:
    pid = 0

    def poll(self):
        return 0

    def wait(self):
        return 0

if __name__ == "__main__":  # pragma: no cover
    unittest.main()
