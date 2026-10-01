# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from pathlib import Path
import tempfile
import unittest

from reference_inventory import inventory


class ReferenceInventoryTests(unittest.TestCase):
    def fixture(self, root):
        (root / "app/plugins").mkdir(parents=True)
        (root / "app/nethermind.runtimeconfig.json").write_text("{}")
        (root / "app/Nethermind.Runner.dll").write_bytes(b"app")
        (root / "app/plugins/Plugin.dll").write_bytes(b"plugin")
        framework = root / "shared/Microsoft.NETCore.App/10.0.12"
        framework.mkdir(parents=True)
        (framework / "System.Private.CoreLib.dll").write_bytes(b"corelib")
        return framework

    def test_nested_references_and_identical_copies_are_inventoried(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            framework = self.fixture(root)
            (framework / "Plugin.dll").write_bytes(b"plugin")
            report, references = inventory(root, "image@sha256:test")
            self.assertEqual(len(report["files"]), 4)
            self.assertEqual(len(references), 3)
            self.assertIn(str(root / "app/plugins/Plugin.dll"), references)
            self.assertIn(str(framework / "System.Private.CoreLib.dll"), references)
            self.assertFalse(report["trace_identity_verified"])
            self.assertTrue(all(len(item["sha256"]) == 64 for item in report["files"]))

    def test_conflicting_filenames_fail(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            framework = self.fixture(root)
            (framework / "Plugin.dll").write_bytes(b"other-plugin")
            with self.assertRaisesRegex(ValueError, "ambiguous reference"):
                inventory(root, "image")

    def test_missing_inputs_fail(self):
        for missing in ("app/nethermind.runtimeconfig.json", "app/Nethermind.Runner.dll",
                        "shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                self.fixture(root)
                (root / missing).unlink()
                if missing.endswith("Nethermind.Runner.dll"):
                    (root / "app/plugins/Plugin.dll").unlink()
                with self.assertRaisesRegex(ValueError, "missing"):
                    inventory(root, "image")


if __name__ == "__main__":
    unittest.main()
