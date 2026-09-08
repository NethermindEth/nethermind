#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import re
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
import account_index_sweep  # noqa: E402


class AccountIndexSweepTests(unittest.TestCase):
    def test_fixed_arm_order_runtime_modes_and_boolean_opt_in(self):
        arms = account_index_sweep.arms()
        self.assertEqual(
            [(arm["label"], arm["mode"], arm["threshold"]) for arm in arms],
            [
                ("master", "binary", -1.0),
                ("forced-interpolation", "interpolation", -1.0),
                ("auto-cv-0.2", "auto", 0.2),
                ("auto-cv-0.05", "auto", 0.05),
                ("auto-cv-0.1", "auto", 0.1),
                ("auto-cv-0.15", "auto", 0.15),
                ("auto-cv-0.25", "auto", 0.25),
                ("auto-cv-0.35", "auto", 0.35),
                ("auto-cv-0.5", "auto", 0.5),
            ],
        )
        self.assertTrue(account_index_sweep.validate_opt_in(True))
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_opt_in("true")

    def test_all_public_labels_map_to_unique_registry_identifiers(self):
        public_labels = [arm["label"] for arm in account_index_sweep.arms()]
        registry_labels = [account_index_sweep.registry_label(label) for label in public_labels]
        self.assertEqual(
            registry_labels,
            [
                "account_master", "account_forced_interpolation", "account_auto_cv_02",
                "account_auto_cv_005", "account_auto_cv_01", "account_auto_cv_015",
                "account_auto_cv_025", "account_auto_cv_035", "account_auto_cv_05",
            ],
        )
        self.assertEqual(len(registry_labels), len(set(registry_labels)))
        self.assertTrue(all(re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", label) for label in registry_labels))

    def test_start_node_is_runtime_only(self):
        start_node = (Path(__file__).parent / "start-node.sh").read_text(encoding="utf-8")
        self.assertNotIn("ACCOUNT_INDEX_HELPER_PATH", start_node)
        self.assertNotIn("prepare_account_index", start_node)
        self.assertIn("binary) return 0", start_node)
        self.assertIn("index_block_search_type=kInterpolation;", start_node)
        self.assertIn("index_block_search_type=kAuto;", start_node)
        self.assertIn("uniform_cv_threshold=${ACCOUNT_INDEX_PREPARE_THRESHOLD};", start_node)


if __name__ == "__main__":
    unittest.main()
