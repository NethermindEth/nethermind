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
        self.memory_anon = 0
        self.has_memory_stat = True
        self.read_bytes = 0
        self.write_bytes = 0
        self.write()

    def write(self):
        (self.root / "cpu.stat").write_text(
            f"usage_usec {self.cpu_usec}\nthrottled_usec {self.throttled_usec}\n", encoding="utf-8")
        (self.root / "memory.current").write_text(f"{self.memory_current}\n", encoding="utf-8")
        if self.has_memory_stat:
            (self.root / "memory.stat").write_text(
                f"anon {self.memory_anon}\nfile {self.memory_current - self.memory_anon}\n",
                encoding="utf-8")
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

    def _run(self, ticks, series_path=None):
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
        try:
            with contextlib.redirect_stdout(io.StringIO()):
                sample_resources.sample("node", str(self.out), 0.0, should_stop,
                                        series_path=series_path)
        finally:
            sample_resources._cgroup_dir = original
            sample_resources._container_id = original_id
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

    def test_anonymous_memory_is_reported_apart_from_reclaimable_page_cache(self):
        """memory.current climbs with page cache whatever the client does; anon is what OOMs."""
        summary = self._run([
            lambda c: (setattr(c, "memory_current", 1000), setattr(c, "memory_anon", 100)),
            lambda c: (setattr(c, "memory_current", 5000), setattr(c, "memory_anon", 300)),
        ])
        self.assertEqual(summary["memory_peak_bytes"], 5000)
        self.assertEqual(summary["memory_anon_peak_bytes"], 300)
        self.assertEqual(summary["memory_anon_avg_bytes"], 200)

    def test_first_and_last_anonymous_samples_state_the_windows_slope(self):
        summary = self._run([
            lambda c: setattr(c, "memory_anon", 100),
            lambda c: setattr(c, "memory_anon", 900),
            lambda c: setattr(c, "memory_anon", 400),
        ])
        self.assertEqual(summary["memory_anon_first_bytes"], 100)
        self.assertEqual(summary["memory_anon_last_bytes"], 400)

    def test_a_cgroup_without_memory_stat_reports_zero_rather_than_failing(self):
        """geth/reth images and older kernels are not required to expose anon accounting."""
        self.cgroup.has_memory_stat = False
        (self.dir / "memory.stat").unlink()
        summary = self._run([lambda c: setattr(c, "memory_current", 10)])
        self.assertEqual(summary["memory_anon_peak_bytes"], 0)
        self.assertEqual(summary["memory_peak_bytes"], 10)

    def test_the_series_records_every_tick_so_a_slope_can_be_read(self):
        series = self.dir / "resources.csv"
        self._run([
            lambda c: (setattr(c, "memory_current", 10), setattr(c, "memory_anon", 1)),
            lambda c: (setattr(c, "memory_current", 20), setattr(c, "memory_anon", 2)),
        ], series_path=str(series))
        rows = [line.split(",") for line in series.read_text(encoding="utf-8").splitlines()]
        self.assertEqual(rows[0], list(sample_resources.SERIES_HEADER))
        self.assertEqual([(r[1], r[2]) for r in rows[1:]], [("10", "1"), ("20", "2")])

    def test_no_series_file_is_written_when_none_was_asked_for(self):
        self._run([lambda c: setattr(c, "memory_current", 10)])
        self.assertEqual(list(self.dir.glob("*.csv")), [])

    def test_sampler_leaves_per_request_costs_to_normalize(self):
        summary = self._run([lambda c: setattr(c, "cpu_usec", 1000)])
        self.assertEqual(summary["requests"], 0)
        self.assertNotIn("cpu_ms_per_request", summary)


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

    def test_a_non_summary_file_is_rejected_without_a_traceback(self):
        self.path.write_text(json.dumps({"not": "a summary"}), encoding="utf-8")
        with self.assertRaises(sample_resources.ResourceSampleError):
            sample_resources.normalize(str(self.path), 10)


if __name__ == "__main__":
    unittest.main()
