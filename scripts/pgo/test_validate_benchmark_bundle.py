# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import copy
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

from validate_benchmark_bundle import compare_rebuilt, digest, validate_bundle, validate_run, validate_selection, verify_artifacts, verify_references
from validate_collection import validate as validate_replay


class BenchmarkBundleTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.sha = "a" * 40
        self.run = {"id": 42, "run_attempt": 1, "status": "completed", "conclusion": "success",
                    "head_sha": self.sha, "path": ".github/workflows/collect-pgo-profile.yml",
                    "event": "workflow_dispatch", "head_repository": {"full_name": "NethermindEth/nethermind", "id": 1},
                    "repository": {"id": 1}, "run_started_at": "2026-01-01T00:00:00Z", "updated_at": "2026-01-01T00:02:00Z"}
        self.collection = self.root / "pgo-collection-42-1"
        self.collection.mkdir()
        self.refs = self.root / "pgo-app-references-42-1"
        image = "nethermindeth/nethermind@sha256:" + "b" * 64
        self.references = {"image": image, "files": []}
        for name in ("app/Nethermind.Trie.dll", "shared/System.Private.CoreLib.dll"):
            path = self.refs / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_bytes(name.encode())
            self.references["files"].append({"path": name, "bytes": path.stat().st_size, "sha256": digest(path)})
        (self.refs / "manifest.json").write_text(json.dumps(self.references))
        headers = [{"index": i, "number": 25489990 + i, "hash": "0x" + f"{i:064x}"} for i in range(1011)]
        self.manifest = {"config_source": "/mnt/sda/expb-data/github-action-mainnet-fusaka-flat.yaml",
                         "snapshot_source": "/mnt/sda/nethermind-flat-25490000",
                         "payloads": "/mnt/sda/expb-data/fusaka-payloads/payloads-10k.jsonl",
                         "image": image, "amount": 1000,
                         "replay_window": {"warmup": 11, "amount": 1000, "snapshot_number": 25490000,
                                           "training_first_number": 25490001, "training_last_number": 25491000,
                                           "headers": headers}}
        self.save_manifest()
        log = "".join(f"| {i} | 100 | 1.0 |\n" for i in range(11, 1011))
        log += "Nethermind is shut down\nevent=\"Cleanup completed\"\n"
        log += "".join(f"EXPB_ENGINE_RESULT idx={i} warmup={int(i < 11)} kind={kind} "
                       f"status=VALID latest_valid_hash={headers[i]['hash']}\n"
                       for i in range(1011) for kind in ("newPayload", "forkchoiceUpdated"))
        (self.collection / "expb.log").write_text(log)
        self.recorded = validate_replay(self.collection / "expb.log", 1000, 0, self.collection / "manifest.json")
        self.assertEqual(self.recorded["status"], "valid")
        (self.collection / "execution-validation.json").write_text(json.dumps(self.recorded))
        for name in ("nethermind-pgo-profile/nethermind.mibc", "nethermind-pgo-raw-data/nethermind-1.nettrace"):
            path = self.root / name
            path.parent.mkdir()
            path.write_bytes(b"fixture")
        self.seal_artifacts()

    def seal_artifacts(self):
        archives = self.root / ".artifact-archives"
        archives.mkdir(exist_ok=True)
        artifacts = []
        names = ("pgo-collection-42-1", "pgo-app-references-42-1", "nethermind-pgo-profile", "nethermind-pgo-raw-data")
        for identifier, name in enumerate(names, 1):
            archive = archives / f"{identifier}.zip"
            with zipfile.ZipFile(archive, "w") as bundle:
                for path in (self.root / name).rglob("*"):
                    if path.is_file():
                        bundle.write(path, path.relative_to(self.root / name).as_posix())
            artifacts.append({"id": identifier, "name": name, "digest": "sha256:" + digest(archive),
                              "workflow_run": {"id": 42, "head_sha": self.sha, "repository_id": 1, "head_repository_id": 1},
                              "created_at": "2026-01-01T00:01:00Z", "expired": False})
        (self.root / "artifact-api.json").write_text(json.dumps({"artifacts": artifacts}))

    def save_manifest(self):
        (self.collection / "manifest.json").write_text(json.dumps(self.manifest))
        if hasattr(self, "recorded"):
            self.seal_artifacts()

    def test_valid_bundle_projects_identity_without_configuration_contents(self):
        self.manifest["unused_configuration"] = "private-value-sentinel"
        self.save_manifest()
        report = validate_bundle(self.root, self.run, self.sha)
        self.assertEqual(report["engine_api_results"], 2022)
        self.assertEqual(report["reference_files"], 2)
        self.assertNotIn("private-value-sentinel", json.dumps(report))
        self.assertNotIn("config_source", report)

    def test_wrong_run_identity_or_completion_is_rejected(self):
        changes = ({"status": "in_progress"}, {"conclusion": "failure"}, {"head_sha": "c" * 40},
                   {"path": ".github/workflows/other.yml"}, {"event": "pull_request"},
                   {"head_repository": {"full_name": "other/repo"}}, {"id": 0}, {"run_attempt": 0})
        for change in changes:
            with self.subTest(change=change), self.assertRaises(ValueError):
                validate_run(self.run | change, self.sha)
        with self.assertRaises(ValueError):
            validate_run(self.run, "short")

    def test_wrong_training_window_or_layout_is_rejected(self):
        original = copy.deepcopy(self.manifest)
        changes = ({"config_source": "github-action-mainnet-flat.yaml"}, {"snapshot_source": "halfpath"},
                   {"payloads": "/realblocks/payloads.jsonl"}, {"amount": 10})
        for change in changes:
            with self.subTest(change=change), self.assertRaises(ValueError):
                self.manifest = original | change
                self.save_manifest()
                validate_bundle(self.root, self.run, self.sha)
        self.manifest = original
        self.manifest["replay_window"]["warmup"] = 0
        self.save_manifest()
        with self.assertRaises(ValueError):
            validate_bundle(self.root, self.run, self.sha)

    def test_http_only_or_mismatched_recorded_replay_is_rejected(self):
        path = self.collection / "expb.log"
        original = path.read_text()
        path.write_text(original.replace("EXPB_ENGINE_RESULT", "HTTP_RESULT"))
        self.seal_artifacts()
        with self.assertRaises(ValueError):
            validate_bundle(self.root, self.run, self.sha)
        path.write_text(original)
        (self.collection / "execution-validation.json").write_text(json.dumps(self.recorded | {"engine_api_results": 1}))
        self.seal_artifacts()
        with self.assertRaises(ValueError):
            validate_bundle(self.root, self.run, self.sha)

    def test_different_image_or_missing_raw_trace_is_rejected(self):
        path = self.refs / "manifest.json"
        path.write_text(json.dumps(self.references | {"image": "nethermindeth/nethermind@sha256:" + "c" * 64}))
        self.seal_artifacts()
        with self.assertRaises(ValueError):
            validate_bundle(self.root, self.run, self.sha)
        path.write_text(json.dumps(self.references))
        (self.root / "nethermind-pgo-raw-data/nethermind-1.nettrace").unlink()
        self.seal_artifacts()
        with self.assertRaises(ValueError):
            validate_bundle(self.root, self.run, self.sha)

    def test_modified_duplicate_missing_extra_or_escaping_reference_is_rejected(self):
        original = self.references["files"]
        variants = [(original * 2, "hash, size or uniqueness"), ([], "inventory is incomplete")]
        for change, reason in (({"path": "../outside.dll"}, "DLL allowlist"),
                               ({"path": "/app/file.dll"}, "DLL allowlist"),
                               ({"path": "app/config.json"}, "DLL allowlist"),
                               ({"sha256": "0" * 64}, "hash, size or uniqueness"),
                               ({"bytes": 0}, "hash, size or uniqueness")):
            variants.append(([original[0] | change, original[1]], reason))
        for files, reason in variants:
            with self.subTest(files=files), self.assertRaisesRegex(ValueError, reason):
                verify_references(self.refs, self.references | {"files": files})
        path = self.refs / "app/extra.dll"
        path.write_bytes(b"unexpected")
        with self.assertRaises(ValueError):
            verify_references(self.refs, self.references)

    def test_rebuilt_dll_must_match_exactly(self):
        rebuilt = self.root / "rebuilt"
        rebuilt.mkdir()
        path = rebuilt / "Nethermind.Trie.dll"
        path.write_bytes((self.refs / "app/Nethermind.Trie.dll").read_bytes())
        self.assertEqual(compare_rebuilt(self.refs, self.references, rebuilt), {"matched_application_dlls": 1})
        path.write_bytes(b"changed")
        with self.assertRaises(ValueError):
            compare_rebuilt(self.refs, self.references, rebuilt)
        path.unlink()
        with self.assertRaises(ValueError):
            compare_rebuilt(self.refs, self.references, rebuilt)
        path.write_bytes((self.refs / "app/Nethermind.Trie.dll").read_bytes())
        (rebuilt / "Extra.DLL").write_bytes(b"unexpected")
        with self.assertRaises(ValueError):
            compare_rebuilt(self.refs, self.references, rebuilt)

    def test_symlink_outside_reference_root_is_rejected(self):
        outside = self.root / "outside.dll"
        outside.write_bytes(b"outside")
        link = self.refs / "app/link.dll"
        try:
            link.symlink_to(outside)
        except OSError:
            self.skipTest("symlink creation unavailable")
        manifest = copy.deepcopy(self.references)
        manifest["files"].append({"path": "app/link.dll", "bytes": outside.stat().st_size, "sha256": digest(outside)})
        with self.assertRaisesRegex(ValueError, "reference escapes the bundle"):
            verify_references(self.refs, manifest)

    def test_uppercase_extra_reference_is_rejected_on_both_platforms(self):
        (self.refs / "app/Extra.DLL").write_bytes(b"unexpected")
        with self.assertRaisesRegex(ValueError, "inventory is incomplete"):
            verify_references(self.refs, self.references)

    def test_tag_based_image_or_empty_training_files_are_rejected(self):
        self.manifest["image"] = "nethermindeth/nethermind:latest"
        self.save_manifest()
        (self.refs / "manifest.json").write_text(json.dumps(self.references | {"image": self.manifest["image"]}))
        self.seal_artifacts()
        with self.assertRaisesRegex(ValueError, "image identity"):
            validate_bundle(self.root, self.run, self.sha)
        self.manifest["image"] = self.references["image"]
        self.save_manifest()
        (self.refs / "manifest.json").write_text(json.dumps(self.references))
        for name in ("nethermind-pgo-profile/nethermind.mibc", "nethermind-pgo-raw-data/nethermind-1.nettrace"):
            path = self.root / name
            path.write_bytes(b"")
            self.seal_artifacts()
            with self.subTest(name=name), self.assertRaisesRegex(ValueError, "profile or raw trace is missing"):
                validate_bundle(self.root, self.run, self.sha)
            path.write_bytes(b"fixture")

    def test_substituted_profile_or_archive_is_rejected(self):
        (self.root / "nethermind-pgo-profile/nethermind.mibc").write_bytes(b"other run")
        with self.assertRaisesRegex(ValueError, "authenticated artifact"):
            verify_artifacts(self.root, self.run, "42-1")
        self.seal_artifacts()
        (self.root / ".artifact-archives/3.zip").write_bytes(b"other archive")
        with self.assertRaisesRegex(ValueError, "GitHub digest"):
            verify_artifacts(self.root, self.run, "42-1")

    def test_other_run_attempt_or_source_artifact_is_rejected(self):
        path = self.root / "artifact-api.json"
        original = json.loads(path.read_text())
        variants = (("workflow_run", {"id": 43}), ("workflow_run", {"head_sha": "c" * 40}),
                    ("workflow_run", {"repository_id": 2}), ("workflow_run", {"head_repository_id": 2}),
                    (None, {"created_at": "2025-12-31T23:59:59Z"}),
                    (None, {"created_at": "2026-01-01T00:03:00Z"}), (None, {"expired": True}))
        for field, change in variants:
            metadata = copy.deepcopy(original)
            artifact = metadata["artifacts"][2]
            if field:
                artifact[field].update(change)
            else:
                artifact.update(change)
            path.write_text(json.dumps(metadata))
            with self.subTest(change=change), self.assertRaisesRegex(ValueError, "declared run attempt"):
                verify_artifacts(self.root, self.run, "42-1")

    def test_header_numbers_are_checked_independently_of_declared_range(self):
        self.manifest["replay_window"]["headers"][11]["number"] += 1
        self.save_manifest()
        with self.assertRaisesRegex(ValueError, "declared block window"):
            validate_bundle(self.root, self.run, self.sha)

    def test_selection_requires_one_exact_positive_method(self):
        method = {"Method": "[Nethermind.Trie]Nethermind.Trie.TrieNode.GetMemorySize(bool)",
                  "InstrumentationData": [{"InstrumentationKind": "EdgeIntCount", "Data": 2}]}
        full = {"Methods": [method]}
        self.assertEqual(validate_selection(full, full, method["Method"])["instrumentation_records"], 1)
        for selected in ({"Methods": []}, {"Methods": [method, method]},
                         {"Methods": [method | {"InstrumentationData": []}]}):
            with self.subTest(selected=selected), self.assertRaises(ValueError):
                validate_selection(full, selected, method["Method"])
        with self.assertRaises(ValueError):
            validate_selection({"Methods": [method, method]}, full, method["Method"])
        zero = copy.deepcopy(method)
        zero["InstrumentationData"][0]["Data"] = 0
        with self.assertRaises(ValueError):
            validate_selection({"Methods": [zero]}, {"Methods": [zero]}, method["Method"])


if __name__ == "__main__":
    unittest.main()
