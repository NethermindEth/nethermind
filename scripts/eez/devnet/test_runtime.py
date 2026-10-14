import hashlib
import io
import json
from pathlib import Path
import tempfile
import subprocess
import unittest
from unittest.mock import patch
import warnings
import zipfile

import upstream


class RuntimeTests(unittest.TestCase):
    def test_runtime_requires_matching_digest_and_all_five_binaries(self):
        for failure in (None, "digest", "missing", "duplicate", "wrong-source", "wrong-run"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                blob = io.BytesIO()
                names = list(upstream.BINARIES)
                if failure == "missing": names.pop()
                if failure == "duplicate": names.append(names[0])
                with warnings.catch_warnings():
                    warnings.simplefilter("ignore", UserWarning)
                    with zipfile.ZipFile(blob, "w") as archive:
                        for name in names:
                            archive.writestr(name, b"binary")
                data = blob.getvalue()
                revision = "a" * 40
                run = {"id": 7, "head_sha": revision, "event": "push", "head_branch": "main", "conclusion": "success", "repository": {"full_name": upstream.REPOSITORY}}
                artifact = {"id": 8, "name": "e2e-build", "expired": False, "digest": "sha256:" + hashlib.sha256(data).hexdigest(), "size_in_bytes": len(data), "workflow_run": {"id": 7, "head_sha": revision}}
                if failure == "digest": artifact["digest"] = "sha256:" + "0" * 64
                if failure == "wrong-source": artifact["workflow_run"]["head_sha"] = "b" * 40
                if failure == "wrong-run": artifact["workflow_run"]["id"] = 9
                class Args:
                    pass
                args = Args()
                args.output = Path(directory) / "runtime"
                args.manifest = Path(directory) / "manifest"
                args.manifest.write_text(json.dumps({"repository": upstream.REPOSITORY, "revision": revision}))
                def download(command, stdout, **options):
                    self.assertFalse(options["check"])
                    self.assertEqual(command[-1], "repos/" + upstream.REPOSITORY + "/actions/artifacts/8/zip")
                    stdout.write(data)
                    return subprocess.CompletedProcess(command, 0, stderr=b"")
                with patch.object(upstream, "api", side_effect=[{"workflow_runs": [run]}, {"artifacts": [artifact]}]), patch.object(upstream.subprocess, "run", side_effect=download):
                    if failure is not None:
                        with self.assertRaises(ValueError):
                            upstream.runtime(args)
                        self.assertNotIn("runtime", json.loads(args.manifest.read_text()))
                    else:
                        upstream.runtime(args)
                        result = json.loads(args.manifest.read_text())["runtime"]
                        self.assertEqual(result["source"], "artifact")
                        self.assertEqual(result["run_id"], 7)
                        self.assertEqual(set(result["binaries"]), set(upstream.BINARIES))
                        for name in upstream.BINARIES:
                            self.assertEqual((args.output / name).read_bytes(), b"binary")
                            self.assertEqual((args.output / name).stat().st_mode & 0o777, 0o555)
                        self.assertFalse((args.output / "runtime.zip").exists())

    def test_only_unavailable_artifacts_fall_back_to_selected_source(self):
        for status in (401, 403, 404, 410, 500):
            with self.subTest(status=status), tempfile.TemporaryDirectory() as directory:
                revision = "a" * 40
                run = {"id": 7, "head_sha": revision, "event": "push", "head_branch": "main", "conclusion": "success", "repository": {"full_name": upstream.REPOSITORY}}
                artifact = {"id": 8, "name": "e2e-build", "expired": False, "digest": "sha256:" + "0" * 64, "size_in_bytes": 7, "workflow_run": {"id": 7, "head_sha": revision}}
                class Args:
                    pass
                args = Args()
                args.output = Path(directory) / "runtime"
                args.manifest = Path(directory) / "manifest"
                args.manifest.write_text(json.dumps({"repository": upstream.REPOSITORY, "revision": revision}))
                def download(command, stdout, **options):
                    stdout.write(b"partial")
                    return subprocess.CompletedProcess(command, 1, stderr=("gh: HTTP " + str(status)).encode())
                with patch.object(upstream, "api", side_effect=[{"workflow_runs": [run]}, {"artifacts": [artifact]}]), patch.object(upstream.subprocess, "run", side_effect=download):
                    if status == 500:
                        with self.assertRaises(subprocess.CalledProcessError):
                            upstream.runtime(args)
                        self.assertNotIn("runtime", json.loads(args.manifest.read_text()))
                    else:
                        upstream.runtime(args)
                        manifest = json.loads(args.manifest.read_text())
                        self.assertEqual(manifest["revision"], revision)
                        self.assertEqual(manifest["runtime"]["source"], "build-required")
                        self.assertEqual(manifest["runtime"]["artifact_unavailable"], status)
                        self.assertFalse((args.output / "runtime.zip").exists())

    def test_missing_matching_runtime_requires_same_source_build(self):
        class Args:
            pass
        with tempfile.TemporaryDirectory() as directory:
            args = Args()
            args.output = Path(directory) / "runtime"
            args.manifest = Path(directory) / "manifest"
            args.manifest.write_text(json.dumps({"repository": upstream.REPOSITORY, "revision": "a" * 40}))
            with patch.object(upstream, "api", return_value={"workflow_runs": []}), patch.object(upstream.subprocess, "run") as execute:
                upstream.runtime(args)
            manifest = json.loads(args.manifest.read_text())
            self.assertEqual(manifest["revision"], "a" * 40)
            self.assertEqual(manifest["runtime"]["source"], "build-required")
            execute.assert_not_called()


if __name__ == "__main__":
    unittest.main()
