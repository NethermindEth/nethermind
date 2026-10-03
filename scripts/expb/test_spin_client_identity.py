# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from contextlib import contextmanager
from pathlib import Path
import re
import sys
import tempfile
from types import ModuleType
import unittest
from unittest.mock import Mock, patch

BUNDLE = Path(__file__).with_name("spin-diagnostic")
sys.path.insert(0, str(BUNDLE))
import scheduler_collector as sc
sys.path.pop(0)


def load_identity():
    path = BUNDLE / "client-identity.patch"
    header = "--- /dev/null\n+++ b/src/expb/payloads/executor/client_identity.py\n"
    source = path.read_text()
    if source.count(header) != 1:
        raise AssertionError("expected one client identity new-file patch")
    hunk, *lines = source.split(header, 1)[1].splitlines()
    match = re.fullmatch(r"@@ -0,0 \+1,(\d+) @@", hunk)
    if not match or len(lines) != int(match[1]) or not all(line.startswith("+") for line in lines):
        raise AssertionError("unexpected client identity new-file patch structure")
    module = ModuleType("spin_client_identity_under_test")
    with patch.dict(sys.modules, {"expb.payloads.executor.scheduler_collector": sc}):
        exec(compile("\n".join(line[1:] for line in lines) + "\n", str(path), "exec"), module.__dict__)
    return module


ci = load_identity()
REQUIRED = {"usage_usec": 100, "user_usec": 75, "system_usec": 25}


def encode_stats(values):
    return "".join(f"{key} {value}\n" for key, value in values.items()).encode("ascii")


class CpuStatTests(unittest.TestCase):
    @contextmanager
    def collector(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            process, group, output = root / "proc/42", root / "cgroup", root / "capture"
            for directory in [process, group, output]:
                directory.mkdir(parents=True)
            fields = ["S"] + ["0"] * 19
            fields[11], fields[12], fields[19] = "5", "2", "123"
            (process / "stat").write_text("42 (synthetic) " + " ".join(fields))
            (group / "cpu.stat").write_bytes(encode_stats(REQUIRED))
            (group / "cpu.max").write_text("max 100000\n")
            (group / "cpuset.cpus.effective").write_text("0-3\n")
            collector = ci.ClientIdentity.__new__(ci.ClientIdentity)
            collector.directory, collector.proc, collector.pid = output, root / "proc", 42
            collector.before = None
            collector.identity = {"process": {"start_ticks": 123}, "cgroup": "/synthetic"}
            collector._cgroup = Mock(return_value=("/synthetic", group))
            with patch.object(ci, "proc_identity", return_value=collector.identity["process"]):
                yield collector, group

    def test_preserves_required_and_documented_extension_counters_at_both_boundaries(self):
        for extra in [{}, {"core_sched.force_idle_usec": 0, "nice_usec": 3, "nr_periods": 17,
                           "nr_throttled": 2, "throttled_usec": 40, "nr_bursts": 1, "burst_usec": 5}]:
            with self.subTest(extra=extra), self.collector() as (collector, group):
                expected = REQUIRED | extra
                raw = encode_stats(expected)
                (group / "cpu.stat").write_bytes(raw)
                for boundary in ["before", "after"]:
                    counters = collector._counters()
                    self.assertEqual(counters["cpu_stat"], expected)
                    self.assertEqual((collector.directory / f"cpu-stat-{boundary}.raw").read_bytes(), raw)
                    collector.before = counters

    def test_rejects_malformed_duplicate_and_noninteger_fields_with_private_evidence(self):
        for suffix in [b"extra\n", b"extra 1 2\n", b"\n", b"usage_usec 101\n",
                       b"core_sched.force_idle_usec 1\ncore_sched.force_idle_usec 2\n",
                       b"core_sched.force_idle_usec -1\n", b"extra +1\n", b"extra 1.0\n", b"extra NaN\n",
                       b"core_sched..force_idle_usec 1\n", b"core_sched.force_idle_usec.extra 1\n",
                       b"bad/key 1\n", b"extra \xff\n", "extra ١\n".encode()]:
            for boundary in ["before", "after"]:
                with self.subTest(suffix=suffix, boundary=boundary), self.collector() as (collector, group):
                    if boundary == "after":
                        collector.before = collector._counters()
                    raw = encode_stats(REQUIRED) + suffix
                    (group / "cpu.stat").write_bytes(raw)
                    with self.assertRaises((sc.CaptureError, UnicodeDecodeError)):
                        collector._counters()
                    self.assertEqual((collector.directory / f"cpu-stat-{boundary}.raw").read_bytes(), raw)

    def test_each_basic_counter_is_required(self):
        for missing in REQUIRED:
            with self.subTest(missing=missing), self.collector() as (collector, group):
                raw = encode_stats({key: value for key, value in REQUIRED.items() if key != missing})
                (group / "cpu.stat").write_bytes(raw)
                with self.assertRaisesRegex(sc.CaptureError, "CPU usage counters unavailable"):
                    collector._counters()
                self.assertEqual((collector.directory / "cpu-stat-before.raw").read_bytes(), raw)

    def test_private_evidence_is_exclusive_and_bounded(self):
        for defect in ["existing", "oversized"]:
            with self.subTest(defect=defect), self.collector() as (collector, group):
                evidence = collector.directory / "cpu-stat-before.raw"
                if defect == "existing":
                    evidence.write_bytes(b"keep")
                    error = FileExistsError
                else:
                    (group / "cpu.stat").write_bytes(b"x" * 65537)
                    error = sc.CaptureError
                with self.assertRaises(error):
                    collector._counters()
                if defect == "existing":
                    self.assertEqual(evidence.read_bytes(), b"keep")
                else:
                    self.assertFalse(evidence.exists())


if __name__ == "__main__":
    unittest.main(verbosity=2)
