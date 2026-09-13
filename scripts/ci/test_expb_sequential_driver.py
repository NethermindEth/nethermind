#!/usr/bin/env python3
"""Pure regression tests for the EXPB sequential campaign driver."""

import contextlib
import io
import json
import sys
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path
from unittest.mock import Mock, patch


REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts" / "expb"))
import sequential_driver  # noqa: E402


class ExpbSequentialDriverTests(unittest.TestCase):
    @staticmethod
    def campaign_args(root: Path, *, amount: str = "1", measurement_mode: str = "standard") -> Namespace:
        config = root / "config.yaml"
        config.write_text("paths:\n  work: " + str(root / "work") + "\n")
        return Namespace(
            images_json='[{"id":"image-a","image":"repo:image-a"},{"id":"image-b","image":"repo:image-b"}]',
            config_template=str(config),
            output_dir=str(root / "campaign"),
            expb_data_dir=str(root / "data"),
            flat_snapshot_dir=str(root / "snapshot"),
            flat_snapshot_block_dir=str(root / "snapshot-block"),
            delay_seconds="0",
            amount=amount,
            additional_extra_flags="",
            client_env="",
            trace_blocks="",
            expb_env="",
            expb_bin="expb",
            yq_bin="yq",
            docker_bin="docker",
            run_count=1,
            cleanup_grace_seconds=90,
            dottrace=False,
            dottrace_mode="sampling",
            perf=False,
            measurement_mode=measurement_mode,
        )

    def test_flags_support_comma_and_two_line_forms(self):
        self.assertEqual(
            ["--Merge.SweepMemory=NoGC", "--Pruning.CacheMb=12000", "--Sync.FastSync=false"],
            sequential_driver.parse_flags("--Merge.SweepMemory NoGC,\n--Pruning.CacheMb=12000\n--Sync.FastSync\nfalse"),
        )

    def test_parse_output_keeps_sse_block_identity_and_ttfb_secondary(self):
        output = """
        [payload-server] client_metric block_number=10 processing_ms=12.5
        [payload-server] client_metric block_number=11 processing_ms=7.5
        | 10 | 100 | 15.0 |
        | 11 | 100 | 9.0 |
        Cleanup completed
        """
        metrics, exceptions, invalid, blocks = sequential_driver.parse_output(output)
        self.assertEqual("SSE", metrics["SOURCE"])
        self.assertEqual("2", metrics["COUNT"])
        self.assertEqual("10.00", metrics["AVG"])
        self.assertEqual("2", metrics["TTFB_COUNT"])
        self.assertEqual([10, 11], [item["block_number"] for item in blocks])
        self.assertEqual([], exceptions)
        self.assertEqual([], invalid)

    def test_parse_output_uses_ttfb_when_sse_is_absent(self):
        metrics, _, _, _ = sequential_driver.parse_output("| 10 | 100 | 15.0 |\n| 11 | 100 | 9.0 |\n")
        self.assertEqual("TTFB", metrics["SOURCE"])
        self.assertEqual("2", metrics["COUNT"])
        self.assertEqual("12.00", metrics["AVG"])
        self.assertEqual(
            [{"payload_index": 10, "ttfb_ms": 15.0}, {"payload_index": 11, "ttfb_ms": 9.0}],
            sequential_driver.parse_k6_rows("| 10 | 100 | 15.0 |\n| 11 | 100 | 9.0 |\n"),
        )

    def test_warmup_parser_requires_payload_server_success_marker(self):
        successful, failed = sequential_driver.parse_warmup_records(
            "warmup configured\n"
            "[payload-server] warmup block=10 ok elapsed=1.2ms\n"
            "[payload-server] warmup block=11 FAILED elapsed=2.3ms error=timeout\n"
        )
        self.assertEqual([10], successful)
        self.assertEqual([11], failed)

    def test_expected_amount_is_resolved_from_config_when_cli_is_blank(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = self.campaign_args(root, amount="")
            config = Path(args.config_template)
            config.write_text(config.read_text() + "scenarios:\n  nethermind:\n    amount: 7\n")
            campaign = sequential_driver.Campaign(args)
            yq_result = Mock(returncode=0, stdout="7\n", stderr="")
            with patch.object(sequential_driver.subprocess, "run", return_value=yq_result) as run:
                amount = campaign.resolve_expected_amount(config, "nethermind")

        self.assertEqual(7, amount)
        self.assertEqual("nethermind", run.call_args.kwargs["env"]["SK"])

    def test_cv_is_unavailable_for_one_run(self):
        self.assertEqual("unavailable", sequential_driver.coefficient_of_variation([10]))
        self.assertEqual("14.14%", sequential_driver.coefficient_of_variation([90, 110]))

    def test_image_ids_are_required_to_be_unique_and_shell_safe(self):
        plan = json.dumps(
            [
                {"id": "image-0-abc123", "image": "repo:first", "tag": "first"},
                {"id": "image-1-abc123", "image": "other:first", "tag": "first"},
            ]
        )
        self.assertEqual(2, len(sequential_driver.load_image_plan(plan)))
        with self.assertRaises(ValueError):
            sequential_driver.load_image_plan('[{"id":"same","image":"a"},{"id":"same","image":"b"}]')

    def test_snapshot_scratch_must_be_empty_even_without_mounts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / "config.yaml"
            work = root / "scratch"
            config.write_text(f"paths:\n  work: {work}\n")
            for leaf in ("upper", "work", "merged"):
                (work / leaf).mkdir(parents=True)

            yq_result = Mock(returncode=0, stdout=json.dumps([True, str(work)]), stderr="")
            with patch.object(sequential_driver.subprocess, "run", return_value=yq_result):
                self.assertEqual((True, []), sequential_driver.verify_snapshot_scratch(config, root))
            (work / "upper" / "leftover").write_text("cancelled run")
            with patch.object(sequential_driver.subprocess, "run", return_value=yq_result):
                clean, problems = sequential_driver.verify_snapshot_scratch(config, root)

        self.assertFalse(clean)
        self.assertTrue(any("upper" in problem and "not empty" in problem for problem in problems))

    def test_snapshot_scratch_parser_failure_fails_closed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / "config.yaml"
            config.write_text("paths: {work: /outside/scratch}\n")
            yq_result = Mock(returncode=2, stdout="", stderr="bad yaml")
            with patch.object(sequential_driver.subprocess, "run", return_value=yq_result):
                clean, problems = sequential_driver.verify_snapshot_scratch(config, root)

        self.assertFalse(clean)
        self.assertIn("cannot parse rendered config with yq", problems[0])

    def test_campaign_refuses_leftover_scratch_before_first_sample(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config = root / "config.yaml"
            work = root / "scratch"
            config.write_text(f"paths:\n  work: {work}\n")
            (work / "upper").mkdir(parents=True)
            (work / "upper" / "leftover").write_text("cancelled run")
            args = Namespace(
                images_json='[{"id":"image-a","image":"repo:image-a"}]',
                config_template=str(config),
                output_dir=str(root / "campaign"),
                expb_data_dir=str(root / "data"),
                flat_snapshot_dir=str(root / "snapshot"),
                flat_snapshot_block_dir=str(root / "snapshot-block"),
                delay_seconds="0",
                amount="1",
                additional_extra_flags="",
                client_env="",
                trace_blocks="",
                expb_env="",
                expb_bin="expb",
                yq_bin="yq",
                docker_bin="docker",
                run_count=1,
                cleanup_grace_seconds=90,
                dottrace=False,
                dottrace_mode="sampling",
                perf=False,
                measurement_mode="standard",
            )
            yq_result = Mock(returncode=0, stdout=json.dumps([True, str(work)]), stderr="")
            campaign = sequential_driver.Campaign(args)
            with patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])), patch.object(
                sequential_driver.subprocess, "run", return_value=yq_result
            ), patch.object(sequential_driver.subprocess, "Popen") as popen:
                result = campaign.run()

        self.assertEqual(1, result)
        self.assertTrue(campaign.aborted)
        self.assertEqual([], campaign.samples)
        popen.assert_not_called()

    def test_termination_forwards_signal_without_waiting_in_handler(self):
        campaign = Mock()
        campaign.current_process = Mock()
        campaign.current_process.pid = 123
        campaign.current_process.poll.return_value = None
        campaign.args.cleanup_grace_seconds = 90
        campaign.cancel_requested = False
        campaign.aborted = False
        timer = Mock()
        with patch.object(sequential_driver.os, "killpg", create=True) as killpg, patch.object(
            sequential_driver.threading, "Timer", return_value=timer
        ) as timer_factory:
            sequential_driver.Campaign.terminate(campaign, sequential_driver.signal.SIGTERM, None)

        killpg.assert_called_once_with(123, sequential_driver.signal.SIGTERM)
        timer_factory.assert_called_once()
        timer.start.assert_called_once_with()
        self.assertTrue(campaign.cancel_requested)
        self.assertTrue(campaign.aborted)

    def test_force_termination_kills_owned_process_group_even_after_parent_exit(self):
        campaign = Mock()
        process = Mock()
        process.pid = 123
        campaign.current_process = process
        with patch.object(sequential_driver.os, "killpg", create=True) as killpg:
            sequential_driver.Campaign._force_termination(campaign, process)

        if hasattr(sequential_driver.signal, "SIGKILL"):
            killpg.assert_called_once_with(123, sequential_driver.signal.SIGKILL)
        else:
            process.kill.assert_called_once_with()

    def test_summary_refuses_cv_when_ordered_sse_blocks_differ(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = Namespace(
                images_json='[{"id":"image-a","image":"repo:image-a"}]',
                output_dir=str(root / "campaign"),
                run_count=2,
                measurement_mode="standard",
            )
            campaign = sequential_driver.Campaign.__new__(sequential_driver.Campaign)
            campaign.args = args
            campaign.output_dir = root / "campaign"
            campaign.output_dir.mkdir()
            campaign.aborted = False
            campaign.failures = 0
            campaign.preflight = None
            campaign.samples = [
                {
                    "image_id": "image-a",
                    "run": 1,
                    "status": "success",
                    "metrics_source": "SSE",
                    "sse_block_ids": [10, 11],
                    "metrics": {"SOURCE": "SSE", "COUNT": "2", "AVG": "10.00", "AVG_EXACT": "10"},
                },
                {
                    "image_id": "image-a",
                    "run": 2,
                    "status": "success",
                    "metrics_source": "SSE",
                    "sse_block_ids": [10, 12],
                    "metrics": {"SOURCE": "SSE", "COUNT": "2", "AVG": "12.00", "AVG_EXACT": "12"},
                },
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        cv_cell = next(line for line in summary.splitlines() if line.startswith("| 1 |"))
        self.assertIn("mismatched block ids", cv_cell)
        aggregate_row = next(line for line in summary.splitlines() if line.startswith("| `repo:image-a`"))
        self.assertIn("| 2 | n/a | n/a |", aggregate_row)

    def test_summary_suppresses_mean_for_mixed_source_or_count(self):
        def record(image_id: str, source: str, count: str, average: str) -> dict:
            return {
                "image_id": image_id,
                "run": 1,
                "status": "success",
                "metrics": {
                    "SOURCE": source,
                    "COUNT": count,
                    "AVG": average,
                    "AVG_EXACT": average,
                },
                "sse_block_ids": [10, 11] if source == "SSE" else [],
            }

        cases = (
            ("mixed source", [record("image-a", "SSE", "2", "10"), record("image-a", "TTFB", "2", "12")]),
            ("mismatched count", [record("image-a", "SSE", "2", "10"), record("image-a", "SSE", "3", "12")]),
        )
        for name, samples in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args = Namespace(
                    images_json='[{"id":"image-a","image":"repo:image-a"}]',
                    output_dir=str(root / "campaign"),
                    run_count=2,
                    measurement_mode="standard",
                )
                campaign = sequential_driver.Campaign.__new__(sequential_driver.Campaign)
                campaign.args = args
                campaign.output_dir = root / "campaign"
                campaign.output_dir.mkdir()
                campaign.aborted = False
                campaign.failures = 0
                campaign.preflight = None
                campaign.samples = samples
                campaign.write_summary()
                summary = (campaign.output_dir / "summary.md").read_text()
                aggregate_row = next(line for line in summary.splitlines() if line.startswith("| `repo:image-a`"))
                self.assertIn("| 2 | n/a | n/a |", aggregate_row)

    def test_summary_aggregates_successes_and_compares_comparable_images(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = Namespace(
                images_json='[{"id":"image-a","image":"repo:a","tag":"a"},{"id":"image-b","image":"repo:b","tag":"b"}]',
                output_dir=str(root / "campaign"),
                run_count=2,
                measurement_mode="standard",
            )
            campaign = sequential_driver.Campaign.__new__(sequential_driver.Campaign)
            campaign.args = args
            campaign.output_dir = root / "campaign"
            campaign.output_dir.mkdir()
            campaign.aborted = False
            campaign.failures = 2
            campaign.preflight = None
            def record(image_id: str, run: int, status: str, average: str) -> dict:
                return {
                    "image_id": image_id,
                    "run": run,
                    "status": status,
                    "started_at": "2026-09-13T00:00:00Z",
                    "metrics_source": "SSE",
                    "sse_block_ids": [100, 101],
                    "metrics": {
                        "SOURCE": "SSE",
                        "COUNT": "2",
                        "AVG": average,
                        "AVG_EXACT": average,
                        "MEDIAN": average,
                        "P95": average,
                    },
                }
            campaign.samples = [
                record("image-a", 1, "success", "10.00"),
                record("image-a", 2, "failed", "99.00"),
                record("image-b", 1, "success", "12.00"),
                record("image-b", 2, "failed", "98.00"),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        self.assertIn("| `a` (image-a) | 1 | 10.00 | +0.00% |", summary)
        self.assertIn("| `b` (image-b) | 1 | 12.00 | +20.00% |", summary)
        failed_row = next(line for line in summary.splitlines() if "| `a` (image-a) | 2 |" in line)
        self.assertIn("| n/a | n/a |", failed_row)

    def test_delivery_count_gate_uses_k6_rows_when_sse_is_shorter(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, amount="2"))
            process = Mock()
            process.stdout = iter(
                (
                    "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    "| 10 | 100 | 10.0 |\n"
                    "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            with (
                patch.object(campaign, "render_config", side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario"),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

        self.assertEqual(1, sample["delivered_count"])
        self.assertEqual(2, sample["expected_amount"])
        self.assertFalse(sample["delivery_count_valid"])
        self.assertEqual("failed", sample["status"])

    def test_partial_sse_coverage_fails_without_replacing_the_recorded_source(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, amount="3"))
            process = Mock()
            process.stdout = iter(
                (
                    "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    "| 10 | 100 | 10.0 |\n"
                    "| 11 | 100 | 10.0 |\n"
                    "| 12 | 100 | 10.0 |\n"
                    "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            with (
                patch.object(campaign, "render_config", side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario"),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

        self.assertEqual("SSE", sample["metrics_source"])
        self.assertEqual(1, sample["metrics_count"])
        self.assertEqual(3, sample["delivered_count"])
        self.assertFalse(sample["sse_coverage_valid"])
        self.assertEqual("failed", sample["status"])

    def test_console_output_is_bounded_while_combined_log_keeps_full_line(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, amount="1"))
            long_line = "X" * (sequential_driver.CONSOLE_LINE_LIMIT + 904) + "\n"
            process = Mock()
            process.stdout = iter(
                (
                    long_line
                    + "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    + "| 10 | 100 | 10.0 |\n"
                    + "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            stdout = io.StringIO()
            stderr = io.StringIO()
            with (
                patch.object(
                    campaign,
                    "render_config",
                    side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario",
                ),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                contextlib.redirect_stdout(stdout),
                contextlib.redirect_stderr(stderr),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

            raw_log = Path(sample["log"]).read_text(encoding="utf-8")

        self.assertEqual("success", sample["status"])
        self.assertIn("X" * (sequential_driver.CONSOLE_LINE_LIMIT + 904), raw_log)
        console = stdout.getvalue() + stderr.getvalue()
        self.assertIn("X" * sequential_driver.CONSOLE_LINE_LIMIT, console)
        self.assertIn(sequential_driver.CONSOLE_TRUNCATION, console)
        self.assertNotIn("X" * (sequential_driver.CONSOLE_LINE_LIMIT + 1), console)

    def test_compute_warm_failure_skips_remaining_repetitions_for_that_image(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = self.campaign_args(root, measurement_mode="compute-warm")
            args.images_json = (
                '[{"id":"image-a","image":"repo:image-a"},'
                '{"id":"image-b","image":"repo:image-b"}]'
            )
            args.run_count = 3
            campaign = sequential_driver.Campaign(args)
            calls: list[tuple[str, int]] = []

            def sample(image: dict[str, str], run: int) -> dict:
                calls.append((image["id"], run))
                if image["id"] == "image-a":
                    campaign.failures += 1
                    return {
                        "sample_id": f"{image['id']}-run{run}",
                        "image_id": image["id"],
                        "run": run,
                        "started_at": "2026-09-13T00:00:00Z",
                        "status": "failed",
                        "warmup_status": "missing",
                        "cleanup_verified": True,
                        "failure_reasons": ["compute-warm warmup is missing"],
                        "metrics": {},
                    }
                return {
                    "sample_id": f"{image['id']}-run{run}",
                    "image_id": image["id"],
                    "run": run,
                    "started_at": "2026-09-13T00:00:00Z",
                    "status": "success",
                    "metrics_source": "TTFB",
                    "sse_block_ids": [],
                    "metrics": {"SOURCE": "TTFB", "COUNT": "1", "AVG": "1", "AVG_EXACT": "1"},
                }

            with (
                patch.object(campaign, "run_sample", side_effect=sample),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                patch.object(sequential_driver.signal, "signal"),
                contextlib.redirect_stdout(io.StringIO()),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                result = campaign.run()

        self.assertEqual(1, result)
        self.assertEqual(
            [("image-a", 1), ("image-b", 1), ("image-b", 2), ("image-b", 3)],
            calls,
        )
        self.assertEqual(
            ["image-a-run1", "image-a-run2", "image-a-run3", "image-b-run1", "image-b-run2", "image-b-run3"],
            [sample["sample_id"] for sample in campaign.samples],
        )
        self.assertEqual(["skipped", "skipped"], [sample["status"] for sample in campaign.samples[1:3]])

    def test_compute_warm_requires_concrete_success_for_each_delivered_row(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, measurement_mode="compute-warm"))
            process = Mock()
            process.stdout = iter(
                (
                    "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    "| 10 | 100 | 10.0 |\n"
                    "warmup configured\n"
                    "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            with (
                patch.object(campaign, "render_config", side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario"),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

        self.assertEqual("missing", sample["warmup_status"])
        self.assertEqual("failed", sample["status"])

    def test_compute_warm_passes_with_one_success_per_delivered_row(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, measurement_mode="compute-warm"))
            process = Mock()
            process.stdout = iter(
                (
                    "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    "| 10 | 100 | 10.0 |\n"
                    "warmup configured\n"
                    "[payload-server] warmup block=10 ok elapsed=1.2ms\n"
                    "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            with (
                patch.object(campaign, "render_config", side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario"),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

        self.assertEqual("ok", sample["warmup_status"])
        self.assertEqual("success", sample["status"])

    def test_compute_warm_rejects_success_for_the_wrong_payload_index(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(self.campaign_args(root, measurement_mode="compute-warm"))
            process = Mock()
            process.stdout = iter(
                (
                    "[payload-server] client_metric block_number=10 processing_ms=10\n"
                    "| 10 | 100 | 10.0 |\n"
                    "[payload-server] warmup block=9 ok elapsed=1.2ms\n"
                    "Nethermind is shut down\nCleanup completed\n"
                ).splitlines(keepends=True)
            )
            process.wait.return_value = 0
            process.poll.return_value = 0
            with (
                patch.object(campaign, "render_config", side_effect=lambda _image, _run, path: path.write_text("scenario: test\n") or "scenario"),
                patch.object(sequential_driver.subprocess, "Popen", return_value=process),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            ):
                sample = campaign.run_sample(
                    {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"}, 1
                )

        self.assertEqual("missing", sample["warmup_status"])
        self.assertEqual("failed", sample["status"])

    def test_cancellation_between_samples_stops_before_next_executor(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            args = self.campaign_args(root)
            campaign = sequential_driver.Campaign(args)
            calls: list[tuple[str, int]] = []
            manifests = 0

            def sample(image: dict[str, str], run: int) -> dict:
                calls.append((image["id"], run))
                return {
                    "sample_id": f"{image['id']}-run{run}",
                    "image_id": image["id"],
                    "run": run,
                    "started_at": "2026-09-13T00:00:00Z",
                    "status": "success",
                    "metrics_source": "TTFB",
                    "metrics": {"SOURCE": "TTFB", "COUNT": "1", "AVG": "1", "AVG_EXACT": "1"},
                }

            def manifest() -> None:
                nonlocal manifests
                manifests += 1
                if manifests == 3:
                    campaign.cancel_requested = True

            with (
                patch.object(campaign, "run_sample", side_effect=sample),
                patch.object(campaign, "write_manifest", side_effect=manifest),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                patch.object(sequential_driver.signal, "signal"),
                patch.object(sequential_driver, "utc_now", return_value="2026-09-13T00:00:00Z"),
            ):
                result = campaign.run()

        self.assertEqual(1, result)
        self.assertEqual([("image-a", 1)], calls)
        self.assertTrue(campaign.cancel_requested)


if __name__ == "__main__":
    unittest.main()
