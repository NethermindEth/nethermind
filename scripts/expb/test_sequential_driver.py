#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Coverage for the sequential EXPB campaign driver.

The driver owns every pass/fail gate for a multi-image campaign, and a campaign costs hours of
benchmark-runner time. These tests pin the parsing and gating it replaced awk with, plus the
failure scope that decides how much of a campaign one bad sample discards.
"""

import contextlib
import importlib.util
import json
import os
import sys
import tempfile
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DRIVER = ROOT / "scripts" / "expb" / "sequential_driver.py"

_spec = importlib.util.spec_from_file_location("sequential_driver", DRIVER)
driver = importlib.util.module_from_spec(_spec)
sys.modules["sequential_driver"] = driver
_spec.loader.exec_module(driver)


@contextlib.contextmanager
def environment(**values: str):
    """Run with exactly these driver variables set; everything else is cleared."""
    managed = (
        "AMOUNT",
        "ADDITIONAL_EXTRA_FLAGS",
        "CAMPAIGN_FAIL_FAST",
        "CLIENT",
        "CLIENT_ENV",
        "CLIENT_SNAPSHOT_DIR",
        "DELAY_SECONDS",
        "EXPB_CAMPAIGN_DIR",
        "EXPB_DATA_DIR",
        "EXPB_ENV_PASSTHROUGH",
        "EXPB_IMAGES_JSON",
        "FLAT_SNAPSHOT_BLOCK_DIR",
        "FLAT_SNAPSHOT_DIR",
        "MEASUREMENT_MODE",
        "MEASUREMENT_SOURCE",
        "RUN_COUNT",
        "SNAPSHOT_MOUNT_PATH",
        "TRACE_BLOCKS",
    )
    previous = {name: os.environ.get(name) for name in managed}
    for name in managed:
        os.environ.pop(name, None)
    os.environ.update(values)
    try:
        yield
    finally:
        for name, value in previous.items():
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value


class ParsingTests(unittest.TestCase):
    def test_parse_flags_joins_a_value_onto_the_flag_it_belongs_to(self) -> None:
        # Dispatch inputs arrive as free text: newline- or comma-separated, and a value may be split
        # from its flag by whitespace or by the separator itself.
        self.assertEqual(driver.parse_flags(""), [])
        self.assertEqual(driver.parse_flags("--JsonRpc.GasCap=1000"), ["--JsonRpc.GasCap=1000"])
        self.assertEqual(driver.parse_flags("--JsonRpc.GasCap 1000"), ["--JsonRpc.GasCap=1000"])
        self.assertEqual(driver.parse_flags("--JsonRpc.GasCap,1000"), ["--JsonRpc.GasCap=1000"])
        self.assertEqual(
            driver.parse_flags("--A=1\n--B 2\n--C\n3"),
            ["--A=1", "--B=2", "--C=3"],
        )

    def test_parse_pairs_rejects_anything_that_is_not_a_usable_variable(self) -> None:
        self.assertEqual(driver.parse_pairs(""), {})
        self.assertEqual(driver.parse_pairs("A=1, B=2"), {"A": "1", "B": "2"})
        # An empty value is legitimate; a missing '=' or an unusable name is not.
        self.assertEqual(driver.parse_pairs("A="), {"A": ""})
        for rejected in ("A", "1A=2", "A B=2", "=2"):
            with self.subTest(rejected=rejected), self.assertRaises(ValueError):
                driver.parse_pairs(rejected)

    def test_parse_amount_accepts_only_a_positive_integer(self) -> None:
        self.assertIsNone(driver.parse_amount(""))
        self.assertEqual(driver.parse_amount("1000"), 1000)
        for rejected in ("0", "-1", "1.5", "1000 ", "ten"):
            with self.subTest(rejected=rejected), self.assertRaises(ValueError):
                driver.parse_amount(rejected)


class MetricStatsTests(unittest.TestCase):
    def test_percentiles_and_median_match_the_awk_indexing_they_replaced(self) -> None:
        stats = driver.metric_stats([float(value) for value in range(1, 11)])
        self.assertEqual(stats["count"], 10)
        self.assertEqual(stats["avg"], 5.5)
        self.assertEqual(stats["median"], 5.5)
        self.assertEqual(stats["p90"], 9)
        self.assertEqual(stats["p95"], 10)
        self.assertEqual(stats["p99"], 10)
        self.assertEqual(stats["min"], 1)
        self.assertEqual(stats["max"], 10)

        # Odd counts take the middle element, not an interpolation between neighbours.
        self.assertEqual(driver.metric_stats([1.0, 2.0, 3.0])["median"], 2)
        # A single sample has to land on itself at every rank rather than index below zero.
        single = driver.metric_stats([7.0])
        self.assertEqual((single["median"], single["p90"], single["p99"]), (7, 7, 7))
        # Unsorted input is ordered first; the caller never promises sorted values.
        self.assertEqual(driver.metric_stats([3.0, 1.0, 2.0])["min"], 1)

        empty = driver.metric_stats([])
        self.assertEqual(empty["count"], 0)
        self.assertTrue(all(empty[key] is None for key in ("avg", "median", "p90", "p95", "p99", "min", "max")))


class CollectMetricsTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def write_log(self, text: str) -> Path:
        path = self.directory / "combined.log"
        path.write_text(text, encoding="utf-8")
        return path

    def test_sse_and_k6_rows_are_paired_through_ansi_decoration(self) -> None:
        # expb's output is coloured; parsing it without stripping the escapes loses whole rows.
        log = self.write_log(
            "\x1b[32m[payload-server] client_metric block_number=100 processing_ms=20\x1b[0m\n"
            "[payload-server] client_metric block_number=101 processing_ms=30\n"
            "| 1 | 30000000 | 25.0 |\n"
            "\x1b[1m| 2 | 30000000 | 35.0 |\x1b[0m\n"
            "Nethermind is shut down\n"
            'event="Cleanup completed"\n'
        )
        parsed, diagnostics = driver.collect_metrics(log)

        self.assertEqual(parsed["source"], "SSE")
        self.assertEqual((parsed["delivered"], parsed["sse_count"]), (2, 2))
        self.assertEqual(parsed["ids"], [100, 101])
        self.assertEqual(parsed["payload_indices"], [1, 2])
        self.assertEqual(parsed["processing"]["avg"], 25.0)
        self.assertEqual(parsed["request"]["avg"], 30.0)
        # Request minus processing is the request path and GC, paired per payload.
        self.assertEqual(parsed["outside"]["avg"], 5.0)
        self.assertAlmostEqual(parsed["mgas_s"], 60 / (50 / 1000))
        # The request series gets its own throughput over the same payloads' gas.
        self.assertAlmostEqual(parsed["request_mgas_s"], 60 / (60 / 1000))
        self.assertTrue(parsed["shutdown"] and parsed["cleanup"])
        self.assertEqual(diagnostics, {"exceptions": [], "invalid": [], "severe": []})

    def feed_log(self, rows: int, records: int) -> Path:
        # Payload i takes 20 ms to process and 25 ms to request; the feed carries the first `records` of them.
        return self.write_log(
            "".join(f"[payload-server] client_metric block_number={100 + index} processing_ms=20\n" for index in range(records))
            + "".join(f"| {index} | 30000000 | 25.0 |\n" for index in range(1, rows + 1))
        )

    def test_a_feed_that_lacks_only_its_last_record_pairs_as_a_prefix(self) -> None:
        # expb logs block N's record when payload N+1 is fetched, so the last one is the record a normal run
        # lacks; the same rule as `Analyze benchmark output` pairs the rest by position.
        parsed, _ = driver.collect_metrics(self.feed_log(1000, 999))
        self.assertEqual((parsed["source"], parsed["delivered"], parsed["sse_count"]), ("SSE", 1000, 999))
        self.assertEqual(parsed["processing"]["avg"], 20.0)
        self.assertEqual(parsed["outside"]["avg"], 5.0)
        # Gas over the 999 paired payloads, not all 1000, against their processing time.
        self.assertAlmostEqual(parsed["mgas_s"], (999 * 30) / (999 * 20 / 1000))
        # The request series pairs gas and time within the same k6 row, so it covers every payload.
        self.assertAlmostEqual(parsed["request_mgas_s"], (1000 * 30) / (1000 * 25 / 1000))

    def test_a_larger_feed_gap_leaves_the_paired_figures_unavailable(self) -> None:
        # Two or more missing records means one went missing mid-run, and a positional pairing would shear
        # every value after it, so the paired figures decline instead.
        parsed, _ = driver.collect_metrics(self.feed_log(1000, 998))
        self.assertEqual((parsed["delivered"], parsed["sse_count"]), (1000, 998))
        self.assertEqual(parsed["processing"]["avg"], 20.0)
        self.assertIsNone(parsed["outside"]["avg"])
        self.assertIsNone(parsed["mgas_s"])
        self.assertAlmostEqual(parsed["request_mgas_s"], (1000 * 30) / (1000 * 25 / 1000))

    def test_a_complete_feed_pairs_every_payload(self) -> None:
        parsed, _ = driver.collect_metrics(self.feed_log(1000, 1000))
        self.assertEqual(parsed["outside"]["avg"], 5.0)
        self.assertAlmostEqual(parsed["mgas_s"], (1000 * 30) / (1000 * 20 / 1000))

    def test_without_sse_the_request_timings_stand_in_and_outside_is_empty(self) -> None:
        parsed, _ = driver.collect_metrics(self.write_log("| 1 | 30000000 | 25.0 |\n"))
        self.assertEqual(parsed["source"], "TTFB")
        self.assertEqual(parsed["avg"], 25.0)
        self.assertEqual(parsed["ids"], [1])
        self.assertIsNone(parsed["outside"]["avg"])
        self.assertIsNone(parsed["mgas_s"])

    def test_engine_api_ignores_the_client_feed_so_every_client_shares_one_clock(self) -> None:
        # measurement_source=engine-api compares clients on k6's request time; a stray Nethermind SSE
        # line must not turn one arm of a cross-client comparison into a processing-time measurement.
        log = self.write_log(
            "[payload-server] client_metric block_number=100 processing_ms=20\n"
            "| 1 | 30000000 | 25.0 |\n"
            "| 2 | 30000000 | 35.0 |\n"
        )
        parsed, _ = driver.collect_metrics(log, use_sse=False)
        self.assertEqual(parsed["source"], "TTFB")
        self.assertEqual((parsed["count"], parsed["sse_count"], parsed["avg"]), (2, 0, 30.0))
        self.assertIsNone(parsed["mgas_s"])
        self.assertAlmostEqual(parsed["request_mgas_s"], 60 / (60 / 1000))

    def test_an_empty_log_reports_no_source_rather_than_a_zero_measurement(self) -> None:
        parsed, _ = driver.collect_metrics(self.write_log(""))
        self.assertEqual((parsed["source"], parsed["count"], parsed["avg"]), ("none", 0, None))
        self.assertFalse(parsed["shutdown"] or parsed["cleanup"])

    def test_diagnostics_are_counted_in_full_but_retained_in_bounded_form(self) -> None:
        # A campaign log is unbounded; the counts drive the gates, the retained lines only explain them.
        noisy = "".join(f"System.Exception: failure {index}\n" for index in range(50))
        parsed, diagnostics = driver.collect_metrics(
            self.write_log(noisy + "Invalid Blocks detected\nFATAL: node died\n" + "x" * 9000 + "\n")
        )
        self.assertEqual(parsed["exception_count"], 50)
        self.assertEqual(len(diagnostics["exceptions"]), driver.MAX_DIAGNOSTIC_LINES)
        self.assertEqual(parsed["invalid_count"], 1)
        self.assertEqual(parsed["severe_count"], 1)
        self.assertLessEqual(len(diagnostics["exceptions"][0]), driver.LIMIT + 80)


class RenderTests(unittest.TestCase):
    BASE = {"scenarios": {"nethermind": {"image": "placeholder"}}}
    IMAGE = {"id": "image-1-abc", "image": "nethermindeth/nethermind:tag"}

    def test_compute_warm_supplies_the_gas_cap_the_warmup_needs(self) -> None:
        # The per-block eth_simulateV1 warm-up exhausts the default 100M RPC gas budget on dense
        # blocks and silently leaves them un-warmed, so the mode raises the cap itself.
        with environment(MEASUREMENT_MODE="compute-warm"):
            config, name = driver.render(self.BASE, self.IMAGE, 1)
        self.assertEqual(name, "nethermind-image-1-abc-run1")
        self.assertEqual(config["scenarios"][name]["extra_flags"], ["--JsonRpc.GasCap=1000000000000"])
        self.assertEqual(config["scenarios"][name]["image"], self.IMAGE["image"])

    def test_compute_warm_refuses_the_overrides_it_would_silently_fight(self) -> None:
        for label, values in (
            ("a gas cap override", {"ADDITIONAL_EXTRA_FLAGS": "--JsonRpc.GasCap=100"}),
            ("an EVM warmup override", {"EXPB_ENV_PASSTHROUGH": "EXPB_EVM_WARMUP=1"}),
        ):
            with self.subTest(rejected=label), environment(MEASUREMENT_MODE="compute-warm", **values):
                with self.assertRaises(ValueError):
                    driver.render(self.BASE, self.IMAGE, 1)

    def test_standard_mode_keeps_the_dispatched_flags_verbatim(self) -> None:
        with environment(ADDITIONAL_EXTRA_FLAGS="--JsonRpc.GasCap=100", CLIENT_ENV="A=1"):
            config, name = driver.render(self.BASE, self.IMAGE, 2)
        scenario = config["scenarios"][name]
        self.assertEqual(scenario["extra_flags"], ["--JsonRpc.GasCap=100"])
        self.assertEqual(scenario["extra_env"], {"A": "1"})

    def test_a_reference_client_gets_its_own_snapshot_and_none_of_the_nethermind_settings(self) -> None:
        base = {
            "scenarios": {
                "nethermind": {
                    "image": "placeholder",
                    "extra_flags": ["--FlatDb.Enabled=true"],
                    "extra_env": {"NETHERMIND_X": "1"},
                    "extra_volumes": {"a": "b"},
                }
            }
        }
        image = {"id": "image-1-abc", "image": "ghcr.io/paradigmxyz/reth:v1"}
        with environment(
            CLIENT="reth",
            CLIENT_SNAPSHOT_DIR="/mnt/sda/reth-25490000",
            SNAPSHOT_MOUNT_PATH="/execution-data",
            ADDITIONAL_EXTRA_FLAGS="--engine.slow-block-threshold=0",
        ):
            config, name = driver.render(base, image, 1)
        self.assertEqual(name, "reth-image-1-abc-run1")
        scenario = config["scenarios"][name]
        self.assertEqual(scenario["client"], "reth")
        self.assertEqual(scenario["image"], image["image"])
        self.assertEqual(scenario["snapshot_source"], "/mnt/sda/reth-25490000")
        self.assertEqual(scenario["snapshot_backend"], "overlay")
        self.assertEqual(scenario["snapshot_mount_path"], "/execution-data")
        self.assertEqual(scenario["startup_wait"], 600)
        # Dispatched flags still reach the reference client; the template's Nethermind ones do not.
        self.assertEqual(scenario["extra_flags"], ["--engine.slow-block-threshold=0"])
        self.assertEqual((scenario["extra_env"], scenario["extra_volumes"], scenario["extra_commands"]), ({}, {}, []))

    def test_a_reference_client_refuses_nethermind_only_settings(self) -> None:
        snapshot = {"CLIENT_SNAPSHOT_DIR": "/mnt/sda/geth-25490000", "SNAPSHOT_MOUNT_PATH": "/execution-data/geth"}
        for label, values in (
            ("compute-warm", {"MEASUREMENT_MODE": "compute-warm"}),
            ("block tracing", {"TRACE_BLOCKS": "25490001"}),
            ("client env", {"CLIENT_ENV": "DOTNET_gcServer=1"}),
            ("a missing snapshot", {"CLIENT_SNAPSHOT_DIR": ""}),
        ):
            with self.subTest(rejected=label), environment(CLIENT="geth", **{**snapshot, **values}):
                with self.assertRaises(ValueError):
                    driver.render(self.BASE, self.IMAGE, 1)
        with environment(CLIENT="erigon"), self.assertRaises(ValueError):
            driver.render(self.BASE, self.IMAGE, 1)

    def test_render_disables_cpu_quota_and_preserves_affinity(self) -> None:
        resources = {"cpu": 8, "cpuset": "2-7,10-15", "infra_cpuset": "0-1,8-9", "mem": "64g"}
        base = {"resources": dict(resources), **self.BASE}
        with environment():
            config, _ = driver.render(base, self.IMAGE, 1)
        self.assertEqual(config["resources"], {**resources, "cpu": 0})
        self.assertEqual(base["resources"], resources)

    def test_a_config_without_the_nethermind_scenario_is_rejected(self) -> None:
        with environment(), self.assertRaises(ValueError):
            driver.render({"scenarios": {}}, self.IMAGE, 1)


class CampaignScopeTests(unittest.TestCase):
    """How much of a campaign one failed sample discards."""

    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)
        self.addCleanup(self.temporary_directory.cleanup)
        self.original = (driver.run_sample, driver.verify_clean, driver.subprocess.check_output)

        def restore() -> None:
            driver.run_sample, driver.verify_clean, driver.subprocess.check_output = self.original

        self.addCleanup(restore)
        # `cancelled` is module state a SIGTERM sets; leaving it true would short-circuit the next case.
        driver.cancelled = False
        driver.verify_clean = lambda _config: None
        driver.subprocess.check_output = lambda *_args, **_kwargs: json.dumps(
            {"scenarios": {"nethermind": {}}, "paths": {"work": "work"}}
        )

    def run_campaign(self, failing: set[str], unclean: frozenset[str] = frozenset(), **values: str) -> tuple[int, list[str]]:
        attempted: list[str] = []

        def fake_run_sample(_base: dict, image: dict, run: int, root: Path) -> dict:
            sample_id = f"{image['id']}-run{run}"
            attempted.append(sample_id)
            status = "failed" if image["id"] in failing | unclean else "success"
            return {
                "sample_id": sample_id,
                "image_id": image["id"],
                "image": image["image"],
                "run": run,
                "status": status,
                "cleanup_verified": image["id"] not in unclean,
                "metrics": {"source": "SSE", "count": 1, "avg": 25.0, "delivered": 1, "ids": [1]},
                "sse_block_ids": [1],
            }

        driver.run_sample = fake_run_sample
        images = [{"id": f"image-{index}", "image": f"repo:tag{index}"} for index in (1, 2, 3)]
        with environment(
            EXPB_CAMPAIGN_DIR=str(self.directory / "campaign"),
            EXPB_IMAGES_JSON=json.dumps(images),
            **values,
        ):
            return driver.main(), attempted

    def test_an_explicit_comparison_stops_the_campaign_on_the_first_failure(self) -> None:
        # A bad arm invalidates the A/B, so the remaining arms are not worth the runner time.
        code, attempted = self.run_campaign({"image-2"}, RUN_COUNT="2", CAMPAIGN_FAIL_FAST="true")
        self.assertEqual(code, 1)
        self.assertEqual(attempted, ["image-1-run1", "image-1-run2", "image-2-run1"])

    def test_a_retrospective_sweep_abandons_only_the_failed_image(self) -> None:
        # Bisecting across many master builds: one benign shutdown-time exception on image 2 must not
        # discard the images that did run, nor the ones still to run.
        code, attempted = self.run_campaign({"image-2"}, RUN_COUNT="2", CAMPAIGN_FAIL_FAST="false")
        self.assertEqual(code, 1)
        self.assertEqual(attempted, ["image-1-run1", "image-1-run2", "image-2-run1", "image-3-run1", "image-3-run2"])
        campaign = json.loads((self.directory / "campaign" / "campaign.json").read_text(encoding="utf-8"))
        self.assertEqual([sample["status"] for sample in campaign["samples"]].count("failed"), 1)
        self.assertTrue((self.directory / "campaign" / "summary.md").is_file())

    def test_a_cleanup_verification_failure_stops_even_a_retrospective_sweep(self) -> None:
        # Debris the next sample would inherit makes every later measurement suspect, whatever the campaign is for.
        code, attempted = self.run_campaign(set(), frozenset({"image-2"}), RUN_COUNT="2", CAMPAIGN_FAIL_FAST="false")
        self.assertEqual(code, 1)
        self.assertEqual(attempted, ["image-1-run1", "image-1-run2", "image-2-run1"])

    def test_a_summary_that_cannot_be_written_still_leaves_the_campaign_record(self) -> None:
        def broken_summary(*_args) -> None:
            raise KeyError("metrics")

        original = driver.write_summary
        driver.write_summary = broken_summary
        self.addCleanup(setattr, driver, "write_summary", original)
        code, attempted = self.run_campaign(set(), RUN_COUNT="1")
        self.assertEqual(code, 1)
        campaign = json.loads((self.directory / "campaign" / "campaign.json").read_text(encoding="utf-8"))
        self.assertEqual([sample["sample_id"] for sample in campaign["samples"]], attempted)
        self.assertIn("summary could not be written", campaign["failure_reasons"][0])

    def test_a_final_record_that_cannot_be_written_keeps_the_samples_already_saved(self) -> None:
        # The record saved after the last sample holds every sample; the fallback must not replace it with a stub.
        original = driver.save_campaign

        def failing_final_save(*args) -> None:
            if len(args) > 5:
                raise TypeError("not JSON serializable")
            original(*args)

        driver.save_campaign = failing_final_save
        self.addCleanup(setattr, driver, "save_campaign", original)
        code, attempted = self.run_campaign(set(), RUN_COUNT="1")
        self.assertEqual(code, 1)
        campaign = json.loads((self.directory / "campaign" / "campaign.json").read_text(encoding="utf-8"))
        self.assertEqual([sample["sample_id"] for sample in campaign["samples"]], attempted)
        self.assertEqual(campaign["status"], "failed")
        self.assertIn("campaign record could not be written", campaign["failure_reasons"][-1])

    def test_a_clean_campaign_runs_every_sample_and_succeeds(self) -> None:
        code, attempted = self.run_campaign(set(), RUN_COUNT="1")
        self.assertEqual(code, 0)
        self.assertEqual(attempted, ["image-1-run1", "image-2-run1", "image-3-run1"])

    def test_malformed_dispatch_input_fails_before_any_runner_time_is_spent(self) -> None:
        valid_images = '[{"id":"image-1","image":"repo:tag"}]'
        for images, values in (
            ("[]", {}),
            ('[{"id":"..","image":"repo:tag"}]', {}),
            ('[{"id":"a b","image":"repo:tag"}]', {}),
            (valid_images, {"CLIENT_ENV": "FOO"}),
            (valid_images, {"EXPB_ENV_PASSTHROUGH": "bad-key=1"}),
            (valid_images, {"AMOUNT": "0"}),
            (valid_images, {"MEASUREMENT_MODE": "compute-warm", "ADDITIONAL_EXTRA_FLAGS": "--JsonRpc.GasCap=100"}),
        ):
            with self.subTest(images=images, **values):
                driver.run_sample = lambda *_args: self.fail("no sample may run")
                with environment(
                    EXPB_CAMPAIGN_DIR=str(self.directory / "rejected"),
                    EXPB_IMAGES_JSON=images,
                    **values,
                ):
                    self.assertEqual(driver.main(), 1)
                campaign = json.loads((self.directory / "rejected" / "campaign.json").read_text(encoding="utf-8"))
                self.assertEqual(campaign["status"], "failed")


class SummaryTests(unittest.TestCase):
    def test_averages_are_formatted_and_missing_ones_render_as_unavailable(self) -> None:
        def sample(sample_id: str, status: str, avg: float | None) -> dict:
            return {
                "sample_id": sample_id,
                "image_id": "image-1",
                "status": status,
                "metrics": {
                    "source": "SSE",
                    "count": 1,
                    "avg": avg,
                    "delivered": 1,
                    "ids": [1],
                    "request": {"avg": avg},
                    "outside": {"avg": None},
                    "mgas_s": None,
                },
                "sse_block_ids": [1],
            }

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            images = [{"id": "image-1", "image": "repo:tag"}]
            driver.write_summary(root, images, 2, [sample("image-1-run1", "success", 23.007412345678901), sample("image-1-run2", "failed", None)])
            summary = (root / "summary.md").read_text(encoding="utf-8")

        self.assertIn("| image-1-run1 | success | SSE | 1 | 23.0074 | 23.0074 | n/a | n/a | n/a |", summary)
        self.assertIn("| image-1-run2 | failed | SSE | 1 | n/a | n/a | n/a | n/a | n/a |", summary)
        # Ids are opaque; the summary has to say which full image reference each one ran.
        self.assertIn("- `image-1` → `repo:tag`", summary)
        self.assertIn("Image image-1: mean AVG=23.0074 ms;", summary)
        self.assertNotIn("None", summary)


if __name__ == "__main__":
    unittest.main()
