#!/usr/bin/env python3
"""Focused contract tests for the EXPB Account CV harness."""

from __future__ import annotations

import json
import os
import tempfile
import unittest
from pathlib import Path

import render_config
from expb_account_cv import (
    ARMS,
    SnapshotPreparationHook,
    arm_for_snapshot_name,
    expected_client_container,
)


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
  outputs: /mnt/sda/expb-data/outputs
resources:
  cpu: 8
  mem: 64g
  cpuset: 2-7,10-15
scenarios:
  nethermind:
    client: nethermind
    payloads: /mnt/sda/expb-data/payloads/fusaka.jsonl
    fcus: /mnt/sda/expb-data/payloads/fcus.jsonl
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
            for arm in ARMS:
                scenario = document["scenarios"][arm.label]
                self.assertEqual(scenario["amount"], 1000)
                self.assertEqual(scenario["delay"], 0)
                self.assertEqual(scenario["repeat"], 1)
                self.assertEqual(len([flag for flag in scenario["extra_flags"] if "FlatAccountDbAdditional" in flag]), 1)
                self.assertTrue(any(flag.startswith("--FlatDb.PersistenceWriteBufferFloor=67108864") for flag in scenario["extra_flags"]))


class SnapshotHookTests(unittest.TestCase):
    def _hook(self, root: Path, mounted: set[Path]) -> SnapshotPreparationHook:
        helper = root / "AccountIndexPrepare"
        helper.write_text("helper", encoding="utf-8")
        helper.chmod(0o755)
        return SnapshotPreparationHook(
            root / "scratch",
            root / "scratch" / "results",
            helper,
            expected_arm="master",
            mount_checker=lambda path: path in mounted,
            unmount=lambda path: mounted.discard(path),
        )

    def test_hook_runs_after_overlay_mount_for_executor_snapshot_name(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            mounted: set[Path] = set()
            hook = self._hook(root, mounted)
            events: list[str] = []
            service = type("Service", (), {})()
            service.overlay_work_dir = root / "scratch" / "work"
            service.overlay_upper_dir = root / "scratch" / "upper"
            service.overlay_merged_dir = root / "scratch" / "merged"

            def original(_service: object, name: str, _source: str) -> Path:
                events.append(f"original:{name}")
                for path in (service.overlay_work_dir, service.overlay_upper_dir, service.overlay_merged_dir):
                    path.mkdir(parents=True)
                mounted.add(service.overlay_merged_dir.resolve())
                return service.overlay_merged_dir

            def fake_run(_arm: object, _merged: Path, result: Path) -> None:
                events.append("helper")
                (result / "helper.json").write_text("{}", encoding="utf-8")

            hook._run_helper = fake_run  # type: ignore[method-assign]
            hook._validate_helper = lambda _arm, _result: {
                "account_content_sha256": "a" * 64,
                "account_entry_count": 1,
                "helper_sha256": "b" * 64,
                "uniform_index_count": 1,
                "persisted_index_bytes": 1,
                "filter_bytes": 1,
                "table_reader_memory_bytes": 1,
            }  # type: ignore[method-assign]
            hook._record_fingerprint = lambda _arm, _report, _result: events.append("fingerprint")  # type: ignore[method-assign]

            result = hook.create_snapshot(service, "expb-executor-master", "/canonical", original)
            self.assertEqual(result, service.overlay_merged_dir)
            self.assertEqual(events, ["original:expb-executor-master", "helper", "fingerprint"])
            self.assertTrue((service.overlay_merged_dir / ".account-index-prepare-owned").is_file())

    def test_failed_preparation_unmounts_before_removing_the_owned_view(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            mounted: set[Path] = set()
            hook = self._hook(root, mounted)
            service = type("Service", (), {})()
            service.overlay_work_dir = root / "scratch" / "work"
            service.overlay_upper_dir = root / "scratch" / "upper"
            service.overlay_merged_dir = root / "scratch" / "merged"

            def original(_service: object, _name: str, _source: str) -> Path:
                for path in (service.overlay_work_dir, service.overlay_upper_dir, service.overlay_merged_dir):
                    path.mkdir(parents=True)
                mounted.add(service.overlay_merged_dir.resolve())
                return service.overlay_merged_dir

            hook._run_helper = lambda *_args: (_ for _ in ()).throw(RuntimeError("helper failed"))  # type: ignore[method-assign]
            with self.assertRaises(RuntimeError):
                hook.create_snapshot(service, "expb-executor-master", "/canonical", original)
            self.assertFalse(mounted)
            self.assertFalse(service.overlay_merged_dir.exists())


class PinnedContractTests(unittest.TestCase):
    def test_arm_name_matches_pinned_executor_name_and_sampler_target(self) -> None:
        for arm in ARMS:
            self.assertEqual(arm_for_snapshot_name(f"expb-executor-{arm.label.replace('.', '-')}").label, arm.label)
            self.assertEqual(expected_client_container(arm.label), f"expb-executor-{arm.label.replace('.', '-')}-nethermind")


if __name__ == "__main__":
    unittest.main()
