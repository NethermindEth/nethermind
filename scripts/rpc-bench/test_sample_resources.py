#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Tests for the cgroup resource sampler.

The sampler runs on Linux against real cgroup files, so these drive it through a fabricated
cgroup directory and an injected stop predicate — the arithmetic is what matters, and getting it
wrong silently misreports how much machine a client costs.
"""

import contextlib
import importlib.util
import io
import json
import shutil
import tempfile
import unittest
from pathlib import Path

# The module is named with a hyphen, so it cannot be imported by name.
_SPEC = importlib.util.spec_from_file_location(
    "sample_resources", Path(__file__).with_name("sample-resources.py"))
sample_resources = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(sample_resources)


class FakeCgroup:
    """A cgroup v2 directory whose counters advance on demand."""

    def __init__(self, root: Path):
        self.root = root
        self.cpu_usec = 0
        self.throttled_usec = 0
        self.memory_current = 0
        self.read_bytes = 0
        self.write_bytes = 0
        self.write()

    def write(self):
        (self.root / "cpu.stat").write_text(
            f"usage_usec {self.cpu_usec}\nthrottled_usec {self.throttled_usec}\n", encoding="utf-8")
        (self.root / "memory.current").write_text(f"{self.memory_current}\n", encoding="utf-8")
        (self.root / "memory.peak").write_text("999999999999\n", encoding="utf-8")
        (self.root / "io.stat").write_text(
            f"8:0 rbytes={self.read_bytes} wbytes={self.write_bytes}\n", encoding="utf-8")
        for name in ("cpu", "io", "memory"):
            (self.root / f"{name}.pressure").write_text("some avg10=0.00 total=0\n", encoding="utf-8")


class SamplerArithmeticTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.cgroup = FakeCgroup(self.dir)
        self.out = self.dir / "resources.json"

    def tearDown(self):
        self.tmp.cleanup()

    def _run(self, ticks, frequencies=None, cppc_root=None):
        """Drive the loop for len(ticks) iterations, applying each tick's mutations."""
        state = {"i": 0}

        def should_stop():
            index = state["i"]
            if index >= len(ticks):
                return True
            ticks[index](self.cgroup)
            self.cgroup.write()
            state["i"] += 1
            return False

        original = sample_resources._cgroup_dir
        sample_resources._cgroup_dir = lambda _cid: self.dir
        original_id = sample_resources._container_id
        sample_resources._container_id = lambda _name: "cid"
        original_frequencies = sample_resources._frequency_observations
        original_frequency_root = sample_resources.CPU_FREQ_ROOT
        if cppc_root is None:
            readings = iter(frequencies or ({source: [] for source in sample_resources.CPU_FREQ_SOURCES},) * len(ticks))
            sample_resources._frequency_observations = lambda *_: next(readings)
        else:
            sample_resources.CPU_FREQ_ROOT = cppc_root
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                sample_resources.sample("node", str(self.out), 0.0, should_stop)
        finally:
            sample_resources._cgroup_dir = original
            sample_resources._container_id = original_id
            sample_resources._frequency_observations = original_frequencies
            sample_resources.CPU_FREQ_ROOT = original_frequency_root
        return json.loads(self.out.read_text(encoding="utf-8"))

    def test_throttling_is_a_delta_not_the_cgroup_lifetime_value(self):
        """Startup throttling belongs to the node's lifetime, not to the measured cell."""
        self.cgroup.throttled_usec = 5_000_000  # accrued long before the cell
        self.cgroup.write()
        summary = self._run([lambda c: setattr(c, "throttled_usec", 5_000_400)])
        self.assertEqual(summary["cpu_throttled_usec"], 400)

    def test_memory_peak_is_the_sampled_maximum_not_the_lifetime_high_water_mark(self):
        """memory.peak covers warmup; a cell-local peak must come from the samples."""
        summary = self._run([
            lambda c: setattr(c, "memory_current", 100),
            lambda c: setattr(c, "memory_current", 700),
            lambda c: setattr(c, "memory_current", 300),
        ])
        self.assertEqual(summary["memory_peak_bytes"], 700)
        self.assertEqual(summary["memory_avg_bytes"], (100 + 700 + 300) // 3)

    def test_sampler_leaves_per_request_costs_to_normalize(self):
        summary = self._run([lambda c: setattr(c, "cpu_usec", 1000)])
        self.assertEqual(summary["requests"], 0)
        self.assertNotIn("cpu_ms_per_request", summary)

    def test_frequency_aggregation_omits_missing_sources(self):
        summary = self._run(
            [lambda c: setattr(c, "cpu_usec", 1000), lambda c: setattr(c, "cpu_usec", 2000)],
            frequencies=[
                {"scaling_cur_freq": [3_700_000, 3_800_000], "cpuinfo_cur_freq": []},
                {"scaling_cur_freq": [3_900_000], "cpuinfo_cur_freq": []},
            ])
        self.assertEqual(summary["cpu_frequency_khz"]["scaling_cur_freq"], {
            "sample_count": 2, "observation_count": 3, "avg_khz": 3_800_000,
            "min_khz": 3_700_000, "max_khz": 3_900_000})
        self.assertNotIn("cpuinfo_cur_freq", summary["cpu_frequency_khz"])
        self.assertEqual(summary["cpu_frequency_unavailable_sources"], [
            "cpuinfo_cur_freq", "cppc_delivered_perf"])
        self.assertNotIn("estimated_cycles_per_request", summary)

    def test_cppc_frequency_is_aggregated_from_counter_deltas(self):
        root = self.dir / "sys" / "devices" / "system" / "cpu"

        def write_cpu(cpu, delivered, reference, reference_perf=80, nominal_perf=100, nominal_freq=3800):
            directory = root / cpu / "acpi_cppc"
            directory.mkdir(parents=True, exist_ok=True)
            (directory / "feedback_ctrs").write_text(
                f"ref:{reference} del:{delivered}\n", encoding="utf-8")
            for name, value in (("reference_perf", reference_perf), ("nominal_perf", nominal_perf),
                                ("nominal_freq", nominal_freq)):
                (directory / name).write_text(f"{value}\n", encoding="utf-8")

        summary = self._run(
            [lambda c: write_cpu("cpu0", 1000, 500), lambda c: write_cpu("cpu0", 1100, 600)],
            cppc_root=root)
        self.assertEqual(summary["cpu_frequency_khz"]["cppc_delivered_perf"], {
            "sample_count": 1, "observation_count": 1, "avg_khz": 3_040_000,
            "min_khz": 3_040_000, "max_khz": 3_040_000})
        self.assertEqual(summary["cpu_frequency_unavailable_sources"], [
            "scaling_cur_freq", "cpuinfo_cur_freq"])

    def test_cppc_frequency_rejects_missing_malformed_reset_and_disappearing_counters(self):
        root = self.dir / "sys" / "devices" / "system" / "cpu"
        directory = root / "cpu0" / "acpi_cppc"

        def write_feedback(delivered, reference, valid=True):
            directory.mkdir(parents=True, exist_ok=True)
            text = (f"ref:{reference} del:{delivered}\n" if valid else "broken")
            (directory / "feedback_ctrs").write_text(text, encoding="utf-8")
            for name, value in (("reference_perf", 100), ("nominal_perf", 100), ("nominal_freq", 3800)):
                (directory / name).write_text(f"{value}\n", encoding="utf-8")

        sampler = sample_resources._CppcFrequencySampler(root)
        self.assertEqual(sampler.sample(), [])
        write_feedback(1000, 500)
        self.assertEqual(sampler.sample(), [])
        write_feedback(900, 600)  # counter reset
        self.assertEqual(sampler.sample(), [])
        write_feedback(1000, 600, valid=False)  # malformed feedback file
        self.assertEqual(sampler.sample(), [])
        shutil.rmtree(directory)
        self.assertEqual(sampler.sample(), [])  # disappearing CPU drops its baseline
        write_feedback(1000, 600)
        self.assertEqual(sampler.sample(), [])
        write_feedback(1100, 700)
        self.assertEqual(sampler.sample(), [3_800_000.0])


class NormalizeTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "resources.json"

    def tearDown(self):
        self.tmp.cleanup()

    def _write(self, **overrides):
        summary = {"cpu_seconds": 45.0, "cpu_avg_cores": 3.0, "io_read_bytes": 2000,
                   "memory_peak_bytes": 1, "requests": 0}
        summary.update(overrides)
        self.path.write_text(json.dumps(summary), encoding="utf-8")

    def test_per_request_costs_use_the_delivered_count(self):
        self._write()
        with contextlib.redirect_stdout(io.StringIO()):
            sample_resources.normalize(str(self.path), 1000)
        summary = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertEqual(summary["requests"], 1000)
        self.assertEqual(summary["cpu_ms_per_request"], 45.0)
        self.assertEqual(summary["io_read_bytes_per_request"], 2.0)

    def test_renormalizing_replaces_rather_than_keeps_a_stale_rate(self):
        """A second pass with a different count must not leave the first pass's figures behind."""
        self._write()
        with contextlib.redirect_stdout(io.StringIO()):
            sample_resources.normalize(str(self.path), 1000)
            sample_resources.normalize(str(self.path), 500)
        summary = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertEqual(summary["cpu_ms_per_request"], 90.0)

    def test_normalize_derives_cycles_per_request_for_available_frequency_sources(self):
        self._write(cpu_frequency_khz={
            "scaling_cur_freq": {"sample_count": 2, "observation_count": 4, "avg_khz": 3_800_000,
                                  "min_khz": 3_700_000, "max_khz": 3_900_000}})
        with contextlib.redirect_stdout(io.StringIO()):
            sample_resources.normalize(str(self.path), 1000)
        summary = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertEqual(summary["estimated_cycles_per_request"], {"scaling_cur_freq": 171_000_000.0})

    def test_normalize_derives_cycles_per_request_from_cppc_frequency(self):
        self._write(cpu_frequency_khz={
            "cppc_delivered_perf": {"sample_count": 1, "observation_count": 2, "avg_khz": 3_040_000,
                                      "min_khz": 2_800_000, "max_khz": 3_280_000}})
        with contextlib.redirect_stdout(io.StringIO()):
            sample_resources.normalize(str(self.path), 1000)
        summary = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertEqual(summary["estimated_cycles_per_request"], {"cppc_delivered_perf": 136_800_000.0})

    def test_normalize_does_not_invent_cycle_estimates_without_frequency(self):
        self._write()
        with contextlib.redirect_stdout(io.StringIO()):
            sample_resources.normalize(str(self.path), 1000)
        summary = json.loads(self.path.read_text(encoding="utf-8"))
        self.assertNotIn("estimated_cycles_per_request", summary)

    def test_a_non_summary_file_is_rejected_without_a_traceback(self):
        self.path.write_text(json.dumps({"not": "a summary"}), encoding="utf-8")
        with self.assertRaises(sample_resources.ResourceSampleError):
            sample_resources.normalize(str(self.path), 10)


if __name__ == "__main__":
    unittest.main()
