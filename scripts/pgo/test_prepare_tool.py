# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from prepare_tool import prepare, verified_patches


class PrepareToolTests(unittest.TestCase):
    def test_locked_patch_checksums(self):
        lock, patches = verified_patches(Path(__file__).with_name("tool-lock.json"))
        self.assertEqual(lock["runtime_version"], "10.0.12")
        self.assertEqual(lock["traceevent_version"], "3.2.8")
        self.assertEqual(len(patches), 5)

    def test_bad_checksum_fails_before_network_or_source_mutation(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "patch").write_bytes(b"patched after lock")
            lock = root / "lock.json"
            lock.write_text(json.dumps({"patches": [{"file": "patch", "sha256": hashlib.sha256(b"original").hexdigest()}]}))
            with patch("prepare_tool.subprocess.run") as run:
                with self.assertRaisesRegex(ValueError, "checksum mismatch"):
                    prepare(lock, root / "source")
                run.assert_not_called()
            self.assertFalse((root / "source").exists())

    def test_existing_source_is_not_modified(self):
        with tempfile.TemporaryDirectory() as directory:
            with patch("prepare_tool.subprocess.run") as run:
                with self.assertRaisesRegex(ValueError, "already exists"):
                    prepare(Path(__file__).with_name("tool-lock.json"), directory)
                run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
