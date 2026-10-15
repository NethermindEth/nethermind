#!/usr/bin/env python3
"""Deterministic reporting checks; no network namespaces, services or native proofs."""
import importlib.util
from pathlib import Path
import unittest

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

    def test_successful_processes_do_not_invent_failure(self):
        row = {"missing": 0}
        controller.retain_process_failures(row, {0: 0, 1: 0})
        self.assertNotIn("error", row)
        self.assertFalse(report.incomplete(row))

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

    def test_partial_measurement_is_retained_but_not_complete(self):
        rows = [{'missing': 2, 'goodput_tx_per_s_per_receiver': 4}, {'error': 'timeout'}]
        self.assertEqual(report.measured_values(rows, 'goodput_tx_per_s_per_receiver'), [4])
        self.assertTrue(report.incomplete(rows[0]))
        self.assertFalse(report.incomplete({'missing': 0}))

if __name__ == '__main__':
    unittest.main()
