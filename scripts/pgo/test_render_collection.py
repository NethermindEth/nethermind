# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from pathlib import Path
import tempfile
import unittest

import yaml

from render_collection import render


class CollectionRenderTests(unittest.TestCase):
    def fixture(self, root):
        for name in ("payloads", "fcus"):
            (root / name).write_text("[]")
        (root / "snapshot").mkdir()
        config = {"paths": {"work": "shared", "outputs": "old-outputs"}, "scenarios": {"nethermind": {
            "payloads": "payloads", "fcus": "fcus", "snapshot_source": "snapshot",
            "image": "nethermindeth/nethermind:<<DOCKER_TAG>>", "delay": "<<DELAY>>",
            "amount": "<<AMOUNT>>", "extra_flags": ["--FlatDb.PersistenceWriteBufferFloor=67108864"]}}}
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
            self.assertEqual(scenario["delay"], 0)
            self.assertEqual(scenario["snapshot_backend"], "overlay")
            self.assertEqual(scenario["extra_volumes"]["pgo"]["source"], str(root / "run/pgo"))
            self.assertEqual(config["paths"]["outputs"], str(root / "run/outputs"))
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
