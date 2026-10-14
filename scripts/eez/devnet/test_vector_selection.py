import base64
import contextlib
import io
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

    def invoke(self, directory, available, merged=False, data=None, truncated=False):
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
            if path == "pulls/205": return {"merged": merged}
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
            self.assertNotIn("pulls/205", paths)
            for index, name in enumerate(self.names):
                self.assertIn("git/blobs/" + str(index + 3) * 40, paths)
                self.assertEqual((args.directory / name).read_bytes(), payloads[name])
                self.assertEqual(manifest["vectors"]["files"][name], upstream.hashlib.sha256(payloads[name]).hexdigest())

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
            args, _, manifest, _ = self.invoke(Path(temporary), [])
            self.assertEqual(manifest["revision"], self.revision)
            self.assertEqual(manifest["vectors"]["source"], "reviewed-pr-snapshot-before-merge")
            self.assertEqual(manifest["vectors"]["revision"], "d3c94296bd6f23b4a5e0104950948492ab36398b")
            for name in self.names:
                self.assertEqual(manifest["vectors"]["files"][name], upstream.hashlib.sha256((args.directory / name).read_bytes()).hexdigest())

    def test_missing_partial_or_truncated_suite_cannot_use_a_silent_fallback(self):
        for available, merged, truncated in (([], True, False), ([self.names[0]], False, False), (self.names, False, True)):
            with self.subTest(available=available, merged=merged, truncated=truncated), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaises(ValueError):
                    self.invoke(Path(temporary), available, merged=merged, truncated=truncated)
                self.assertNotIn("vectors", json.loads((Path(temporary) / "vectors/upstream.json").read_text()))

    def test_changed_reviewed_snapshot_digest_cannot_pass(self):
        with tempfile.TemporaryDirectory() as temporary, patch.object(upstream.hashlib, "sha256") as digest:
            digest.return_value.hexdigest.return_value = "0" * 64
            with self.assertRaisesRegex(ValueError, "snapshot digest mismatch"):
                self.invoke(Path(temporary), [])

    def test_empty_case_or_missing_requests_responses_cannot_count_as_conformance(self):
        no_response = [{"action": action, "stream": "main", "protobuf_hex": ""} for action in ("open", "send", "send", "eof")]
        no_request = [{"action": action, "stream": "main", "protobuf_hex": ""} for action in ("open", "receive", "receive", "eof")]
        for steps in ([], [{"action": "open", "stream": "main"}, {"action": "eof", "stream": "main"}], no_response, no_request):
            payload = json.dumps({"format": "eez-prove-v2-transcript-1", "cases": [{"name": "empty", "steps": steps}]}).encode()
            with self.subTest(steps=steps), tempfile.TemporaryDirectory() as temporary:
                with self.assertRaisesRegex(ValueError, "incomplete vector case"):
                    self.invoke(Path(temporary), self.names, data=payload)


if __name__ == "__main__":
    unittest.main()
