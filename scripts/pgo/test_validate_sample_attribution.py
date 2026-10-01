# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import unittest

from validate_sample_attribution import LABELS, validate


class SampleAttributionTests(unittest.TestCase):
    def fixture(self, **changes):
        raw = {"processId": 7, "before": 100, "after": 200, "records": 10, "weight": 20}
        counts = dict.fromkeys(LABELS, 0)
        counts.update(total=20, native=2, attributed=18)
        counts.update(changes)
        log = "Universal CPU sample process: 7\nUniversal CPU sample window start: 100\nUniversal CPU sample window end: 200\n"
        log += "".join(f"{label}: {counts[key]}\n" for key, label in LABELS.items())
        return raw, log

    def test_weighted_accounting_and_native_samples(self):
        raw, log = self.fixture()
        self.assertEqual(validate(raw, log, True)["counts"]["attributed"], 18)

    def test_rejects_changed_context_missing_duplicate_and_invalid_diagnostics(self):
        raw, log = self.fixture()
        cases = [log.replace("process: 7", "process: 8"), log.replace("start: 100", "start: 99"),
                 log.replace("end: 200", "end: 201"), log.replace("in window: 20", "in window: 19"),
                 log.replace("attributed: 18", "attributed: 17"), log.replace("attributed: 18", "attributed: -1"),
                 log.replace("attributed: 18", "attributed: 9223372036854775808"),
                 log.replace("Samples successfully attributed: 18\n", ""),
                 log + "Universal CPU sample weight in window: 20\n"]
        for case in cases:
            with self.subTest(case=case), self.assertRaises(ValueError):
                validate(raw, case)

    def test_managed_gate_rejects_each_unresolved_bucket_and_empty_attribution(self):
        for key in ("missing_stacks", "missing_maps", "outside_maps", "unknown_inlinees", "missing_il", "outside_graph"):
            raw, log = self.fixture(**{key: 1, "attributed": 17})
            self.assertEqual(validate(raw, log)["counts"][key], 1)
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "managed attribution"):
                validate(raw, log, True)
        raw, log = self.fixture(native=20, attributed=0)
        with self.assertRaisesRegex(ValueError, "managed attribution"):
            validate(raw, log, True)

    def test_raw_report_numeric_validation(self):
        raw, log = self.fixture()
        for key, value in [("processId", True), ("processId", 0), ("weight", True), ("weight", -1), ("records", 1.5), ("before", float("nan")), ("after", 99)]:
            with self.subTest(key=key, value=value), self.assertRaises(ValueError):
                validate(dict(raw, **{key: value}), log)


if __name__ == "__main__":
    unittest.main()
