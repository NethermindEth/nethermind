# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
from pathlib import Path
import tempfile
import unittest

import yaml

from inventory import file_identity, first_request, inspect_inputs


class InventoryTests(unittest.TestCase):
    def test_header_summary_excludes_transaction_bodies(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "payloads.jsonl"
            path.write_text('{"method":"engine_newPayloadV4","params":[{"blockNumber":"0x1","parentHash":"0xaa","transactions":["0x123"]}]}\n')
            summary = first_request(path)
            self.assertEqual(summary["header"], {"blockNumber": "0x1", "parentHash": "0xaa"})
            self.assertNotIn("transactions", str(summary))
            self.assertEqual(file_identity(path)["physical_line_count"], 1)
            path.write_text("a\nb")
            self.assertEqual(file_identity(path)["physical_line_count"], 2)

    def test_workflow_is_manual_fusaka_inventory_without_promotion(self):
        workflow = Path(__file__).resolve().parents[2] / ".github/workflows/collect-pgo-profile.yml"
        config = yaml.load(workflow.read_text(encoding="utf-8"), Loader=yaml.BaseLoader)
        self.assertEqual(list(config["on"]), ["workflow_dispatch"])
        phase = config["on"]["workflow_dispatch"]["inputs"]["phase"]
        self.assertEqual(phase["default"], "inventory")
        self.assertNotIn("pull-requests", config["permissions"])
        self.assertEqual(config["permissions"]["contents"], "read")
        self.assertNotIn("update-pgo-profile", config["jobs"])
        self.assertEqual(config["jobs"]["inventory"]["runs-on"], ["self-hosted", "reproducible-benchmarks"])
        self.assertEqual(config["jobs"]["build-pgo-image"]["if"], "inputs.phase == 'instrumentation'")
        self.assertIn("github-action-mainnet-fusaka-flat.yaml", str(config["jobs"]["collect"]))
        self.assertNotIn("realblocks", str(config))
        self.assertNotIn("halfpath", str(config))

    def test_resolves_inputs_and_keeps_snapshot_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            snapshot = root / "snapshot"
            snapshot.mkdir()
            (snapshot / "head").write_bytes(b"head identity")
            (root / "payloads.json").write_bytes(b"[]")
            (root / "fcus.json").write_bytes(b"[]")
            config = {"scenarios": {"nethermind": {"payloads": "payloads.json", "fcus": "fcus.json",
                                                  "snapshot_source": "snapshot", "extra_flags": ["--Init.StateDbKeyScheme=Flat"]}}}
            config_path = root / "config.yaml"
            config_path.write_text(yaml.safe_dump(config))
            report = inspect_inputs(config_path)
            self.assertEqual(report["files"]["payloads"]["sha256"], hashlib.sha256(b"[]").hexdigest())
            self.assertEqual(report["snapshot"]["path"], str(snapshot.resolve()))
            self.assertEqual(report["snapshot"]["entries"], ["head"])
            self.assertFalse(report["state_identity_verified"])
            self.assertEqual((snapshot / "head").read_bytes(), b"head identity")
            for key in ("payloads", "fcus", "snapshot_source"):
                with self.subTest(missing=key):
                    broken = dict(config["scenarios"]["nethermind"])
                    broken[key] = "missing"
                    config_path.write_text(yaml.safe_dump({"scenarios": {"nethermind": broken}}))
                    with self.assertRaises(FileNotFoundError):
                        inspect_inputs(config_path)


if __name__ == "__main__":
    unittest.main()
