#!/usr/bin/env python3
"""Deterministic reporting checks; no network namespaces, services or native proofs."""
import importlib.util
import hashlib
import json
from pathlib import Path
import unittest
import tempfile
import subprocess
from unittest.mock import Mock, patch

HERE = Path(__file__).resolve().parent

def load(name):
    spec = importlib.util.spec_from_file_location(name, HERE / (name + '.py'))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

controller = load('netbench')
report = load('report')

class MetricsTests(unittest.TestCase):
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

    def test_report_renders_partial_errors_and_scoped_comparisons(self):
        try:
            import matplotlib
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
        rows[3].update(error="decoder failed <fixture>", missing=24, validated_receiver_deliveries=24)
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
            with patch("sys.argv", ["report.py", str(raw), "--out", str(out)]):
                report.main()
            self.assertEqual(json.loads((out / "results.json").read_text()), rows)
            self.assertEqual(json.loads((out / "report-source.json").read_text())["report_script_sha256"],
                             hashlib.sha256((HERE / "report.py").read_bytes()).hexdigest())
            document = (out / "index.html").read_text()
            self.assertIn("decoder failed &lt;fixture&gt;", document)
            self.assertIn("no result &lt;fixture&gt;", document)
            self.assertIn("TX/useful ratio", document)
            for name in ("matched-rtt", "link-topology", "broadcast-fallback", "resource-wire", "large-objects", "goodput-tx1"):
                self.assertTrue((out / f"{name}.png").stat().st_size > 1000)
                svg = (out / f"{name}.svg").read_text()
                self.assertIn("Controlled verifier", svg)
            self.assertIn("not which path won", (out / "broadcast-fallback.svg").read_text())
            self.assertIn("No control/HOL", (out / "matched-rtt.svg").read_text())

if __name__ == '__main__':
    unittest.main()
