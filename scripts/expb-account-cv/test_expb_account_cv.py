#!/usr/bin/env python3
"""Focused contract tests for the EXPB Account CV harness."""

from __future__ import annotations

import tempfile
import unittest
from pathlib import Path

import render_config
from expb_account_cv import (
    ARMS,
    account_extra_flag,
    arm_for_snapshot_name,
    expected_client_container,
)


class ArmTests(unittest.TestCase):
    def test_exact_arm_order_and_account_overrides(self) -> None:
        self.assertEqual(
            [arm.label for arm in ARMS],
            ["master", "forced-interpolation", "auto-cv-0.1", "auto-cv-0.2", "auto-cv-0.5"],
        )
        self.assertIsNone(account_extra_flag(ARMS[0]))
        self.assertEqual(
            account_extra_flag(ARMS[1]),
            "--Db.FlatAccountDbAdditionalRocksDbOptions="
            "block_based_table_factory.index_block_search_type=kInterpolation;",
        )
        self.assertIn("uniform_cv_threshold=0.1;", account_extra_flag(ARMS[2]))

    def test_pinned_executor_and_sampler_names(self) -> None:
        for arm in ARMS:
            executor = f"expb-executor-{arm.label.replace('.', '-')}"
            self.assertEqual(arm_for_snapshot_name(executor).label, arm.label)
            self.assertEqual(expected_client_container(arm.label), f"{executor}-nethermind")


class ConfigTests(unittest.TestCase):
    def test_render_keeps_resources_and_emits_exact_five_account_arms(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source = root / "source.yaml"
            output = root / "rendered.yaml"
            source.write_text(
                """
paths:
  work: /mnt/sda/expb-data/work
resources:
  cpu: 8
  mem: 64g
  cpuset: 2-7,10-15
scenarios:
  nethermind:
    client: nethermind
    payloads: /mnt/sda/expb-data/payloads/fusaka.jsonl
    snapshot_source: /mnt/sda/nethermind-flat-25490000
    extra_flags: [--Existing.Flag=true]
""",
                encoding="utf-8",
            )
            document = render_config.build_config(
                source,
                output,
                work_root=root / "scratch",
                output_root=root / "results",
                expected_snapshot_source="/mnt/sda/nethermind-flat-25490000",
                expected_cpuset="2-7,10-15",
                expected_memory="64g",
            )
            self.assertEqual(list(document["scenarios"]), [arm.label for arm in ARMS])
            self.assertEqual(document["resources"]["cpuset"], "2-7,10-15")
            self.assertEqual(document["scenarios"]["master"]["extra_flags"].count(
                "--Db.FlatAccountDbAdditionalRocksDbOptions="
            ), 0)
            for arm in ARMS:
                scenario = document["scenarios"][arm.label]
                self.assertEqual(scenario["amount"], 1000)
                self.assertEqual(scenario["delay"], 0)
                self.assertEqual(scenario["repeat"], 1)
                self.assertTrue(
                    any(
                        flag.startswith("--FlatDb.PersistenceWriteBufferFloor=67108864")
                        for flag in scenario["extra_flags"]
                    )
                )


if __name__ == "__main__":
    unittest.main()
