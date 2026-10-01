# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from pathlib import Path
import json
import tempfile
import unittest

import yaml

from render_collection import render


class CollectionRenderTests(unittest.TestCase):
    def fixture(self, root):
        (root / "snapshot").mkdir()
        payloads = [{"blockNumber": hex(number), "blockHash": str(number), "parentHash": str(number - 1),
                     "stateRoot": f"root{number}"} for number in range(90, 111)]
        (root / "payloads").write_text("".join(json.dumps({"method": "engine_newPayloadV4", "params": [body]}) + "\n"
                                            for body in payloads))
        (root / "fcus").write_text("".join(json.dumps({"method": "engine_forkchoiceUpdatedV3",
            "params": [{"headBlockHash": body["blockHash"]}]}) + "\n" for body in payloads))
        (root / "snapshot/_snapshot_eth_getBlockByNumber.json").write_text(json.dumps({"result": {
            "number": "0x64", "hash": "100", "stateRoot": "root100"}}))
        config = {"paths": {"work": "shared", "outputs": "old-outputs"},
            "export": {"prometheus_remote_write": {"endpoint": "https://metrics.example.invalid",
                "basic_auth": {"username": "collection-export-user", "password": "collection-export-secret"}}},
            "scenarios": {"nethermind": {
            "payloads": "payloads", "fcus": "fcus", "snapshot_source": "snapshot",
            "image": "nethermindeth/nethermind:<<DOCKER_TAG>>", "delay": "<<DELAY>>",
            "amount": "<<AMOUNT>>", "warmup": 11,
            "extra_flags": ["--FlatDb.PersistenceWriteBufferFloor=67108864"]}}}
        path = root / "github-action-mainnet-fusaka-flat.yaml"
        path.write_text(yaml.safe_dump(config))
        return path

    def test_replay_is_bounded_and_writable_paths_are_owned(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_path = self.fixture(root)
            original = config_path.read_bytes()
            config = render(config_path, root / "run", "nethermindeth/nethermind@sha256:" + "a" * 64, 10)
            scenario = config["scenarios"]["nethermind-pgo-collect"]
            self.assertEqual(scenario["amount"], 10)
            self.assertEqual(scenario["warmup"], 11)
            window = json.loads((root / "run/manifest.json").read_text())["replay_window"]
            self.assertEqual((window["training_first_number"], window["training_last_number"]), (101, 110))
            self.assertEqual(scenario["delay"], 0)
            self.assertEqual(scenario["snapshot_backend"], "overlay")
            self.assertEqual(scenario["extra_volumes"]["pgo"]["source"], str(root / "run/pgo"))
            self.assertEqual(config["paths"]["outputs"], str(root / "run/outputs"))
            self.assertNotIn("export", config)
            rendered = (root / "run/config.yaml").read_text()
            for value in ("metrics.example.invalid", "collection-export-user", "collection-export-secret"):
                self.assertNotIn(value, rendered)
            self.assertEqual(config_path.read_bytes(), original)
            with self.assertRaisesRegex(ValueError, "already exists"):
                render(config_path, root / "run", scenario["image"], 10)

    def test_inputs_and_output_cannot_overlap(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_path = self.fixture(root)
            with self.assertRaisesRegex(ValueError, "overlap"):
                render(config_path, root / "snapshot/new", "nethermindeth/nethermind@sha256:" + "a" * 64, 10)
            self.assertFalse((root / "snapshot/new").exists())

    def test_delayed_collection_preserves_inputs_and_records_pacing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = self.fixture(root)
            original = path.read_bytes()
            config = render(path, root / "run", "nethermindeth/nethermind@sha256:" + "a" * 64, 10, 1)
            scenario = config["scenarios"]["nethermind-pgo-collect"]
            manifest = json.loads((root / "run/manifest.json").read_text())
            self.assertEqual((scenario["delay"], scenario["warmup_delay"]), (1, 0))
            self.assertEqual((manifest["delay_seconds"], manifest["warmup_delay_seconds"]), (1, 0))
            self.assertEqual(path.read_bytes(), original)

    def test_invalid_delay_fails_before_output_creation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = self.fixture(root)
            for delay in (-1, 2, True, 1.0, "1"):
                with self.subTest(delay=delay), self.assertRaisesRegex(ValueError, "delay"):
                    render(path, root / "run", "nethermindeth/nethermind@sha256:" + "a" * 64, 10, delay)
                self.assertFalse((root / "run").exists())

    def test_invalid_amount_or_unpinned_image_fails_before_output_creation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            config_path = self.fixture(root)
            for amount, image in ((0, "a"), (1001, "a"), (10, "nethermindeth/nethermind:master")):
                with self.subTest(amount=amount, image=image), self.assertRaises(ValueError):
                    render(config_path, root / "run", image, amount)
                self.assertFalse((root / "run").exists())


if __name__ == "__main__":
    unittest.main()
