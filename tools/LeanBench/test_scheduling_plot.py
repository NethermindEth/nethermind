#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Reject ambiguous JSON types before rendering recorded decision counts."""

import json
from pathlib import Path
import tempfile
import unittest

from scheduling_plot import counts


class SchedulingPlotTests(unittest.TestCase):
    def capture_counts(self, builder=1, deliveries=0, stale=True):
        rows = [
            {"scenario": "background-proof-to-production-blockprocessor", "builderAdditionalProofCalls": builder},
            {"scenario": "eligible-completed-wrapper-during-new-proof", "eligibleCompletedWrapperSendsWhileBlocked": deliveries},
            {"scenario": "removed-selection-during-proof", "staleDelivery": stale},
        ]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "capture.json"
            path.write_text(json.dumps({"results": rows}))
            return counts(path)

    def test_integer_counts_and_boolean_flag(self):
        self.assertEqual(self.capture_counts(stale=True), [1, 0, 1])
        self.assertEqual(self.capture_counts(builder=0, deliveries=1, stale=False), [0, 1, 0])

    def test_boolean_is_not_a_builder_count(self):
        for value in (True, False):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.capture_counts(builder=value)

    def test_boolean_is_not_a_delivery_count(self):
        for value in (True, False):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.capture_counts(deliveries=value)

    def test_stale_delivery_requires_boolean(self):
        for value in (0, 1, "false", None):
            with self.subTest(value=value), self.assertRaises(ValueError):
                self.capture_counts(stale=value)


if __name__ == "__main__":
    unittest.main()
