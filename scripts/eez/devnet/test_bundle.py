import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import bundle
import upstream
from embedded import CASES


class PreparedInputCacheTests(unittest.TestCase):
    @staticmethod
    def manifest(profile="rollup0"):
        manifest = {"repository": upstream.PROFILES[profile], "profile": profile,
                    "revision": "a" * 40, "core_revision": "b" * 40, "main_at_resolution": "c" * 40}
        if profile == "rollup0":
            manifest["streaming"] = {"source": "reviewed-pr206-before-merge", "wire": "prove.v2",
                "profile": "rollup0-conditional", "execution_factory": "EthBlockExecutorFactory",
                "skip_consensus_layer_calls": False, "source_blobs": {"client.rs": "d" * 40}}
        return manifest

    @staticmethod
    def identity(directory, manifest):
        source, output = directory / "manifest.json", directory / "build-identity.json"
        source.write_text(json.dumps(manifest))
        upstream.cache_identity(SimpleNamespace(manifest=source, output=output))
        return hashlib.sha256(output.read_bytes()).hexdigest()

    def test_unrelated_main_and_resolution_origin_reuse_the_same_key(self):
        for profile in upstream.PROFILES:
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                manifest = self.manifest(profile)
                original = self.identity(root, manifest)
                manifest["main_at_resolution"] = "e" * 40
                manifest["runtime"] = {"source": "source-build", "binaries": {"eez-composer": "sha256:fixture"}}
                if "streaming" in manifest:
                    manifest["streaming"]["source"] = "reviewed-pr206-replay"
                # JSON member order also has no effect on Actions' file hash.
                self.assertEqual(original, self.identity(root, dict(reversed(list(manifest.items())))))

    def test_every_selected_build_input_changes_the_key(self):
        changes = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx",
                   "revision": "e" * 40, "core_revision": "e" * 40, "wire": "prove.v1",
                   "streaming_profile": "other", "execution_factory": "EezBlockExecutorFactory",
                   "skip_consensus_layer_calls": True, "source_blobs": {"client.rs": "e" * 40}}
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = self.identity(root, self.manifest())
            for key, value in changes.items():
                with self.subTest(input=key):
                    manifest = self.manifest()
                    if key in ("repository", "profile", "revision", "core_revision"):
                        manifest[key] = value
                    else:
                        manifest["streaming"]["profile" if key == "streaming_profile" else key] = value
                    self.assertNotEqual(original, self.identity(root, manifest))

    def test_restore_rejects_mismatched_inputs_or_missing_provenance_without_writing(self):
        for mutation in ("revision", "core_revision", "execution", "runtime"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                cached, current = self.manifest(), self.manifest()
                cached["runtime"] = {"source": "source-build", "binaries": {"eez-composer": "verified-checksum"}}
                if mutation == "runtime":
                    del cached["runtime"]
                elif mutation == "execution":
                    cached["streaming"]["skip_consensus_layer_calls"] = True
                else:
                    cached[mutation] = "e" * 40
                saved, resolution = root / "upstream.json", root / "resolution.json"
                saved.write_text(json.dumps(cached))
                resolution.write_text(json.dumps(current))
                before = saved.read_bytes()
                with self.assertRaises((ValueError, KeyError)):
                    upstream.refresh_resolution(SimpleNamespace(manifest=saved, resolution=resolution))
                self.assertEqual(before, saved.read_bytes())

    def test_cli_restore_keeps_runtime_provenance_and_uses_current_resolution(self):
        for profile in upstream.PROFILES:
            with self.subTest(profile=profile), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                cached, current = self.manifest(profile), self.manifest(profile)
                cached["runtime"] = {"source": "artifact", "artifact_id": 7, "run_id": 8,
                                     "digest": "sha256:archive", "binaries": {"eez-composer": "sha256:binary"}}
                current["main_at_resolution"] = "e" * 40
                if "streaming" in current:
                    current["streaming"]["source"] = "reviewed-pr206-replay"
                resolution = root / "resolution.json"
                resolution.write_text(json.dumps(current))
                saved = root / "upstream.json"
                saved.write_text(json.dumps(cached))
                identity = root / "build-identity.json"
                command = [sys.executable, upstream.__file__]
                subprocess.run(command + ["cache-identity", "--manifest", str(resolution), "--output", str(identity)], check=True)
                self.assertNotIn("main_at_resolution", json.loads(identity.read_text()))
                subprocess.run(command + ["refresh-resolution", "--manifest", str(saved), "--resolution", str(resolution)], check=True)
                self.assertEqual(json.loads(saved.read_text()), dict(current, runtime=cached["runtime"]))
                self.assertEqual(json.loads(resolution.read_text()), current)


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
