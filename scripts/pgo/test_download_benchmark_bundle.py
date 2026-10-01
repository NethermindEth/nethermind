# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import copy
import json
from pathlib import Path
import shutil
import stat
import tempfile
import unittest
import zipfile

from download_benchmark_bundle import download_bundle, extract_archive, list_artifacts
import test_validate_benchmark_bundle as fixtures


class DownloadBundleTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.BenchmarkBundleTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name) / "download"
        self.run = copy.deepcopy(self.fixture.run)
        self.metadata = json.loads((self.fixture.root / "artifact-api.json").read_text())
        self.calls = []

    def request(self, endpoint, destination=None):
        self.calls.append(endpoint)
        if destination is not None:
            identifier = endpoint.split("/")[-2]
            shutil.copyfile(self.fixture.root / ".artifact-archives" / f"{identifier}.zip", destination)
            return None
        if "artifacts?" in endpoint:
            return copy.deepcopy(self.metadata)
        return copy.deepcopy(self.run)

    def test_authenticated_download_validates_complete_bundle(self):
        report = download_bundle(self.root, 42, self.fixture.sha, self.request)
        self.assertEqual(report["engine_api_results"], 2022)
        self.assertEqual(report["run_id"], 42)
        self.assertEqual(len(list((self.root / ".artifact-archives").glob("*.zip"))), 4)
        self.assertEqual(self.calls.count("actions/runs/42"), 2)
        self.assertNotIn("config_source", json.dumps(report))

    def test_digest_failure_prevents_extraction(self):
        self.metadata["artifacts"][0]["digest"] = "sha256:" + "0" * 64
        with self.assertRaisesRegex(ValueError, "GitHub digest"):
            download_bundle(self.root, 42, self.fixture.sha, self.request)
        self.assertFalse((self.root / "pgo-collection-42-1").exists())

    def test_wrong_run_or_missing_artifact_never_creates_destination(self):
        for change, reason in (({"id": 43}, "different training run"),
                               ({"head_sha": "b" * 40}, "identity or completion")):
            with self.subTest(change=change):
                original = self.run
                self.run = original | change
                with self.assertRaisesRegex(ValueError, reason):
                    download_bundle(self.root, 42, self.fixture.sha, self.request)
                self.run = original
                self.assertFalse(self.root.exists())
        self.metadata["artifacts"].pop()
        self.metadata["total_count"] -= 1
        with self.assertRaisesRegex(ValueError, "missing or ambiguous"):
            download_bundle(self.root, 42, self.fixture.sha, self.request)
        self.assertFalse(self.root.exists())

    def test_rerun_during_download_is_rejected(self):
        def request(endpoint, destination=None):
            result = self.request(endpoint, destination)
            if endpoint == "actions/runs/42" and self.calls.count(endpoint) == 2:
                result["run_attempt"] = 2
            return result
        with self.assertRaisesRegex(ValueError, "changed during"):
            download_bundle(self.root, 42, self.fixture.sha, request)
        self.assertFalse((self.root / "bundle-validation.json").exists())

    def test_existing_destination_is_preserved(self):
        self.root.mkdir()
        sentinel = self.root / "sentinel"
        sentinel.write_text("keep")
        with self.assertRaises(FileExistsError):
            download_bundle(self.root, 42, self.fixture.sha, self.request)
        self.assertEqual(sentinel.read_text(), "keep")
        self.assertFalse(any("/zip" in call for call in self.calls))

    def test_pagination_and_inconsistent_inventory(self):
        pages = [{"total_count": 101, "artifacts": [{"id": i} for i in range(100)]},
                 {"total_count": 101, "artifacts": [{"id": 100}]}]
        def request(endpoint):
            return pages[int(endpoint.rsplit("=", 1)[1]) - 1]
        self.assertEqual(len(list_artifacts(42, request)["artifacts"]), 101)
        for second, reason in (({"total_count": 102, "artifacts": [{"id": 100}]}, "changed"),
                               ({"total_count": 101, "artifacts": []}, "incomplete"),
                               ({"total_count": 101, "artifacts": [{"id": 99}]}, "duplicate")):
            with self.subTest(reason=reason):
                pages[1] = second
                with self.assertRaisesRegex(ValueError, reason):
                    list_artifacts(42, request)

    def test_unsafe_members_are_rejected_before_any_file_write(self):
        cases = [("../escape", 0), ("/absolute/", 0), ("unsafe\\directory/", 0),
                 ("null\0suffix", 0), ("C:/escape", 0), (".", 0),
                 ("link", stat.S_IFLNK | 0o777), ("device", stat.S_IFCHR | 0o600),
                 ("safe", 0), ("folder/../escape/", 0)]
        for index, (name, mode) in enumerate(cases):
            with self.subTest(name=name):
                archive = self.root.parent / f"unsafe-{index}.zip"
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr("safe", b"must not be written")
                    info = zipfile.ZipInfo()
                    info.filename = name
                    info.orig_filename = name
                    info.external_attr = mode << 16
                    bundle.writestr(info, b"unsafe")
                destination = self.root.parent / f"extracted-{index}"
                with self.assertRaisesRegex(ValueError, "unsafe or duplicate"):
                    extract_archive(archive, destination)
                self.assertEqual(list(destination.rglob("*")), [])


if __name__ == "__main__":
    unittest.main()
