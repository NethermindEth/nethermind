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

    def test_partial_measurement_is_retained_but_not_complete(self):
        rows = [{'missing': 2, 'goodput_tx_per_s_per_receiver': 4}, {'error': 'timeout'}]
        self.assertEqual(report.measured_values(rows, 'goodput_tx_per_s_per_receiver'), [4])
        self.assertTrue(report.incomplete(rows[0]))
        self.assertFalse(report.incomplete({'missing': 0}))

if __name__ == '__main__':
    unittest.main()
