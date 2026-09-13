#!/usr/bin/env python3
"""Focused regression tests for the EXPB sequential campaign driver."""

from argparse import Namespace
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch


REPO = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(REPO / "scripts" / "expb"))
import sequential_driver  # noqa: E402


class FakeProcess:
    def __init__(self, output: str, returncode: int = 0):
        self.stdout = iter(output.splitlines(keepends=True))
        self.returncode = returncode
        self.pid = 1234

    def wait(self, timeout: float | None = None) -> int:
        return self.returncode

    def poll(self) -> int:
        return self.returncode


class ExpbSequentialDriverTests(unittest.TestCase):
    @staticmethod
    def image(image_id: str = "image-a", reference: str | None = None) -> dict[str, str]:
        return {
            "id": image_id,
            "image": reference or f"repo:{image_id}",
            "tag": image_id,
            "date": "2026-09-13",
        }

    @classmethod
    def args(
        cls,
        root: Path,
        *,
        images: list[dict[str, str]] | None = None,
        amount: int = 1,
        run_count: int = 1,
        measurement_mode: str = "standard",
    ) -> Namespace:
        config = root / "config.yaml"
        config.write_text(
            f"paths:\n  work: {root / 'work'}\n"
            "scenarios:\n  nethermind:\n    amount: 1\n"
        )
        return Namespace(
            images_json=json.dumps(images or [cls.image()]),
            config_template=str(config),
            output_dir=str(root / "campaign"),
            expb_data_dir=str(root / "data"),
            flat_snapshot_dir=str(root / "snapshot"),
            flat_snapshot_block_dir=str(root / "snapshot-block"),
            delay_seconds="0",
            amount=str(amount),
            additional_extra_flags="",
            client_env="",
            trace_blocks="",
            expb_env="",
            expb_bin="expb",
            yq_bin="yq",
            docker_bin="docker",
            run_count=run_count,
            cleanup_grace_seconds=90,
            dottrace=False,
            dottrace_mode="sampling",
            perf=False,
            measurement_mode=measurement_mode,
        )

    @staticmethod
    def log(
        delivered: tuple[int, ...] = (10,),
        sse: tuple[int, ...] | None = None,
        warmup_ok: tuple[int, ...] = (),
        warmup_failed: tuple[int, ...] = (),
        shutdown: bool = True,
        cleanup: bool = True,
        extra: tuple[str, ...] = (),
    ) -> str:
        lines = [
            f"[payload-server] client_metric block_number={block} processing_ms=10.0"
            for block in (sse if sse is not None else delivered)
        ]
        lines.extend(f"| {index} | 100 | 10.0 |" for index in delivered)
        lines.extend(
            f"[payload-server] warmup block={block} ok elapsed=1.0ms"
            for block in warmup_ok
        )
        lines.extend(
            f"[payload-server] warmup block={block} FAILED elapsed=1.0ms error=rpc"
            for block in warmup_failed
        )
        lines.extend(extra)
        if shutdown:
            lines.append("Nethermind is shut down")
        if cleanup:
            lines.append("Cleanup completed")
        return "\n".join(lines) + "\n"


    @classmethod
    def run_sample(
        cls,
        root: Path,
        *,
        output: str,
        amount: int = 1,
        returncode: int = 0,
        cleanup: tuple[bool, list[str]] = (True, []),
        scratch: tuple[bool, list[str]] = (True, []),
        measurement_mode: str = "standard",
        capture_console: bool = True,
    ) -> tuple[dict, Mock]:
        campaign = sequential_driver.Campaign(
            cls.args(root, amount=amount, measurement_mode=measurement_mode)
        )
        popen = Mock(return_value=FakeProcess(output, returncode))
        patches = (
            patch.object(campaign, "render_config", return_value="scenario"),
            patch.object(sequential_driver.subprocess, "Popen", popen),
            patch.object(sequential_driver, "verify_cleanup", return_value=cleanup),
            patch.object(sequential_driver, "verify_snapshot_scratch", return_value=scratch),
        )
        with contextlib.ExitStack() as stack:
            for item in patches:
                stack.enter_context(item)
            if capture_console:
                stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                stack.enter_context(contextlib.redirect_stderr(io.StringIO()))
            sample = campaign.run_sample(cls.image(), 1)
        return sample, popen

    @classmethod
    def record(
        cls,
        image_id: str,
        run: int,
        *,
        status: str = "success",
        source: str = "SSE",
        count: str = "2",
        average: str = "10",
        blocks: tuple[int, ...] = (100, 101),
    ) -> dict:
        return {
            "sample_id": f"{image_id}-run{run}",
            "image_id": image_id,
            "run": run,
            "started_at": "2026-09-13T00:00:00Z",
            "status": status,
            "metrics_source": source,
            "sse_block_ids": list(blocks) if source == "SSE" else [],
            "metrics": {
                "SOURCE": source,
                "COUNT": count,
                "AVG": average,
                "AVG_EXACT": average,
            },
        }

    @classmethod
    def summary(cls, root: Path, images: list[dict[str, str]], samples: list[dict]) -> str:
        campaign = sequential_driver.Campaign(
            cls.args(root, images=images, run_count=2)
        )
        campaign.samples = samples
        campaign.write_summary()
        return (campaign.output_dir / "summary.md").read_text()

    def test_parse_output_keeps_sse_identity_and_k6_delivery(self):
        log = self.log(delivered=(10, 11), sse=(10, 11))
        metrics, exceptions, invalid, client_metrics = sequential_driver.parse_output(log)

        self.assertEqual(("SSE", "2", "10.00"), (metrics["SOURCE"], metrics["COUNT"], metrics["AVG"]))
        self.assertEqual("2", metrics["TTFB_COUNT"])
        self.assertEqual([10, 11], [item["block_number"] for item in client_metrics])
        self.assertEqual([10, 11], [row["payload_index"] for row in sequential_driver.parse_k6_rows(log)])
        self.assertEqual(([], []), (exceptions, invalid))

    def test_sample_health_rejects_failed_execution_and_invalid_logs(self):
        cases = (
            ("nonzero execution", self.log(), 1, "execution_success", False),
            ("no metrics", "Nethermind is shut down\nCleanup completed\n", 0, "metrics_count", False),
            ("missing shutdown", self.log(shutdown=False), 0, "normal_shutdown", False),
            ("missing cleanup", self.log(cleanup=False), 0, "cleanup_completed_marker", False),
            ("exception", self.log(extra=("System.Exception: failed",)), 0, "exception_found", True),
            ("invalid block", self.log(extra=("Invalid Block 10",)), 0, "invalid_block_found", True),
        )
        for name, output, exit_code, field, expected in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                sample, _ = self.run_sample(Path(directory), output=output, returncode=exit_code)
            self.assertEqual("failed", sample["status"])
            self.assertEqual(expected, bool(sample[field]))

    def test_delivery_and_sse_coverage_are_checked(self):
        complete = self.log(delivered=(10, 11, 12), sse=(10, 11))
        with tempfile.TemporaryDirectory() as directory:
            sample, _ = self.run_sample(Path(directory), output=complete, amount=3)
        self.assertEqual("success", sample["status"])
        self.assertEqual((3, 2, "SSE"), (sample["delivered_count"], sample["metrics_count"], sample["metrics_source"]))
        self.assertTrue(sample["sse_coverage_valid"])

        with tempfile.TemporaryDirectory() as directory:
            sample, _ = self.run_sample(
                Path(directory),
                output=self.log(delivered=(10, 11, 12), sse=(10,)),
                amount=3,
            )
        self.assertEqual("failed", sample["status"])
        self.assertFalse(sample["sse_coverage_valid"])
        self.assertEqual("SSE", sample["metrics_source"])

        with tempfile.TemporaryDirectory() as directory:
            sample, _ = self.run_sample(
                Path(directory),
                output=self.log(delivered=(10, 11), sse=(10, 11)),
                amount=3,
            )
        self.assertEqual(2, sample["delivered_count"])
        self.assertFalse(sample["delivery_count_valid"])
        self.assertEqual("failed", sample["status"])


    def test_cleanup_and_scratch_guards_stop_cross_sample_contamination(self):
        for case in ("initial container", "initial scratch", "after sample"):
            with self.subTest(case=case), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args = self.args(root, images=[self.image(), self.image("image-b")])
                campaign = sequential_driver.Campaign(args)
                popen = Mock(return_value=FakeProcess(self.log()))
                base_patches = [
                    patch.object(sequential_driver.signal, "signal"),
                    patch.object(sequential_driver.subprocess, "Popen", popen),
                    patch.object(campaign, "render_config", return_value="scenario"),
                    patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                    patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                ]
                if case == "initial container":
                    base_patches[3] = patch.object(
                        sequential_driver,
                        "verify_cleanup",
                        return_value=(False, ["benchmark containers remain: stale"]),
                    )
                elif case == "initial scratch":
                    work = root / "data" / "work"
                    (work / "upper").mkdir(parents=True)
                    (work / "upper" / "leftover").write_text("cancelled")
                    Path(args.config_template).write_text(f"paths:\n  work: {work}\n")
                    yq_result = Mock(
                        returncode=0,
                        stdout=json.dumps([True, str(work)]),
                        stderr="",
                    )
                    base_patches[4] = patch.object(
                        sequential_driver.subprocess,
                        "run",
                        return_value=yq_result,
                    )
                else:
                    base_patches[3] = patch.object(
                        sequential_driver,
                        "verify_cleanup",
                        side_effect=[(True, []), (False, ["benchmark containers remain: c1"])],
                    )
                with contextlib.ExitStack() as stack:
                    for item in base_patches:
                        stack.enter_context(item)
                    with contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                        result = campaign.run()

            self.assertEqual(1, result)
            self.assertTrue(campaign.aborted)
            if case == "after sample":
                self.assertEqual(1, popen.call_count)
                self.assertEqual(1, len(campaign.samples))
            else:
                self.assertEqual(0, popen.call_count)
                self.assertEqual([], campaign.samples)
                if case == "initial scratch":
                    self.assertIn(
                        "not empty",
                        campaign.preflight["snapshot_scratch_problems"][0],
                    )

    def test_compute_warmup_requires_exact_success_for_each_delivered_index(self):
        cases = (
            ("all exact", (9, 10, 100), (9, 10, 100), (), "ok"),
            ("generic line is not evidence", (9, 10, 100), (9, 10), (), "missing"),
            ("failed marker", (9, 10, 100), (9, 10), (100,), "failed"),
        )
        for name, delivered, successes, failures, expected in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                extra = ("ordinary warmup block=100 ok",) if name.startswith("generic") else ()
                sample, _ = self.run_sample(
                    Path(directory),
                    output=self.log(
                        delivered=delivered,
                        sse=delivered,
                        warmup_ok=successes,
                        warmup_failed=failures,
                        extra=extra,
                    ),
                    amount=len(delivered),
                    measurement_mode="compute-warm",
                )
            self.assertEqual(expected, sample["warmup_status"])
            self.assertEqual("success" if expected == "ok" else "failed", sample["status"])


    def test_failed_compute_warmup_skips_remaining_repetitions(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            images = [self.image(), self.image("image-b")]
            campaign = sequential_driver.Campaign(
                self.args(root, images=images, run_count=3, measurement_mode="compute-warm")
            )
            calls: list[tuple[str, int]] = []

            def run_sample(image: dict[str, str], run: int) -> dict:
                calls.append((image["id"], run))
                if image["id"] == "image-a":
                    campaign.failures += 1
                    return {
                        "sample_id": f"{image['id']}-run{run}",
                        "image_id": image["id"],
                        "run": run,
                        "started_at": "2026-09-13T00:00:00Z",
                        "status": "failed",
                        "warmup_status": "failed",
                        "cleanup_verified": True,
                        "metrics": {},
                    }
                return self.record("image-b", run, source="TTFB", count="1", average="1")

            with (
                patch.object(campaign, "run_sample", side_effect=run_sample),
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
        self.assertEqual(["skipped", "skipped"], [sample["status"] for sample in campaign.samples[1:3]])

    def test_cancellation_starts_graceful_shutdown_watchdog(self):
        with tempfile.TemporaryDirectory() as directory:
            campaign = sequential_driver.Campaign(self.args(Path(directory)))
        process = Mock()
        process.pid = 123
        process.poll.return_value = None
        campaign.current_process = process
        timer = Mock()
        with (
            patch.object(sequential_driver.os, "killpg", create=True) as killpg,
            patch.object(sequential_driver.threading, "Timer", return_value=timer) as timer_factory,
            contextlib.redirect_stdout(io.StringIO()),
        ):
            campaign.terminate(sequential_driver.signal.SIGTERM, None)
            process.poll.return_value = 0
            campaign._force_termination(process)

        killpg.assert_any_call(123, sequential_driver.signal.SIGTERM)
        if hasattr(sequential_driver.signal, "SIGKILL"):
            killpg.assert_any_call(123, sequential_driver.signal.SIGKILL)
        else:
            process.kill.assert_called_once_with()
        timer_factory.assert_called_once()
        timer.start.assert_called_once_with()
        process.wait.assert_not_called()
        self.assertTrue(campaign.cancel_requested)
        self.assertTrue(campaign.aborted)

    def test_campaign_order_is_fresh_and_yq_receives_full_image_reference(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            campaign = sequential_driver.Campaign(
                self.args(root, images=[self.image(), self.image("image-b")], run_count=2)
            )
            destination = root / "rendered.yaml"
            full_image = "ghcr.io/example/nethermind:pr-123@sha256:" + "a" * 64
            with patch.object(sequential_driver.subprocess, "run", Mock()) as yq:
                scenario = campaign.render_config(
                    self.image(reference=full_image), 1, destination
                )
            self.assertEqual("nethermind-multi-image-a-run1", scenario)
            self.assertEqual(full_image, yq.call_args.kwargs["env"]["IMAGE"])

            rendered: list[tuple[str, int, Path]] = []

            def render(image: dict[str, str], run: int, path: Path) -> str:
                rendered.append((image["id"], run, path))
                path.write_text(f"scenario: {image['id']}-{run}\n")
                return f"nethermind-multi-{image['id']}-run{run}"

            popen = Mock(side_effect=lambda *args, **kwargs: FakeProcess(self.log()))
            with (
                patch.object(campaign, "render_config", side_effect=render),
                patch.object(sequential_driver.subprocess, "Popen", popen),
                patch.object(sequential_driver, "verify_cleanup", return_value=(True, [])),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                patch.object(sequential_driver.signal, "signal"),
                contextlib.redirect_stdout(io.StringIO()),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                result = campaign.run()

        self.assertEqual(0, result)
        self.assertEqual(
            ["image-a-run1", "image-a-run2", "image-b-run1", "image-b-run2"],
            [sample["sample_id"] for sample in campaign.samples],
        )
        self.assertEqual(4, len({path for _, _, path in rendered}))
        config_paths = [
            Path(call.args[0][call.args[0].index("--config-file") + 1])
            for call in popen.call_args_list
        ]
        self.assertEqual([path for _, _, path in rendered], config_paths)


    def test_combined_log_retains_full_lines_while_console_is_bounded(self):
        long_line = "X" * (sequential_driver.CONSOLE_LINE_LIMIT + 100)
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            stdout = io.StringIO()
            with (
                patch.object(
                    sequential_driver,
                    "print_console_line",
                    wraps=sequential_driver.print_console_line,
                ),
                contextlib.redirect_stdout(stdout),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                sample, _ = self.run_sample(
                    root,
                    output=long_line + "\n" + self.log(),
                    capture_console=False,
                )
            raw_log = Path(sample["log"]).read_text(encoding="utf-8")

        console = stdout.getvalue()
        self.assertIn(long_line, raw_log)
        self.assertIn("X" * sequential_driver.CONSOLE_LINE_LIMIT, console)
        self.assertIn(sequential_driver.CONSOLE_TRUNCATION, console)
        self.assertNotIn("X" * (sequential_driver.CONSOLE_LINE_LIMIT + 1), console)

    def test_summary_uses_only_comparable_successful_samples(self):
        images = [self.image(), self.image("image-b")]
        with tempfile.TemporaryDirectory() as directory:
            samples = [
                self.record("image-a", 1, average="10"),
                self.record("image-a", 2, status="failed", average="90"),
                self.record("image-b", 1, average="12"),
                self.record("image-b", 2, average="12"),
            ]
            summary = self.summary(Path(directory), images, samples)

        aggregate_a = next(line for line in summary.splitlines() if "image-a" in line and "| 1 | 10.00 |" in line)
        aggregate_b = next(line for line in summary.splitlines() if "image-b" in line and "| 2 | 12.00 |" in line)
        self.assertIn("+0.00%", aggregate_a)
        self.assertIn("+20.00%", aggregate_b)
        self.assertIn("unavailable", next(line for line in summary.splitlines() if line.startswith("| 1 |")))

        cases = (
            ("mixed source", self.record("image-a", 1, source="SSE"), self.record("image-a", 2, source="TTFB")),
            ("mismatched count", self.record("image-a", 1, count="2"), self.record("image-a", 2, count="3")),
            ("mismatched blocks", self.record("image-a", 1), self.record("image-a", 2, blocks=(100, 102))),
        )
        for name, first, second in cases:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                summary = self.summary(Path(directory), [self.image()], [first, second])
            aggregate = next(line for line in summary.splitlines() if "image-a" in line and "| 2 | n/a | n/a |" in line)
            self.assertIn("n/a", aggregate)


if __name__ == "__main__":
    unittest.main()
