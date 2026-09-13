#!/usr/bin/env python3
"""Integration tests for the EXPB sequential campaign driver."""

import argparse
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


class FakeExpbProcess:
    def __init__(self, output: str, returncode: int):
        self.stdout = iter(output.splitlines(keepends=True))
        self.returncode = returncode
        self.pid = 1234

    def wait(self, timeout: float | None = None) -> int:
        return self.returncode

    def poll(self) -> int:
        return self.returncode


class ExpbDriverIntegrationTests(unittest.TestCase):
    def make_args(
        self,
        root: Path,
        images: list[dict[str, str]] | None = None,
        run_count: int = 1,
        measurement_mode: str = "standard",
    ) -> argparse.Namespace:
        config_template = root / "template.yaml"
        config_template.write_text(
            "paths:\n"
            "  data: /mnt/sda/expb-data\n"
            "scenarios:\n"
            "  nethermind:\n"
            "    image: nethermindeth/nethermind:<<DOCKER_TAG>>\n"
            "    delay: <<DELAY>>\n"
        )
        image_plan = images or [{"id": "image-a", "image": "repo:image-a"}]
        return argparse.Namespace(
            images_json=json.dumps(image_plan),
            config_template=str(config_template),
            output_dir=str(root / "campaign"),
            expb_data_dir=str(root / "expb-data"),
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
            run_count=run_count,
            cleanup_grace_seconds=90,
            dottrace=False,
            dottrace_mode="sampling",
            perf=False,
            measurement_mode=measurement_mode,
        )

    @staticmethod
    def valid_log(
        processing_ms: float = 10.0,
        warmup: str = "",
        normal_shutdown: bool = True,
        cleanup: bool = True,
    ) -> str:
        lines = [
            f"[payload-server] client_metric block_number=100 processing_ms={processing_ms}",
            "| 100 | 100 | 10.0 |",
        ]
        if warmup:
            lines.append(warmup.rstrip("\n"))
        if normal_shutdown:
            lines.append("Nethermind is shut down")
        if cleanup:
            lines.append("Cleanup completed")
        return "\n".join(lines) + "\n"

    def run_sample(
        self,
        root: Path,
        *,
        output: str,
        returncode: int = 0,
        cleanup: tuple[bool, list[str] | tuple[str, ...]] = (True, ()),
        measurement_mode: str = "standard",
    ) -> tuple[dict, Mock]:
        args = self.make_args(root, measurement_mode=measurement_mode)
        campaign = sequential_driver.Campaign(args)
        process_mock = Mock(
            side_effect=lambda *popen_args, **popen_kwargs: FakeExpbProcess(
                output, returncode
            )
        )
        with (
            patch.object(campaign, "render_config", return_value="scenario"),
            patch.object(sequential_driver.subprocess, "Popen", process_mock),
            patch.object(sequential_driver, "verify_cleanup", return_value=cleanup),
            patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
            contextlib.redirect_stdout(io.StringIO()),
        ):
            sample = campaign.run_sample(
                {"id": "image-a", "image": "repo:image-a", "tag": "image-a", "date": "n/a"},
                1,
            )
        return sample, process_mock

    def test_nonzero_execution_stays_failed_with_valid_metrics(self):
        with self.temporary_root() as root:
            sample, _ = self.run_sample(
                root,
                output=self.valid_log(),
                returncode=1,
            )

        self.assertEqual(1, sample["exit_code"])
        self.assertFalse(sample["execution_success"])
        self.assertEqual("failed", sample["status"])
        self.assertEqual(1, sample["metrics_count"])

    def test_zero_metrics_cannot_pass(self):
        with self.temporary_root() as root:
            sample, _ = self.run_sample(
                root,
                output="Nethermind is shut down\nCleanup completed\n",
            )

        self.assertEqual(0, sample["metrics_count"])
        self.assertEqual("failed", sample["status"])

    def test_missing_normal_shutdown_or_cleanup_marker_cannot_pass(self):
        cases = {
            "normal shutdown": "Cleanup completed\n",
            "cleanup": "Nethermind is shut down\n",
        }
        for name, suffix in cases.items():
            with self.subTest(name=name):
                with self.temporary_root() as root:
                    sample, _ = self.run_sample(
                        root,
                        output=self.valid_log(
                            normal_shutdown=name != "normal shutdown",
                            cleanup=name != "cleanup",
                        ),
                    )
                self.assertEqual("failed", sample["status"])

    def test_compute_warm_requires_successful_warmup(self):
        cases = {
            "failed": "[payload-server] warmup block=100 FAILED elapsed=1.0ms error=rpc\n",
            "missing": "",
        }
        for name, warmup in cases.items():
            with self.subTest(name=name):
                with self.temporary_root() as root:
                    sample, _ = self.run_sample(
                        root,
                        output=self.valid_log(warmup=warmup),
                        measurement_mode="compute-warm",
                    )
                self.assertNotEqual("ok", sample.get("warmup_status"))
                self.assertEqual("failed", sample["status"])

    def test_compute_warm_rejects_generic_warmup_output(self):
        with self.temporary_root() as root:
            sample, _ = self.run_sample(
                root,
                output=self.valid_log(warmup="benchmark warmup complete\n"),
                measurement_mode="compute-warm",
            )

        self.assertNotEqual("ok", sample.get("warmup_status"))
        self.assertEqual("failed", sample["status"])

    def test_compute_warm_accepts_payload_server_warmup_evidence(self):
        with self.temporary_root() as root:
            sample, _ = self.run_sample(
                root,
                output=self.valid_log(
                    warmup="[payload-server] warmup block=100 ok elapsed=1.0ms\n"
                ),
                measurement_mode="compute-warm",
            )

        self.assertEqual("ok", sample.get("warmup_status"))
        self.assertEqual("success", sample["status"])

    def test_incomplete_k6_delivery_cannot_pass(self):
        with self.temporary_root() as root:
            sample, _ = self.run_sample(
                root,
                output=self.valid_log().replace("| 100 | 100 | 10.0 |\n", ""),
            )

        self.assertEqual(1, sample["metrics_count"])
        self.assertEqual("failed", sample["status"])

    def test_cleanup_leftover_aborts_before_next_image(self):
        with self.temporary_root() as root:
            images = [
                {"id": "image-a", "image": "repo:image-a"},
                {"id": "image-b", "image": "repo:image-b"},
            ]
            args = self.make_args(root, images=images)
            campaign = sequential_driver.Campaign(args)
            popen = Mock(
                side_effect=lambda *popen_args, **popen_kwargs: FakeExpbProcess(
                    self.valid_log(), 0
                )
            )
            with (
                patch.object(campaign, "render_config", return_value="scenario"),
                patch.object(sequential_driver.subprocess, "Popen", popen),
                patch.object(
                    sequential_driver,
                    "verify_cleanup",
                    side_effect=[
                        (True, []),
                        (False, ["benchmark containers remain: c1"]),
                    ],
                ),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                patch.object(sequential_driver.signal, "signal"),
                contextlib.redirect_stdout(io.StringIO()),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                result = campaign.run()

        self.assertEqual(1, result)
        self.assertTrue(campaign.aborted)
        self.assertEqual(1, len(campaign.samples))
        self.assertEqual("image-a-run1", campaign.samples[0]["sample_id"])
        self.assertEqual(1, popen.call_count)

    def test_initial_cleanup_leftover_prevents_any_executor_start(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            popen = Mock()
            with (
                patch.object(sequential_driver.subprocess, "Popen", popen),
                patch.object(
                    sequential_driver,
                    "verify_cleanup",
                    return_value=(False, ["benchmark containers remain: stale"]),
                ),
                patch.object(sequential_driver, "verify_snapshot_scratch", return_value=(True, [])),
                patch.object(sequential_driver.signal, "signal"),
                contextlib.redirect_stdout(io.StringIO()),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                result = campaign.run()

        self.assertEqual(1, result)
        self.assertTrue(campaign.aborted)
        self.assertEqual([], campaign.samples)
        popen.assert_not_called()

    def test_successful_samples_are_fresh_and_ordered(self):
        with self.temporary_root() as root:
            images = [
                {"id": "image-a", "image": "repo:image-a"},
                {"id": "image-b", "image": "repo:image-b"},
            ]
            args = self.make_args(root, images=images, run_count=2)
            campaign = sequential_driver.Campaign(args)
            rendered: list[tuple[str, int, Path]] = []

            def render(image: dict[str, str], run: int, destination: Path) -> str:
                rendered.append((image["id"], run, destination))
                destination.write_text(f"scenario: {image['id']}-{run}\n")
                return f"nethermind-multi-{image['id']}-run{run}"

            popen = Mock(
                side_effect=lambda *popen_args, **popen_kwargs: FakeExpbProcess(
                    self.valid_log(), 0
                )
            )
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
            [
                "image-a-run1",
                "image-a-run2",
                "image-b-run1",
                "image-b-run2",
            ],
            [sample["sample_id"] for sample in campaign.samples],
        )
        self.assertEqual(4, len({path for _, _, path in rendered}))
        config_paths = [
            Path(call.args[0][call.args[0].index("--config-file") + 1])
            for call in popen.call_args_list
        ]
        self.assertEqual([path for _, _, path in rendered], config_paths)

    def test_summary_cv_excludes_failed_samples(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            campaign.samples = [
                self.sample_record("success", "SSE", "2", "10.00", 1),
                self.sample_record("failed", "SSE", "2", "100.00", 2),
                self.sample_record("success", "SSE", "2", "12.00", 3),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        self.assertIn("12.86%", summary)

    def test_summary_cv_refuses_mismatched_counts(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            campaign.samples = [
                self.sample_record("success", "SSE", "2", "10.00", 1),
                self.sample_record("success", "SSE", "2", "12.00", 2),
                self.sample_record("success", "SSE", "3", "100.00", 3),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        cv_cell = self.summary_cv_cell(summary)
        self.assertRegex(cv_cell, r"(?i)unavailable|mixed[ _-]*count|mismatch(?:ed)?[ _-]*count")
        self.assertNotIn("12.86%", cv_cell)

    def test_summary_cv_refuses_mismatched_block_ids(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            campaign.samples = [
                self.sample_record("success", "SSE", "2", "10.00", 1, [100, 101]),
                self.sample_record("success", "SSE", "2", "12.00", 2, [100, 102]),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        cv_cell = self.summary_cv_cell(summary)
        self.assertRegex(cv_cell, r"(?i)unavailable|mixed[ _-]*block|mismatch(?:ed)?[ _-]*block")
        self.assertNotIn("12.86%", cv_cell)

    def test_summary_cv_refuses_mixed_metric_sources(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            campaign.samples = [
                self.sample_record("success", "SSE", "2", "10.00", 1),
                self.sample_record("success", "TTFB", "2", "12.00", 2),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        cv_cell = self.summary_cv_cell(summary)
        self.assertRegex(cv_cell, r"(?i)unavailable|mixed[ _-]*source")
        self.assertNotIn("12.86%", summary)

    def test_render_config_passes_complete_image_reference_to_yq(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            destination = root / "rendered.yaml"
            image = "ghcr.io/example/nethermind:pr-123@sha256:0123456789abcdef"
            yq = Mock()
            with patch.object(sequential_driver.subprocess, "run", yq):
                scenario = campaign.render_config(
                    {"id": "image-a", "image": image, "tag": "pr-123", "date": "today"},
                    1,
                    destination,
                )

        self.assertEqual("nethermind-multi-image-a-run1", scenario)
        self.assertEqual(1, yq.call_count)
        command = yq.call_args.args[0]
        self.assertEqual(
            [
                "yq",
                "-i",
                ".scenarios.[strenv(SK)].image = strenv(IMAGE)",
                str(destination),
            ],
            command,
        )
        self.assertEqual("nethermind-multi-image-a-run1", yq.call_args.kwargs["env"]["SK"])
        self.assertEqual(image, yq.call_args.kwargs["env"]["IMAGE"])

    @staticmethod
    def sample_record(
        status: str,
        source: str,
        count: str,
        average: str,
        run: int,
        block_numbers: list[int] | None = None,
    ) -> dict:
        if block_numbers is None and source == "SSE":
            block_numbers = list(range(100, 100 + int(count)))
        return {
            "sample_id": f"image-a-run{run}",
            "image_id": "image-a",
            "run": run,
            "started_at": "2026-09-13T00:00:00Z",
            "status": status,
            "metrics_source": source,
            "sse_block_ids": block_numbers or [],
            "sse_client_metrics": [
                {"block_number": block, "processing_ms": float(average)}
                for block in (block_numbers or [])
            ],
            "metrics": {
                "SOURCE": source,
                "COUNT": count,
                "AVG": average,
                "AVG_EXACT": average,
            },
        }

    @staticmethod
    def summary_cv_cell(summary: str) -> str:
        return next(line for line in summary.splitlines() if line.startswith("| 1 |"))

    @staticmethod
    @contextlib.contextmanager
    def temporary_root():
        with tempfile.TemporaryDirectory() as directory:
            yield Path(directory)


if __name__ == "__main__":
    unittest.main()
