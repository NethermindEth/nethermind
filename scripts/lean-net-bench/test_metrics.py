#!/usr/bin/env python3
"""Deterministic reporting checks; no network namespaces, services or native proofs."""
import contextlib
import io
import os
import sys
import importlib.util
import hashlib
import json
from pathlib import Path
import unittest
import tempfile
import subprocess
import xml.etree.ElementTree as ET
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent

def load(name):
    spec = importlib.util.spec_from_file_location(name, HERE / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

controller = load('netbench')
report = load('report')
matrix = load('matrix')

class MetricsTests(unittest.TestCase):
    @unittest.skipUnless(hasattr(os, "wait4"), "native capture requires macOS/Linux wait4")
    def test_native_capture_records_profile_and_historical_case_names(self):
        with tempfile.TemporaryDirectory() as directory:
            probe = Path(directory, "fake-probe")
            key = "a" * 64
            probe.write_text(f"#!{sys.executable}\nprint('mode,profile_key')\nprint('REGRESSION-NOT-A-PROOF,{key}')\n")
            probe.chmod(0o755)
            out = Path(directory, "captured")
            command = [sys.executable, str(HERE / "native-cost/capture.py"), "--exe", str(probe), "--out", str(out),
                       "--repetitions", "2", "--fork-revision", "b" * 40, "--profile-key", key]
            capture = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(capture.returncode, 0, capture.stderr)
            self.assertTrue((out / "sphincs1.csv").exists())
            self.assertTrue((out / "sphincs1-r2.csv").exists())
            self.assertFalse((out / "sphincs1-r1.csv").exists())
            metadata = json.loads((out / "sphincs1.json").read_text())
            self.assertEqual(metadata["profileKey"], key)
            self.assertEqual(metadata["forkRevision"], "b" * 40)
            self.assertEqual(metadata["repetitions"], 2)
            self.assertEqual(metadata["binarySha256"], hashlib.sha256(probe.read_bytes()).hexdigest())
            wrong = Path(directory, "wrong-profile")
            command[command.index(str(out))] = str(wrong)
            command[-1] = "c" * 64
            capture = subprocess.run(command, capture_output=True, text=True)
            self.assertEqual(capture.returncode, 1, capture.stderr)
            retained = json.loads((wrong / "prepare_parents.json").read_text())
            self.assertEqual(retained["exitCode"], 0)
            self.assertIn("expected profile key", retained["error"])
            self.assertFalse((wrong / "sphincs1.csv").exists())

    def test_matrix_resume_preserves_every_recorded_attempt(self):
        with tempfile.TemporaryDirectory() as directory:
            results = Path(directory, "results.jsonl")
            previous = [
                {"label": "load-rlpx-tx1-r1-rep1", "missing": 0},
                {"label": "load-rlpx-tx1-r8-rep1", "missing": 2},
                {"label": "load-rlpx-tx1-r32-rep1", "error": "deadline"},
            ]
            original = "".join(json.dumps(row) + "\n" for row in previous)
            results.write_text(original)
            with patch("sys.argv", ["matrix.py", "--test-dll", "unused.dll", "--results", str(results),
                                     "--repetitions", "1", "--match", "load-rlpx-tx1-"]), \
                    patch.object(matrix.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, '{"missing":0}')) as launch, \
                    contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(matrix.main(), 0)
            self.assertEqual(launch.call_count, 1)
            self.assertIn("load-rlpx-tx1-r64-rep1", launch.call_args.args[0])
            self.assertEqual(results.read_text(), original)

    def test_existing_attempt_directory_cannot_supply_stale_provenance(self):
        with tempfile.TemporaryDirectory() as directory:
            old = Path(directory, "same-label")
            old.mkdir()
            provenance = old / "provenance.json"
            provenance.write_text('{"source_revision":"STALE"}')
            results = Path(directory, "results.jsonl")
            original = '{"label":"same-label","missing":2}\n'
            results.write_text(original)
            stderr = io.StringIO()
            with patch("sys.argv", ["netbench.py", "run", "--test-dll", "unused.dll", "--out", directory,
                                     "--label", "same-label", "--results", str(results)]), \
                    contextlib.redirect_stderr(stderr), contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaises(SystemExit) as error:
                    controller.main()
            self.assertEqual(error.exception.code, 2)
            self.assertIn("use a new attempt label", stderr.getvalue())
            self.assertEqual(results.read_text(), original)
            self.assertEqual(provenance.read_text(), '{"source_revision":"STALE"}')

    def test_broadcast_rlpx_is_rejected_before_creating_an_attempt(self):
        with patch("sys.argv", ["netbench.py", "run", "--test-dll", "unused.dll", "--binding", "rlpx",
                                 "--broadcast", "--kind", "2", "--interval-ms", "12000"]), \
                patch.object(controller, "run") as launch, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                controller.main()
        self.assertEqual(error.exception.code, 2)
        launch.assert_not_called()

    def test_two_bursts_cover_two_release_intervals(self):
        records = [(0, 0)] * 4 + [(1_000_000_000, 1_000_000_000)] * 4
        row = controller.origination_metrics(records, 1000, 16, 1)
        self.assertEqual(row['released_objects'], 8)
        self.assertEqual(row['actual_offered_window_s'], 2)
        self.assertEqual(row['actual_offered_objects_per_s'], 4)
        self.assertEqual(row['actual_offered_tx_per_s'], 64)
        self.assertEqual(row['origin_schedule_lateness_ms']['max'], 0)

    def test_delayed_origin_changes_rate_and_lateness(self):
        row = controller.origination_metrics([(0, 0), (2_000_000_000, 1_000_000_000)], 1000, 1, 1)
        self.assertAlmostEqual(row['actual_offered_objects_per_s'], 2 / 3)
        self.assertEqual(row['origin_schedule_lateness_ms']['p95'], 1000)
        self.assertEqual(row['actual_origin_span_s'], 2)

    def test_no_observed_origin_is_unknown_rate(self):
        row = controller.origination_metrics([], 1000, 1, 1)
        self.assertEqual(row['released_objects'], 0)
        self.assertIsNone(row['actual_offered_objects_per_s'])
        self.assertIsNone(row['origin_schedule_lateness_ms']['p95'])

    def test_block_sidecar_has_no_transaction_rate(self):
        row = controller.origination_metrics([(0, 0)], 12000, 1, 2)
        self.assertAlmostEqual(row['actual_offered_objects_per_s'], 1 / 12)
        self.assertIsNone(row['actual_offered_tx_per_s'])

    def test_failed_run_recovers_configured_rate(self):
        row = {'error': 'timeout', 'provenance': {'arguments': {'burst': 8, 'interval_ms': 1000}}}
        self.assertEqual(report.offered_rate(row), 8)
        self.assertTrue(report.incomplete(row))
        self.assertEqual(report.measured_values([row], 'goodput_tx_per_s_per_receiver'), [])

    def test_failed_cleanup_preserves_measurements_and_marks_run_incomplete(self):
        row = {"missing": 0, "completed": 6, "goodput_tx_per_s_per_receiver": 12.5}
        controller.retain_process_failures(row, {0: 0, 1: 1})
        self.assertEqual(row["node_exit_codes"], {"0": 0, "1": 1})
        self.assertIn("node 1: 1", row["error"])
        self.assertEqual(row["goodput_tx_per_s_per_receiver"], 12.5)
        self.assertEqual(row["completed"], 6)
        self.assertTrue(report.incomplete(row))

    def test_process_failure_retains_existing_error_and_forced_termination(self):
        row = {"error": "only one result", "missing": 2}
        controller.retain_process_failures(row, {0: -9, 1: None})
        self.assertTrue(row["error"].startswith("only one result;"))
        self.assertIn("node 0: -9", row["error"])
        self.assertIn("node 1: did not exit", row["error"])
        self.assertTrue(report.incomplete(row))

    def test_controller_deadline_remains_failure_even_if_survivors_finish_during_grace(self):
        row = {"missing": 0, "completion_wait": {"all_nodes_done": False, "abort_reason": "deadline"}}
        controller.retain_process_failures(row, {0: 0, 1: 0})
        self.assertIn("completion wait aborted: deadline", row["error"])
        self.assertTrue(report.incomplete(row))

    def test_successful_processes_do_not_invent_failure(self):
        row = {"missing": 0}
        controller.retain_process_failures(row, {0: 0, 1: 0})
        self.assertNotIn("error", row)
        self.assertFalse(report.incomplete(row))

    def test_missing_origin_retains_receiver_delivery_without_inventing_latency(self):
        results = {1: {"completed": {"0": 5_000_000_000}, "body_bytes": 100}}
        row = controller.receiver_metrics(results, 2, [(0, 1)], range(1), lambda _: 0)
        self.assertEqual(row["validated_receiver_deliveries"], 1)
        self.assertEqual(row["completed"], 1)
        self.assertEqual(row["missing"], 0)
        self.assertEqual(row["latency_unknown_objects"], 1)
        self.assertEqual(row["latency_unknown_receiver_deliveries"], 1)
        self.assertEqual(row["latency_sampled_objects"], 0)
        self.assertIsNone(row["latency_ms"]["p50"])
        controller.retain_process_failures(row, {0: 1, 1: 0})
        self.assertTrue(report.incomplete(row))
        self.assertEqual(row["validated_receiver_deliveries"], 1)

    def test_partial_receivers_are_counted_but_not_all_receiver_latency_samples(self):
        results = {0: {"originated": {"0": 1_000_000_000}},
                   1: {"completed": {"0": 2_000_000_000}}}
        row = controller.receiver_metrics(results, 3, [(0, 1), (1, 2)], range(1), lambda _: 0)
        self.assertEqual(row["validated_receiver_deliveries"], 1)
        self.assertEqual(row["expected_receiver_deliveries"], 2)
        self.assertEqual(row["completed"], 0)
        self.assertEqual(row["missing"], 1)
        self.assertEqual(row["latency_by_hops_p50_ms"], {"1": 1000})
        self.assertEqual(row["latency_sampled_objects"], 0)
        self.assertEqual(row["latency_unknown_receiver_deliveries"], 0)

    def test_all_receivers_sample_slowest_latency_and_keep_unknown_objects_separate(self):
        results = {0: {"originated": {"0": 1_000_000_000}},
                   1: {"completed": {"0": 2_000_000_000, "1": 5_000_000_000}},
                   2: {"completed": {"0": 3_000_000_000, "1": 6_000_000_000}}}
        row = controller.receiver_metrics(results, 3, [(0, 1), (1, 2)], range(2), lambda _: 0)
        self.assertEqual(row["validated_receiver_deliveries"], 4)
        self.assertEqual(row["completed"], 2)
        self.assertEqual(row["missing"], 0)
        self.assertEqual(row["latency_sampled_objects"], 1)
        self.assertEqual(row["latency_unknown_objects"], 1)
        self.assertEqual(row["latency_ms"]["p95"], 2000)

    def test_goodput_window_retains_attempt_and_late_delivery_time(self):
        result = {"measured_ns": 1_000_000_000, "finished_ns": 3_000_000_000,
                  "completed": {"0": 10_000_000_000}}
        self.assertEqual(controller.goodput_window({1: result}, 6, 4), 9)
        self.assertEqual(controller.goodput_window({1: result}, 6, 12), 12)
        self.assertEqual(controller.goodput_window({1: result}, 20, 4), 20)
        self.assertIsNone(controller.goodput_window({}, 6, 4))

    def test_wire_baseline_finishes_before_release_file_even_with_slow_serial_reads(self):
        now = [0]
        with tempfile.TemporaryDirectory() as directory:
            def sample(node):
                self.assertFalse(Path(directory, "measure").exists(), "nodes must still be waiting for release")
                now[0] += 50_000_000
                return {"tx_bytes": node + 10}
            measured, values, window = controller.prepare_measurement(directory, 4, True, sample, lambda: now[0])
            self.assertEqual(int(Path(directory, "measure").read_text()), measured)
        self.assertEqual(values, {node: {"tx_bytes": node + 10} for node in range(4)})
        self.assertEqual(window["before_started_unix_ns"], 0)
        self.assertEqual(window["before_finished_unix_ns"], 200_000_000)
        self.assertLess(window["before_finished_unix_ns"], measured)

    def test_aborted_wire_capture_retains_counters_but_is_not_comparable(self):
        for complete in (True, False):
            with self.subTest(complete=complete):
                window = {"before_started_unix_ns": 1, "before_finished_unix_ns": 2}
                times = iter([100, 200])
                after = controller.capture_wire_end(window, 2, lambda node: {"tx_bytes": node + 100}, complete, lambda: next(times))
                self.assertEqual(after, {0: {"tx_bytes": 100}, 1: {"tx_bytes": 101}})
                self.assertEqual(window["comparison_eligible"], complete)
                self.assertEqual(window["partial_scope"], not complete)
                self.assertEqual(window["after_started_unix_ns"], 100)
                self.assertEqual(window["after_finished_unix_ns"], 200)

    def test_failed_peer_allows_survivor_deadline_to_retain_partial_result(self):
        with tempfile.TemporaryDirectory() as directory:
            process, log = Mock(), Mock()
            partial = Path(directory, "result.1.json")
            def finish(timeout):
                if timeout < 90:
                    raise subprocess.TimeoutExpired("node", timeout)
                partial.write_text('{"completed":{"0":123}}')
            process.wait.side_effect = finish
            self.assertEqual(controller.finish_processes([(process, log)], 90, False), 120)
            self.assertTrue(partial.exists())
            process.kill.assert_not_called()
            log.close.assert_called_once()

    def test_stalled_survivor_is_still_bounded_and_reaped(self):
        process, log = Mock(), Mock()
        process.wait.side_effect = [subprocess.TimeoutExpired("node", 120), 1]
        self.assertEqual(controller.finish_processes([(process, log)], 90, False), 120)
        process.kill.assert_called_once()
        self.assertEqual(process.wait.call_args_list[1].kwargs["timeout"], 10)
        log.close.assert_called_once()

    def test_cpu_comparison_requires_timely_clean_snapshots(self):
        row = {"idle_snapshot_ns": 1_000_000_000, "snapshot_before_ns": 3_000_000_000,
               "snapshot_after_ns": 4_000_000_000, "measured_ns": 3_000_000_000,
               "warmup_completed_at_idle": 3, "warmup_completed_at_start": 3,
               "originated": {"3": 3_001_000_000}, "completed": {"3": 3_500_000_000}}
        self.assertTrue(controller.cpu_snapshot_quality({0: row}, 3, 1)["eligible"])
        for changes, reason in [({"idle_snapshot_ns": 1_050_000_000}, "late idle"),
                                ({"snapshot_before_ns": 3_050_000_000}, "snapshot"),
                                ({"warmup_completed_at_idle": 2}, "unfinished warmup"),
                                ({"originated": {"3": 2_999_000_000}}, "activity precedes")]:
            with self.subTest(changes=changes):
                quality = controller.cpu_snapshot_quality({0: dict(row, **changes)}, 3, 1)
                self.assertFalse(quality["eligible"])
                self.assertTrue(any(reason in issue for issue in quality["issues"]))
        self.assertFalse(controller.cpu_snapshot_quality({0: row}, 3, 2)["eligible"])
        self.assertFalse(controller.cpu_snapshot_quality({0: {}}, 3, 1)["eligible"])

    def test_comparison_rejects_mixed_campaigns_including_failed_rows(self):
        provenance = {"source_revision": "source-a", "source_archive_sha256": "archive-a", "test_dll_sha256": "dll-a"}
        valid = {"provenance": provenance, "missing": 0}
        report.validate_campaign([valid, {"provenance": dict(provenance), "error": "timeout"}])
        for field in provenance:
            with self.subTest(field=field):
                diagnostic = {"provenance": dict(provenance, **{field: "different"}), "error": "cleanup failed"}
                with self.assertRaisesRegex(ValueError, "Mixed transport campaigns"):
                    report.validate_campaign([valid, diagnostic])
                report.validate_campaign([diagnostic])
        with self.assertRaisesRegex(ValueError, "Mixed transport campaigns"):
            report.validate_campaign([valid, {"error": "missing provenance"}])
        report.validate_campaign([{"error": "old diagnostic without provenance"}])

    def test_warmup_barrier_waits_for_every_node(self):
        for nodes in (2, 4):
            with self.subTest(nodes=nodes), tempfile.TemporaryDirectory() as directory:
                for node in range(nodes - 1):
                    Path(directory, f"warmup.{node}").write_text(str(100 + node))
                process = Mock()
                process.poll.return_value = None
                def finish_last(_):
                    Path(directory, f"warmup.{nodes - 1}").write_text("200")
                with patch.object(controller.time, "sleep", side_effect=finish_last) as sleep:
                    status = controller.wait_warmup(directory, nodes, [(process, None)], 5)
                sleep.assert_called_once()
                self.assertEqual(status["completed_nodes"], nodes)
                self.assertEqual(status["completed_unix_ns"][str(nodes - 1)], 200)
                self.assertNotIn("error", status)

    def test_warmup_timeout_or_process_exit_retains_partial_evidence(self):
        for timeout, exit_code in ((0, None), (5, 1)):
            with self.subTest(timeout=timeout, exit_code=exit_code), tempfile.TemporaryDirectory() as directory:
                Path(directory, "warmup.0").write_text("123")
                process = Mock()
                process.poll.return_value = exit_code
                status = controller.wait_warmup(directory, 2, [(process, None)], timeout)
                self.assertEqual(status["completed_nodes"], 1)
                self.assertEqual(status["completed_unix_ns"], {"0": 123})
                self.assertIn("error", status)
                self.assertIn("observed_unix_ns", status)

    def test_broadcast_measurement_uses_fresh_slot_after_baseline_reads(self):
        genesis = 1_000_000_000
        for earliest in (genesis + 11_999_999_999, genesis + 12_000_000_000, genesis + 12_000_000_001):
            with self.subTest(earliest=earliest):
                start = controller.broadcast_start(earliest, genesis)
                self.assertGreaterEqual(start, earliest)
                self.assertEqual((start - genesis) % 12_000_000_000, 100_000_000)
        with tempfile.TemporaryDirectory() as directory:
            now = [genesis + 1_000_000_000]
            def sample(node):
                self.assertFalse(Path(directory, "measure").exists())
                now[0] += 10_000_000_000
                return {"tx_bytes": node}
            measured, _, _ = controller.prepare_measurement(directory, 2, True, sample, lambda: now[0], genesis)
            self.assertEqual(measured, genesis + 36_000_000_000 + 100_000_000)
            self.assertGreaterEqual(measured, now[0] + 4_000_000_000)
            self.assertEqual(int(Path(directory, "measure").read_text()), measured)

    def test_partial_measurement_is_retained_but_not_complete(self):
        rows = [{'missing': 2, 'goodput_tx_per_s_per_receiver': 4}, {'error': 'timeout'}]
        self.assertEqual(report.measured_values(rows, 'goodput_tx_per_s_per_receiver'), [4])
        self.assertTrue(report.incomplete(rows[0]))
        self.assertFalse(report.incomplete({'missing': 0}))

    @staticmethod
    def report_row(label="load-rlpx-tx16-r8-rep1", binding="rlpx", rtt=50):
        arguments = dict(binding=binding, topology="pair", kind=1, transactions_per_object=16,
                         proof_bytes=293448, objects=48, warmup=2, burst=8, interval_ms=1000,
                         origins="0", verify_ms=0, rate_mbit=100, loss=0, broadcast=False,
                         tiered=False, cpus=None, sysctl=[], rtt_ms=rtt)
        return dict(arguments, label=label, missing=0, body_bytes=300000,
                    validated_receiver_deliveries=48, expected_receiver_deliveries=48,
                    goodput_tx_per_s_per_receiver=100, goodput_mbit_per_s_per_receiver=15,
                    actual_offered_objects_per_s=8, actual_offered_tx_per_s=128,
                    released_objects=48, latency_ms={"p95": 250}, cpu_comparison_eligible=True,
                    cpu_net_ms_per_object=3, cpu_ms_total=150, peak_rss_mb_max=500,
                    wire_bytes=28800000, wire_counter_window={"comparison_eligible": True},
                    redials=0, peers_lost_at_end=0,
                    provenance={"source_revision": "RENDER-REGRESSION-NOT-A-MEASUREMENT",
                                "source_archive_sha256": "fixture", "test_dll_sha256": "fixture",
                                "arguments": arguments})

    def test_rtt_comparison_requires_identical_workload_and_keeps_failed_attempts(self):
        low = self.report_row()
        high = self.report_row("rtt200-rlpx-tx16-r8-rep1", rtt=200)
        high["error"] = "timeout"
        self.assertEqual(report.matched_rtt_rows([low, high]), [low, high])
        for field, value in (("proof_bytes", 300000), ("objects", 49), ("loss", 1),
                             ("warmup", 1), ("origins", "0,1"), ("binding", "ethp2p")):
            with self.subTest(field=field):
                changed = dict(high, **{field: value})
                self.assertEqual(report.matched_rtt_rows([low, changed]), [])
        unknown = dict(high)
        unknown.pop("warmup")
        unknown["provenance"] = {}
        self.assertEqual(report.matched_rtt_rows([low, unknown]), [])

    def test_resource_comparisons_require_complete_explicitly_eligible_windows(self):
        row = self.report_row()
        self.assertEqual(report.comparison_cpu(row), 3)
        self.assertEqual(report.wire_ratio(row), 2)
        self.assertEqual(report.wire_ratio(dict(row, validated_receiver_deliveries=96)), 1)
        for eligible in (False, None, 1):
            with self.subTest(eligible=eligible):
                self.assertIsNone(report.comparison_cpu(dict(row, cpu_comparison_eligible=eligible)))
                self.assertIsNone(report.wire_ratio(dict(row, wire_counter_window={"comparison_eligible": eligible})))
        for changed in (dict(row, error="cleanup failed"), dict(row, missing=1)):
            self.assertIsNone(report.comparison_cpu(changed))
            self.assertIsNone(report.wire_ratio(changed))
            self.assertIsNone(report.complete_p95(changed))
            self.assertEqual(report.delivery_percent(changed), 100)
        self.assertIsNone(report.wire_ratio(dict(row, body_bytes=None)))
        self.assertIsNone(report.wire_ratio(dict(row, validated_receiver_deliveries=0)))
        self.assertEqual(report.wire_ratio(dict(row, wire_bytes=0)), 0)

    def test_load_window_scope_uses_configured_duration_and_retains_unknowns(self):
        short = self.report_row()
        long = dict(short, objects=240)
        self.assertIn("Scheduled release: 6 s.", report.load_window_scope([short]))
        self.assertIn("Scheduled release: 30 s.", report.load_window_scope([long]))
        self.assertIn("Scheduled release: 6 s, 30 s.", report.load_window_scope([short, long]))
        self.assertIn("Scheduled release: 3 s.", report.load_window_scope([dict(short, objects=5, burst=2)]))
        self.assertIn("Scheduled release: 6 s.", report.load_window_scope([dict(short, offered_objects_per_s=999)]))
        self.assertIn("Scheduled release: 6 s.", report.load_window_scope([{"provenance": short["provenance"]}]))
        for unknown in ({}, dict(short, objects=True), dict(short, burst=float("nan")),
                        dict(short, provenance={}, interval_ms=None)):
            with self.subTest(unknown=unknown):
                self.assertIn("duration unavailable", report.load_window_scope([unknown]))
                self.assertIn("Some attempt durations unavailable", report.load_window_scope([short, unknown]))

    def test_overload_counters_keep_expiries_and_only_bound_never_fetched(self):
        row = dict(self.report_row(), validated_receiver_deliveries=24,
                   events={"1/fetch_started": 27, "1/reassembled": 24, "1/expired": 3})
        self.assertEqual(report.event_count(row, "expired"), 3)
        self.assertEqual(report.minimum_never_fetched(row), 21)
        self.assertEqual(report.minimum_never_fetched(dict(row, events={"1/fetch_started": 50})), 0)
        self.assertIsNone(report.minimum_never_fetched(dict(row, events={"1/fetch_started": 23})))
        for unknown in ({}, {"1/expired": True}, {"1/expired": -1}):
            with self.subTest(unknown=unknown):
                self.assertIsNone(report.event_count({} if not unknown else {"events": unknown}, "expired"))

    def test_report_renders_partial_errors_and_scoped_comparisons(self):
        try:
            import matplotlib
            from matplotlib.axes import Axes
        except ImportError:
            self.skipTest("matplotlib required for rendering regression")
        rows = []
        for binding in ("rlpx", "ethp2p"):
            for rtt in (50, 200):
                prefix = "load" if rtt == 50 else "rtt200"
                rows.append(self.report_row(f"{prefix}-{binding}-tx16-r8-rep1", binding, rtt))
            for topology, rate, loss in (("pair", 10, 0), ("pair", 100, 1), ("chain4", 100, 0)):
                row = self.report_row(f"link-{binding}-{topology}-{rate}-{loss}-rep1", binding)
                row.update(topology=topology, rate_mbit=rate, loss=loss)
                rows.append(row)
            row = self.report_row(f"size-{binding}-8388608-rep1", binding)
            row["proof_bytes"] = 8388608
            rows.append(row)
        rows[3].update(error="decoder failed <fixture>", missing=24, validated_receiver_deliveries=24,
                       events={"1/fetch_started": 27, "1/reassembled": 24, "1/expired": 3})
        rows.append(dict(self.report_row("load-rlpx-tx16-r8-rep2"), missing=24,
                         validated_receiver_deliveries=24, goodput_tx_per_s_per_receiver=50))
        rows.append(dict(self.report_row("size-rlpx-8388608-rep2"), proof_bytes=8388608,
                         goodput_mbit_per_s_per_receiver=30, latency_ms={"p95": 500}))
        for loss in (0, 1):
            row = self.report_row(f"broadcast-ethp2p-l{loss}-rep1", "ethp2p")
            row.update(loss=loss, broadcast=True, kind=2, broadcast_shards_accepted_measured=5,
                       broadcast_shards_accepted_total_including_warmup=7)
            rows.append(row)
        rows.append({"label": "load-rlpx-tx1-r64-rep1", "binding": "rlpx", "transactions_per_object": 1,
                     "offered_objects_per_s": 64, "error": "no result <fixture>", "provenance": rows[0]["provenance"]})
        with tempfile.TemporaryDirectory() as directory:
            raw = Path(directory, "fixture.jsonl")
            raw.write_text("".join(json.dumps(row) + "\n" for row in rows))
            out = Path(directory, "report")
            calls = []
            draw_errorbar = Axes.errorbar
            def record_errorbar(axis, *args, **kwargs):
                calls.append((axis, args, kwargs))
                return draw_errorbar(axis, *args, **kwargs)
            with patch("sys.argv", ["report.py", str(raw), "--out", str(out)]), patch.object(Axes, "errorbar", record_errorbar):
                report.main()
            delivery = [kwargs["yerr"] for axis, values, kwargs in calls
                        if axis.get_xlabel() == "Scheduled transactions/s" and kwargs.get("label") == "RLPx + ETH"]
            self.assertIn([[25.0], [25.0]], delivery)
            sizes = [kwargs["yerr"] for axis, values, kwargs in calls
                     if axis.get_title() == "Large proof-object delivery" and kwargs.get("label") == "RLPx + ETH"]
            self.assertEqual(sizes, [[[7.5], [7.5]]])
            latencies = [kwargs["yerr"] for axis, values, kwargs in calls
                         if axis.get_ylabel() == "Worst-receiver p95 (ms)" and kwargs.get("label") == "RLPx + ETH"]
            self.assertEqual(latencies, [[[125.0], [125.0]]])
            self.assertEqual(json.loads((out / "results.json").read_text()), rows)
            self.assertEqual(json.loads((out / "report-source.json").read_text())["report_script_sha256"],
                             hashlib.sha256((HERE / "report.py").read_bytes()).hexdigest())
            document = (out / "index.html").read_text()
            self.assertIn("decoder failed &lt;fixture&gt;", document)
            self.assertIn("no result &lt;fixture&gt;", document)
            self.assertIn("TX/useful ratio", document)
            self.assertIn("Expiry events", document)
            self.assertIn("Minimum never fetched", document)
            self.assertIn("<td>24 / 48</td><td>27</td><td>24</td><td>3</td><td>21</td>", document)
            for name in ("matched-rtt", "link-topology", "broadcast-fallback", "resource-wire", "large-objects", "goodput-tx1"):
                self.assertTrue((out / f"{name}.png").stat().st_size > 1000)
                svg = (out / f"{name}.svg").read_text()
                self.assertIn("Controlled verifier", svg)
                self.assertTrue(all(line == line.rstrip() for line in svg.splitlines()))
            self.assertIn("not which path won", (out / "broadcast-fallback.svg").read_text())
            self.assertIn("No control/HOL", (out / "matched-rtt.svg").read_text())
            rendered_text = " ".join(" ".join(ET.fromstring((out / "link-topology.svg").read_text()).itertext()).split())
            self.assertIn("recorded expiry events", rendered_text)
            for name in ("goodput-tx16", "goodput-bytes", "matched-rtt"):
                rendered_text = " ".join(" ".join(ET.fromstring((out / f"{name}.svg").read_text()).itertext()).split())
                self.assertIn("Scheduled release: 6 s.", rendered_text)
                self.assertIn("drain/timeout observation", rendered_text)
                self.assertIn("not steady-state capacity", rendered_text)

if __name__ == '__main__':
    unittest.main()
