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
    def valid_log(processing_ms: float = 10.0, warmup: str = "") -> str:
        return (
            f"[payload-server] client_metric block_number=100 processing_ms={processing_ms}\n"
            f"{warmup}"
            "Nethermind is shut down\n"
            "Cleanup completed\n"
        )

    def run_sample(
        self,
        root: Path,
        *,
        output: str,
        returncode: int = 0,
        cleanup: tuple[bool, list[str]] = (True, ()),
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
                        output=(
                            "[payload-server] client_metric block_number=100 "
                            "processing_ms=10.0\n"
                            + suffix
                        ),
                    )
                self.assertEqual("failed", sample["status"])

    def test_compute_warm_requires_successful_warmup(self):
        cases = {
            "failed": "warmup failed: error\n",
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
                    return_value=(False, ["benchmark containers remain: c1"]),
                ),
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

    def test_summary_cv_excludes_failed_and_mismatched_counts(self):
        with self.temporary_root() as root:
            args = self.make_args(root)
            campaign = sequential_driver.Campaign(args)
            campaign.samples = [
                self.sample_record("success", "SSE", "2", "10.00", 1),
                self.sample_record("failed", "SSE", "2", "100.00", 2),
                self.sample_record("success", "SSE", "2", "12.00", 3),
                self.sample_record("success", "SSE", "3", "100.00", 4),
            ]
            campaign.write_summary()
            summary = (campaign.output_dir / "summary.md").read_text()

        self.assertIn("12.86%", summary)
        self.assertNotIn("37.80%", summary)

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

        self.assertIn("mixed sources", summary)
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
    ) -> dict:
        return {
            "sample_id": f"image-a-run{run}",
            "image_id": "image-a",
            "run": run,
            "started_at": "2026-09-13T00:00:00Z",
            "status": status,
            "metrics_source": source,
            "metrics": {
                "SOURCE": source,
                "COUNT": count,
                "AVG": average,
                "AVG_EXACT": average,
            },
        }

    @staticmethod
    @contextlib.contextmanager
    def temporary_root():
        with tempfile.TemporaryDirectory() as directory:
            yield Path(directory)


if __name__ == "__main__":
    unittest.main()
