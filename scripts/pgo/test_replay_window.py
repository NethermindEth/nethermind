# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import json
from pathlib import Path
import tempfile
import unittest

import yaml

from render_collection import render
from replay_window import validate_window
import test_render_collection as collection_fixtures


class ReplayWindowTests(unittest.TestCase):
    def test_incorrect_warmup_fails_before_creating_collection_output(self):
        for warmup in (0, 10, 12, None, True, -1, 1001):
            with self.subTest(warmup=warmup), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                config_path = collection_fixtures.CollectionRenderTests().fixture(root)
                config = yaml.safe_load(config_path.read_text())
                config["scenarios"]["nethermind"]["warmup"] = warmup
                config_path.write_text(yaml.safe_dump(config))
                with self.assertRaises(ValueError):
                    render(config_path, root / "run", "nethermindeth/nethermind@sha256:" + "a" * 64, 10)
                self.assertFalse((root / "run").exists())

    def test_corrupt_pair_chain_or_snapshot_is_rejected(self):
        cases = (("fcus", 12, "headBlockHash", "other"),
                 ("payloads", 12, "parentHash", "other"),
                 ("payloads", 12, "blockNumber", "0x99"),
                 ("payloads", 10, "stateRoot", "other"),
                 ("payloads", 10, "blockHash", "other"))
        for file, index, key, value in cases:
            with self.subTest(file=file, key=key), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                collection_fixtures.CollectionRenderTests().fixture(root)
                records = [json.loads(line) for line in (root / file).read_text().splitlines()]
                records[index]["params"][0][key] = value
                (root / file).write_text("\n".join(json.dumps(record) for record in records))
                with self.assertRaises(ValueError):
                    validate_window(root / "payloads", root / "fcus", root / "snapshot", 11, 10)

    def test_window_is_bounded_and_matches_expbs_blank_line_indexing(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            collection_fixtures.CollectionRenderTests().fixture(root)
            for file in ("payloads", "fcus"):
                original = (root / file).read_text()
                (root / file).write_text("\n" + original.replace("\n", "\n\n"))
            report = validate_window(root / "payloads", root / "fcus", root / "snapshot", 11, 10)
            self.assertEqual(len(report["headers"]), 21)
            self.assertEqual(report["headers"][11]["index"], 11)
            self.assertFalse(report["state_identity_verified"])
            with self.assertRaisesRegex(ValueError, "not enough paired"):
                validate_window(root / "payloads", root / "fcus", root / "snapshot", 11, 11)


if __name__ == "__main__":
    unittest.main()
