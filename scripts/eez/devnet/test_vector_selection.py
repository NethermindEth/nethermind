import base64
import contextlib
import io
import gzip
import hashlib
import json
from pathlib import Path
from types import SimpleNamespace
import tempfile
import unittest
from unittest.mock import patch

import upstream


class VectorSelectionTests(unittest.TestCase):
    names = ("rollup0-transcripts.json", "rollup0-limits-transcripts.json")
    revision = "a" * 40
    core = "b" * 40

    def invoke(self, directory, available, merged=True, data=None, truncated=False, old_replay=False):
        args = SimpleNamespace(revision="", directory=directory / "vectors", output=directory / "vectors/upstream.json")
        document = {"format": "eez-prove-v2-transcript-1", "cases": [{"name": "baseline", "steps": [
            {"action": "open", "stream": "main"}, {"action": "send", "stream": "main", "protobuf_hex": ""},
            {"action": "receive", "stream": "main", "protobuf_hex": ""}, {"action": "eof", "stream": "main"}]}]}
        payloads = {}
        blobs = {name: str(index + 3) * 40 for index, name in enumerate(self.names)}
        for name in self.names:
            case = dict(document, source_file=name)
            payloads[name] = json.dumps(case).encode() if data is None else data
        paths = []

        def api(path):
            paths.append(path)
            if path == "commits/main": return {"sha": self.revision}
            if path == "git/trees/" + self.revision:
                return {"tree": [{"path": "eez-core-protocol", "mode": "160000", "sha": self.core}]}
            if path == "git/trees/" + self.revision + "?recursive=1":
                return {"tree": [{"path": "crates/eez-control-rpc/conformance/" + name, "type": "blob", "sha": blobs[name]} for name in available], "truncated": truncated}
            if path == "pulls/229": return {"merged": merged, "merge_commit_sha": "e" * 40}
            if path.startswith("compare/" + "e" * 40): return {"merge_base_commit": {"sha": ("f" if old_replay else "e") * 40}}
            if path.startswith("git/blobs/"):
                blob = path.removeprefix("git/blobs/")
                name = next(name for name in self.names if blobs[name] == blob)
                payload = payloads[name]
                return {"sha": blob, "encoding": "base64", "size": len(payload), "content": base64.b64encode(payload).decode()}
            raise AssertionError("unexpected upstream metadata request " + path)

        with patch.dict(upstream.os.environ, {"GITHUB_OUTPUT": str(directory / "outputs")}), \
                patch.object(upstream, "api", side_effect=api), contextlib.redirect_stdout(io.StringIO()):
            upstream.vectors(args)
        return args, paths, json.loads(args.output.read_text()), payloads

    def test_public_main_freezes_source_core_and_downloads_both_vector_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, paths, manifest, payloads = self.invoke(Path(temporary), self.names)
            self.assertEqual(manifest["revision"], self.revision)
            self.assertEqual(manifest["core_revision"], self.core)
            self.assertEqual(manifest["vectors"]["source"], "public-main")
            self.assertEqual(manifest["vectors"]["revision"], self.revision)
            self.assertEqual(paths.count("commits/main"), 1)
            self.assertIn("pulls/229", paths)
            for index, name in enumerate(self.names):
                self.assertIn("git/blobs/" + str(index + 3) * 40, paths)
                self.assertEqual((args.directory / name).read_bytes(), payloads[name])
                self.assertEqual(manifest["vectors"]["files"][name], upstream.hashlib.sha256(payloads[name]).hexdigest())

    def test_replay_before_quota_contract_merge_cannot_use_old_vectors(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "predates the current streaming quota contract"):
            self.invoke(Path(temporary), self.names, old_replay=True)

    def test_replay_before_quota_contract_merge_cannot_use_old_vectors(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "predates the current streaming quota contract"):
            self.invoke(Path(temporary), self.names, old_replay=True)

    def test_vectors_above_contents_api_limit_use_the_selected_git_blob(self):
        snapshot = Path(upstream.__file__).resolve().parents[3] / "src/Nethermind/Nethermind.Eez.Test/Fixtures/streaming-conformance"
        payload = (snapshot / self.names[0]).read_bytes()
        self.assertGreater(len(payload), 1024 * 1024)
        with tempfile.TemporaryDirectory() as temporary:
            args, paths, manifest, _ = self.invoke(Path(temporary), self.names, data=payload)
            self.assertTrue(all(not path.startswith("contents/") for path in paths))
            self.assertEqual((args.directory / self.names[0]).read_bytes(), payload)
            self.assertEqual(manifest["vectors"]["files"][self.names[0]], "006a64817472bf907122516e06a0cf82852c45d637fec467678fb2b2d515deaf")

    def test_before_merge_uses_exact_reviewed_snapshot_with_distinct_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, paths, manifest, _ = self.invoke(Path(temporary), self.names, merged=False)
            self.assertNotIn("git/trees/" + self.revision + "?recursive=1", paths)
            self.assertEqual(manifest["revision"], self.revision)
            self.assertEqual(manifest["vectors"]["source"], "reviewed-pr-snapshot-before-merge")
            self.assertEqual(manifest["vectors"]["revision"], "ad5792d8044fd2c3a584a82c9b08d1e76aef67ce")
            for name in self.names:
                self.assertEqual(manifest["vectors"]["files"][name], upstream.hashlib.sha256((args.directory / name).read_bytes()).hexdigest())

    def test_missing_partial_or_truncated_suite_cannot_use_a_silent_fallback(self):
        for available, merged, truncated in (([], True, False), ([self.names[0]], True, False), (self.names, True, True)):
            with self.subTest(available=available, merged=merged, truncated=truncated), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaises(ValueError):
                    self.invoke(Path(temporary), available, merged=merged, truncated=truncated)
                self.assertNotIn("vectors", json.loads((Path(temporary) / "vectors/upstream.json").read_text()))

    def test_changed_reviewed_snapshot_digest_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(upstream.hashlib, "sha256") as digest:
            digest.return_value.hexdigest.return_value = "0" * 64
            with self.assertRaisesRegex(ValueError, "snapshot digest mismatch"):
                self.invoke(Path(temporary), [], merged=False)

    def test_empty_case_or_missing_requests_responses_cannot_count_as_conformance(self):
        no_response = [{"action": action, "stream": "main", "protobuf_hex": ""} for action in ("open", "send", "send", "eof")]
        no_request = [{"action": action, "stream": "main", "protobuf_hex": ""} for action in ("open", "receive", "receive", "eof")]
        for steps in ([], [{"action": "open", "stream": "main"}, {"action": "eof", "stream": "main"}], no_response, no_request):
            payload = json.dumps({"format": "eez-prove-v2-transcript-1", "cases": [{"name": "empty", "steps": steps}]}).encode()
            with self.subTest(steps=steps), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaisesRegex(ValueError, "incomplete vector case"):
                    self.invoke(Path(temporary), self.names, data=payload)


class ProfileResolutionTests(unittest.TestCase):
    profiles = (("rollup0", "eez-association/eez-rollup0"), ("rollupx", "eez-association/eez-rollupX"))
    def resolve(self, profile, revision="", merge_base=None, core_entries=None):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        head = "a" * 40
        core = "b" * 40
        repository = dict(self.profiles)[profile]
        calls = []

        def metadata(path, selected=upstream.REPOSITORY):
            self.assertEqual(selected, repository)
            calls.append(path)
            if path == "commits/main": return {"sha": head}
            if path.startswith("compare/"): return {"merge_base_commit": {"sha": merge_base}}
            if path == "git/trees/" + (revision or head):
                entries = [{"path": "eez-core-protocol", "mode": "160000", "sha": core}]
                return {"tree": entries if core_entries is None else core_entries}
            raise AssertionError("unexpected source lookup: " + path)

        args = SimpleNamespace(profile=profile, revision=revision, output=root / "manifest.json")
        with patch.dict(upstream.os.environ, {"GITHUB_OUTPUT": str(root / "outputs")}), \
                patch.object(upstream, "api", side_effect=metadata):
            upstream.resolve(args)
        return json.loads(args.output.read_text()), calls

    def test_each_profile_freezes_its_own_main_and_core_once(self):
        for profile, repository in self.profiles:
            with self.subTest(profile=profile):
                manifest, calls = self.resolve(profile)
                self.assertEqual(manifest["repository"], repository)
                self.assertEqual(manifest["profile"], profile)
                self.assertEqual(manifest["revision"], "a" * 40)
                self.assertEqual(manifest["core_revision"], "b" * 40)
                self.assertEqual(calls, ["commits/main", "git/trees/" + "a" * 40])

    def test_replay_must_be_an_ancestor_in_the_selected_repository(self):
        replay = "c" * 40
        for profile, repository in self.profiles:
            with self.subTest(profile=profile):
                manifest, _ = self.resolve(profile, replay, merge_base=replay)
                self.assertEqual(manifest["revision"], replay)
                self.assertEqual(manifest["main_at_resolution"], "a" * 40)
                with self.assertRaisesRegex(ValueError, "public upstream main history"):
                    self.resolve(profile, replay, merge_base="d" * 40)

    def test_missing_or_duplicate_core_cannot_freeze_a_profile(self):
        core = {"path": "eez-core-protocol", "mode": "160000", "sha": "b" * 40}
        for entries in ([], [core, core]):
            with self.subTest(entries=entries), self.assertRaisesRegex(ValueError, "unique Core gitlink"):
                self.resolve("rollupx", core_entries=entries)

    def test_unknown_profile_or_repository_never_sends_a_request(self):
        with patch.object(upstream.subprocess, "check_output") as request:
            with self.assertRaisesRegex(ValueError, "unsupported upstream profile"):
                upstream.resolve(SimpleNamespace(profile="other"))
            with self.assertRaisesRegex(ValueError, "unsupported upstream repository"):
                upstream.api("commits/main", "other/repository")
            request.assert_not_called()

    def test_api_sends_requests_to_each_literal_repository(self):
        for _, repository in self.profiles:
            with self.subTest(repository=repository), patch.object(upstream.subprocess, "check_output", return_value=b"{}") as request:
                self.assertEqual(upstream.api("commits/main", repository), {})
                request.assert_called_once_with(["gh", "api", "repos/" + repository + "/commits/main"], timeout=60)
        with patch.object(upstream.subprocess, "check_output", return_value=b"{}") as request:
            upstream.api("commits/main")
            request.assert_called_once_with(["gh", "api", "repos/eez-association/eez-rollup0/commits/main"], timeout=60)

    def test_rollup0_loader_cannot_read_rollupx_as_rollup0_vectors(self):
        with patch.object(upstream, "resolve") as resolve:
            with self.assertRaisesRegex(ValueError, "Rollup0 profile"):
                upstream.vectors(SimpleNamespace(profile="rollupx"))
            resolve.assert_not_called()


class RollupXExecutionSelectionTests(unittest.TestCase):
    repository = "eez-association/eez-rollupX"
    revision = "a" * 40
    core = "b" * 40
    prefix = "crates/eez-proof-signer/tests/fixtures/"
    names = ("baseline-lookup", "baseline-static")

    def invoke(self, root, available=True, merged=False, remove=None, add=None, truncated=False, fixture=None, revision="", malformed=None):
        snapshot = Path(upstream.__file__).resolve().parents[3] / "src/Nethermind/Nethermind.Eez.Test/Fixtures/rollupx/recorded-execution"
        reviewed = json.loads((snapshot / "provenance.json").read_text())
        payloads = {}
        for filename in reviewed["cases"]:
            name = filename.removesuffix(".json")
            identity = fixture or "updated-" + (name if name in self.names else "baseline-lookup")
            document = {"format": 2, "case": name, "fixture": identity, "events": [
                {"direction": "client"}, {"direction": "server"}, {"direction": "client"}, {"direction": "eof"}]}
            if malformed is not None: document.update(malformed)
            payloads[self.prefix + "streaming-v2/" + name + ".json"] = json.dumps(document).encode()
            payloads.setdefault(self.prefix + identity + "/l2-genesis.json", json.dumps({"config": {"chainId": 123 if name == "baseline-lookup" else 456}}).encode())
            payloads[self.prefix + identity + "/l1-genesis.json"] = json.dumps({"config": {"chainId": 1337}}).encode()
            payloads[self.prefix + identity + "/identity.json"] = json.dumps({"fixture": identity}).encode()
        if available:
            for name in reviewed["cases"]:
                payloads.setdefault(self.prefix + "streaming-v2/" + name, b"{}")
            if remove: payloads.pop(self.prefix + remove)
            if add:
                payloads[self.prefix + "streaming-v2/" + add] = json.dumps({"format": 2, "case": add.removesuffix(".json"),
                    "fixture": "updated-baseline-lookup", "events": [
                        {"direction": "client"}, {"direction": "server"}, {"direction": "client"}, {"direction": "eof"}]}).encode()
        else:
            payloads.clear()
        entries = []
        blobs = {}
        for path, data in payloads.items():
            digest = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
            blobs[digest] = data
            entries.append({"path": path, "type": "blob", "sha": digest})
        calls = []

        def metadata(path, repository=upstream.REPOSITORY):
            self.assertEqual(repository, self.repository)
            calls.append(path)
            if path == "commits/main": return {"sha": self.revision}
            if path.startswith("compare/"): return {"merge_base_commit": {"sha": revision}}
            if path == "git/trees/" + (revision or self.revision):
                return {"tree": [{"path": "eez-core-protocol", "mode": "160000", "sha": self.core}]}
            if path == "git/trees/" + (revision or self.revision) + "?recursive=1":
                return {"tree": entries, "truncated": truncated}
            if path == "pulls/89": return {"merged": merged}
            if path.startswith("git/blobs/"):
                digest = path.removeprefix("git/blobs/")
                data = blobs[digest]
                return {"sha": digest, "size": len(data), "encoding": "base64", "content": base64.b64encode(data).decode()}
            raise AssertionError("unexpected RollupX metadata request " + path)

        args = SimpleNamespace(profile="rollupx", revision=revision, directory=root / "vectors", output=root / "vectors/upstream.json")
        with patch.dict(upstream.os.environ, {"GITHUB_OUTPUT": str(root / "outputs")}), \
                patch.object(upstream, "api", side_effect=metadata), contextlib.redirect_stdout(io.StringIO()):
            upstream.rollupx_execution(args)
        return args, calls, json.loads(args.output.read_text()), payloads

    def test_current_main_downloads_exact_new_baselines_and_matching_genesis(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, calls, manifest, payloads = self.invoke(Path(temporary))
            source = manifest["execution_vectors"]
            self.assertEqual((manifest["repository"], manifest["revision"], manifest["core_revision"]),
                (self.repository, self.revision, self.core))
            self.assertEqual((source["source"], source["revision"], source["core_revision"]), ("public-main", self.revision, self.core))
            self.assertEqual(calls.count("commits/main"), 1)
            self.assertNotIn("pulls/89", calls)
            self.assertEqual(source["execution_baselines"], ["baseline-lookup", "baseline-static"])
            self.assertEqual((len(source["cases"]), source["streaming_replay"]), (29, "format2-raw"))
            for name in self.names:
                for local, remote in ((name + ".json", "streaming-v2/" + name + ".json"),
                        ("updated-" + name + ".genesis.json", "updated-" + name + "/l2-genesis.json")):
                    raw = gzip.decompress((args.directory / (local + ".gz")).read_bytes())
                    self.assertEqual(raw, payloads[self.prefix + remote])
                    self.assertEqual(source["files"][local], {"length": len(raw), "sha256": hashlib.sha256(raw).hexdigest()})

    def test_manifest_is_metadata_and_all_original_cases_are_downloaded(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, _, manifest, payloads = self.invoke(Path(temporary), add="manifest.json")
            source = manifest["execution_vectors"]
            self.assertEqual(len(source["cases"]), 29)
            self.assertNotIn("manifest.json", source["cases"])
            for name in source["cases"]:
                self.assertEqual(gzip.decompress((args.directory / (name + ".gz")).read_bytes()),
                    payloads[self.prefix + "streaming-v2/" + name])

    def test_explicit_public_replay_freezes_that_revision_instead_of_new_main(self):
        with tempfile.TemporaryDirectory() as temporary:
            _, calls, manifest, _ = self.invoke(Path(temporary), revision="c" * 40)
            self.assertEqual(manifest["execution_vectors"]["revision"], "c" * 40)
            self.assertIn("git/trees/" + "c" * 40 + "?recursive=1", calls)
            self.assertEqual(calls.count("commits/main"), 1)

    def test_before_merge_uses_only_reviewed_snapshot_with_distinct_provenance(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, calls, manifest, _ = self.invoke(Path(temporary), available=False)
            source = manifest["execution_vectors"]
            self.assertEqual(source["source"], "reviewed-pr-snapshot-before-merge")
            self.assertEqual(source["revision"], "759ae5f5cbf87c16b52cedfecc05aae737e90a87")
            self.assertEqual(source["core_revision"], "9a1f815336c20d28e5ec5cecbf195c41fbd5ab0f")
            self.assertIn("pulls/89", calls)
            self.assertEqual(len(source["files"]), 35)
            self.assertEqual(source, json.loads((args.directory / "provenance.json").read_text()))
            for name, expected in source["files"].items():
                raw = gzip.decompress((args.directory / (name + ".gz")).read_bytes())
                self.assertEqual((len(raw), hashlib.sha256(raw).hexdigest()), (expected["length"], expected["sha256"]))

    def test_missing_merged_suite_cannot_fall_back(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "missing the merged vector suite"):
            self.invoke(Path(temporary), available=False, merged=True)

    def test_new_missing_or_truncated_case_inventory_cannot_be_silently_ignored(self):
        for options in ({"add": "new-case.json"}, {"remove": "streaming-v2/rewind-boundaries.json"}, {"truncated": True}):
            with self.subTest(options=options), tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "inventory changed|tree is incomplete"):
                self.invoke(Path(temporary), **options)

    def test_native_v4_inventory_downloads_each_unchanged_case(self):
        with tempfile.TemporaryDirectory() as temporary:
            args, _, manifest, payloads = self.invoke(Path(temporary),
                remove="streaming-v2/single-block-proof.json", add="sync-range-proof.json")
            source = manifest["execution_vectors"]
            self.assertEqual(len(source["cases"]), 29)
            self.assertIn("sync-range-proof.json", source["cases"])
            self.assertNotIn("single-block-proof.json", source["cases"])
            for name in source["cases"]:
                self.assertEqual(gzip.decompress((args.directory / (name + ".gz")).read_bytes()),
                    payloads[self.prefix + "streaming-v2/" + name])

    def test_missing_matching_genesis_cannot_use_an_old_snapshot(self):
        for name in self.names:
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "missing execution input"):
                self.invoke(Path(temporary), remove="updated-" + name + "/l2-genesis.json")

    def test_fixture_cannot_escape_the_selected_directory(self):
        with tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "unsupported RollupX execution recording"):
            self.invoke(Path(temporary), fixture="../../other")

    def test_modified_reviewed_snapshot_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(upstream.gzip, "open", return_value=io.BytesIO(b"changed")), \
                self.assertRaisesRegex(ValueError, "snapshot digest mismatch"):
            self.invoke(Path(temporary), available=False)

    def test_unsupported_recording_schema_or_events_cannot_count_as_execution(self):
        for malformed in ({"format": 3}, {"case": "another-case"}, {"events": []},
                {"events": [{"direction": "client"}] * 4},
                {"events": [{"direction": action} for action in ("client", "server", "eof")]},
                {"events": [{"direction": action} for action in ("client", "server", "eof")] + [{"direction": "client"}] * 4094}):
            with self.subTest(malformed=list(malformed)), tempfile.TemporaryDirectory() as temporary, self.assertRaisesRegex(ValueError, "unsupported|incomplete"):
                self.invoke(Path(temporary), malformed=malformed)

    def test_git_blob_checks_identity_size_encoding_and_content(self):
        data = b'{"input":"independent-data"}'
        digest = hashlib.sha1(b"blob " + str(len(data)).encode() + b"\0" + data).hexdigest()
        valid = {"sha": digest, "size": len(data), "encoding": "base64", "content": base64.b64encode(data).decode()}
        with patch.object(upstream, "api", return_value=valid) as request:
            self.assertEqual(upstream.rollupx_blob({"sha": digest}), data)
            request.assert_called_once_with("git/blobs/" + digest, "eez-association/eez-rollupX")
        for changed in ({"sha": "0" * 40}, {"size": len(data) + 1}, {"size": 9 * 1024 * 1024},
                {"encoding": "none"}, {"content": base64.b64encode(b"x" * len(data)).decode()}):
            with self.subTest(changed=changed), patch.object(upstream, "api", return_value=dict(valid, **changed)), self.assertRaises(ValueError):
                upstream.rollupx_blob({"sha": digest})


if __name__ == "__main__":
    unittest.main()
