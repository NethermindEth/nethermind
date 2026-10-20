import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import bundle
from embedded import CASES


class BundleTests(unittest.TestCase):
    def test_compiled_test_inventory_requires_all_executables(self):
        names = list(CASES) + ["nethermind_streaming_controls"]
        for mutation in (None, "missing", "duplicate", "not-executable", "outside-root", "wrong-kind", "no-test-harness"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                upstream = root / "upstream"
                records = []
                for name in names:
                    binary = upstream / "target/ci-e2e/deps" / name
                    binary.parent.mkdir(parents=True, exist_ok=True)
                    binary.write_text("executable fixture")
                    binary.chmod(0o755)
                    records.append({"reason": "compiler-artifact", "target": {"name": name, "kind": ["test"]},
                                    "profile": {"test": True}, "executable": str(binary)})
                if mutation == "wrong-kind": records[0]["target"]["kind"] = ["bin"]
                if mutation == "no-test-harness": records[0]["profile"]["test"] = False
                if mutation == "missing": records.pop()
                if mutation == "duplicate": records.append(dict(records[0]))
                if mutation == "not-executable": Path(records[0]["executable"]).chmod(0o644)
                if mutation == "outside-root": records[0]["executable"] = str(root / "untrusted")
                (root / "test-build.jsonl").write_text("\n".join(json.dumps(item) for item in records))
                with patch.dict(os.environ, {"EEZ_UPSTREAM": str(upstream)}):
                    if mutation is None:
                        bundle.tests(root)
                        self.assertEqual(set(names), set(json.loads((root / "test-binaries.json").read_text())))
                    else:
                        with self.assertRaises(ValueError): bundle.tests(root)

    def test_runtime_archive_preserves_executable_modes(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            upstream, output = root / "upstream", root / "output"
            upstream.mkdir()
            output.mkdir()
            runtime = output / "runtime"
            runtime.mkdir()
            binary = runtime / "eez-composer"
            binary.write_text("prepared executable")
            binary.chmod(0o555)
            (upstream / "original-scenario").write_text("original assertion")
            with patch.dict(os.environ, {"EEZ_PROFILE": "rollupx", "EEZ_UPSTREAM": str(upstream)}):
                bundle.pack(output)
            subprocess.run(["tar", "-I", "zstd", "-C", str(output), "-xf", str(output / "runtime.tar.zst")], check=True)
            self.assertTrue(os.access(binary, os.X_OK))
            self.assertEqual("prepared executable", binary.read_text())
