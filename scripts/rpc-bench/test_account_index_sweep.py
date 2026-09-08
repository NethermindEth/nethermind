#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import copy
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).parent))
import account_index_sweep  # noqa: E402


SHA = "a" * 64


def helper_report(mode="auto", threshold=0.2):
    search = {"binary": "kBinary", "interpolation": "kInterpolation", "auto": "kAuto"}[mode]
    threshold_text = f"{threshold:g}"
    table = {
        "UniformBlocks": 12,
        "IndexBytes": 4096,
        "FilterBytes": 2048,
        "EstimateTableReadersMemory": 8192,
        "Entries": 4096,
    }
    return {
        "SchemaVersion": 1,
        "DbPath": "/work/mainnet/flat",
        "ScratchRoot": "/work",
        "Mode": mode,
        "Threshold": threshold,
        "Options": {
            "AccountIndexSearchType": search,
            "UniformCvThreshold": threshold_text,
            "AutomaticCompactionsDisabled": True,
        },
        "Content": {
            "Before": {"Count": 4096, "Sha256": SHA},
            "After": {"Count": 4096, "Sha256": SHA},
            "Unchanged": True,
        },
        "Tables": {"Before": copy.deepcopy(table), "After": copy.deepcopy(table)},
        "Ssts": {
            "Before": [{"Number": 122, "SizeBytes": 1000}],
            "After": [{"Number": 123, "SizeBytes": 1000}],
            "Rewritten": True,
        },
        "Timing": {
            "WallMilliseconds": 120.0,
            "CpuMilliseconds": 80.0,
            "WorkingSetBytesBefore": 100,
            "WorkingSetBytesAfter": 110,
            "PeakWorkingSetBytes": 120,
            "PrivateBytesBefore": 200,
            "PrivateBytesAfter": 210,
        },
        "Layout": {
            "Current": "CURRENT",
            "Manifest": "MANIFEST-000001",
            "Files": ["CURRENT", "MANIFEST-000001"],
            "FlatColumnFamilies": ["Account"],
        },
    }


class AccountIndexSweepTests(unittest.TestCase):
    def test_fixed_arm_order_and_boolean_opt_in(self):
        self.assertEqual(
            [arm["label"] for arm in account_index_sweep.arms()],
            [
                "master", "forced-interpolation", "auto-cv-0.2", "auto-cv-0.05", "auto-cv-0.1",
                "auto-cv-0.15", "auto-cv-0.25", "auto-cv-0.35", "auto-cv-0.5",
            ],
        )
        self.assertTrue(account_index_sweep.validate_opt_in(True))
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_opt_in("true")

    def test_helper_is_sanitized_without_private_paths_or_metadata(self):
        safe = account_index_sweep.validate_preparation(helper_report(), "auto", 0.2)
        safe["helper_sha256"] = SHA
        account_index_sweep.validate_sanitized_preparation(safe)
        self.assertEqual(safe["persisted_index_bytes"], 4096)
        self.assertNotIn("DbPath", safe)
        self.assertNotIn("AccountSstMetadata", safe)

    def test_helper_rejects_missing_metrics_and_content_changes(self):
        report = helper_report()
        report["Tables"]["After"]["IndexBytes"] = 0
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_preparation(report, "auto", 0.2)
        report = helper_report()
        report["Content"]["After"]["Sha256"] = "b" * 64
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_preparation(report, "auto", 0.2)
        report = helper_report()
        report["Ssts"]["After"][0]["Number"] = 122
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_preparation(report, "auto", 0.2)

    def test_public_schema_is_fail_closed(self):
        safe = account_index_sweep.validate_preparation(helper_report(), "auto", 0.2)
        safe["helper_sha256"] = SHA
        safe["private_path"] = "/work/mainnet/flat"
        with self.assertRaises(account_index_sweep.ContractError):
            account_index_sweep.validate_sanitized_preparation(safe)


if __name__ == "__main__":
    unittest.main()
