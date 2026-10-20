# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "bal_results", Path(__file__).resolve().parents[1] / "check-bal-catchup-results.py"
)
results = importlib.util.module_from_spec(spec)
spec.loader.exec_module(results)


class BalCatchUpResultsTests(unittest.TestCase):
    def test_rejects_missing_measurements_and_runtime_errors(self):
        cases = [
            (None, "", False),
            ([], "", False),
            ([{"Statistics": None}], "", False),
            ([{"Statistics": {"Mean": 1}}], "", True),
            ([{"Statistics": {"Mean": 1}}], "Invalid Block", False),
            ([{"Statistics": {"Mean": 1}}], "Exception handling is enabled", True),
            ([{"Statistics": {"Mean": 1}}], "System.IO.IOException: disk full", False),
            ([{"Statistics": {"Mean": 1}}], " ---> System.InvalidOperationException: setup failed", False),
            ([{"Statistics": {"Mean": 1}}], "Unhandled exception. System.NullReferenceException: failed", False),
            ([{"Statistics": {"Mean": 1}}], "   System.IO.IOException: disk full", False),
            ([{"Statistics": {"Mean": 1}}], " ---> (Inner Exception #0) System.IO.IOException: disk full", False),
            ([{"Statistics": {"Mean": 1}}], "Exception: failed", False),
        ]
        for measurements, log, valid in cases:
            with self.subTest(measurements=measurements, log=log), tempfile.TemporaryDirectory() as temp:
                directory = Path(temp)
                (directory / "run.log").write_text(log)
                (directory / "results").mkdir()
                if measurements is not None:
                    (directory / "results/test-report-full-compressed.json").write_text(
                        json.dumps({"Benchmarks": measurements})
                    )
                if valid:
                    results.check(directory, 1)
                else:
                    with self.assertRaises(ValueError):
                        results.check(directory, 1)
                with self.assertRaises(ValueError):
                    results.check(directory, 2)


if __name__ == "__main__":
    unittest.main()
