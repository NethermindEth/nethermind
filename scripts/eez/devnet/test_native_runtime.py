import json
import os
from pathlib import Path
import subprocess
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import upstream


class NativeRuntimeTests(unittest.TestCase):
    def test_only_matching_published_runtime_is_imported_and_validation_failures_are_fatal(self):
        failures = (None, "prior-attempt", "unavailable", "source", "repository", "event", "branch", "conclusion", "workflow", "digest", "ambiguous", "payload",
                    "manifest", "paths", "binary", "checkout", "core", "tool-version")
        for failure in failures:
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                args = SimpleNamespace(manifest=root / "manifest.json", output=root / "runtime", checkout=root / "source")
                manifest = {"repository": upstream.PROFILES["rollupx"], "profile": "rollupx",
                            "revision": "a" * 40, "core_revision": "b" * 40}
                args.manifest.write_text(json.dumps(manifest))
                run = {"id": 7, "run_attempt": 2, "path": ".github/workflows/ci.yml", "head_sha": manifest["revision"],
                       "event": "push", "head_branch": "main", "conclusion": "success",
                       "repository": {"full_name": manifest["repository"]}}
                if failure == "source": run["head_sha"] = "c" * 40
                if failure == "repository": run["repository"]["full_name"] = upstream.REPOSITORY
                if failure == "event": run["event"] = "pull_request"
                if failure == "branch": run["head_branch"] = "other"
                if failure == "conclusion": run["conclusion"] = "failure"
                if failure == "workflow": run["path"] = ".github/workflows/other.yml"
                image = "ghcr.io/eez-association/eez-rollupx-ci-runtime"
                reference = image + "@sha256:" + "d" * 64
                pulls = []
                helpers = []
                exports = []

                def execute(command, **options):
                    if command[0] == "docker":
                        attempt = 2 - len(pulls)
                        pulls.append(attempt)
                        self.assertEqual(command, ["docker", "pull", "--platform", "linux/amd64",
                                                  image + ":sha-" + manifest["revision"] + "-7-" + str(attempt)])
                        unavailable = failure == "unavailable" or failure == "prior-attempt" and attempt == 2
                        return subprocess.CompletedProcess(command, 1 if unavailable else 0)
                    self.assertIs(options["check"], True)
                    environment = options["env"]
                    self.assertEqual(environment["GITHUB_SHA"], manifest["revision"])
                    self.assertEqual(environment["GITHUB_REPOSITORY"], manifest["repository"])
                    self.assertEqual(environment["EEZ_EXPECT_PUBLISHED"], "true")
                    helper = Path(command[1]).name
                    helpers.append(helper)
                    if failure == "payload" and helper == "ci-payload.sh" or failure == "manifest" and helper == "prepare-runtime-artifacts.sh":
                        raise subprocess.CalledProcessError(1, command)
                    if helper == "ci-payload.sh":
                        self.assertEqual(command[2:5], ["fetch", "runtime", reference])
                    else:
                        self.assertEqual(helper, "prepare-runtime-artifacts.sh")
                        binaries = Path(environment["RUNNER_TEMP"]) / "export/bin"
                        exports.append(binaries)
                        binaries.mkdir(parents=True)
                        for name in upstream.BINARIES + ("e2e-causal-verify", "verify_blob_evidence"):
                            if failure == "binary" and name == "verify_blob_evidence": continue
                            binary = binaries / name
                            binary.write_text("selected upstream binary")
                            binary.chmod(0o555)
                        for name in ("rustc", "cargo"):
                            (binaries.parent / (name + "-version.txt")).write_text(
                                "invalid" if failure == "tool-version" else name + " 1.95.0 (published)"
                                + ("\nbinary: rustc\ncommit-hash: " + "a" * 40 + "\nhost: x86_64-unknown-linux-gnu\nrelease: 1.95.0\n" if name == "rustc" else "\n"))
                        Path(environment["GITHUB_PATH"]).write_text(str(binaries) + "\n" + ("duplicate\n" if failure == "paths" else ""))
                    return subprocess.CompletedProcess(command, 0)

                def output(command, **options):
                    if command[0] == "docker":
                        self.assertEqual(command, ["docker", "image", "inspect", "--format", "{{json .RepoDigests}}",
                                                  image + ":sha-" + manifest["revision"] + "-7-" + str(pulls[-1])])
                        references = [] if failure == "digest" else [reference]
                        if failure == "ambiguous": references.append(image + "@sha256:" + "e" * 64)
                        return json.dumps(references)
                    self.assertEqual(command[0], "git")
                    source = Path(command[2]) == args.checkout
                    return "c" * 40 if failure == ("checkout" if source else "core") else manifest["revision" if source else "core_revision"]

                with patch.object(upstream, "api", return_value={"workflow_runs": [run]}) as api, \
                     patch.object(upstream.subprocess, "run", side_effect=execute), \
                     patch.object(upstream.subprocess, "check_output", side_effect=output):
                    if failure not in (None, "prior-attempt", "unavailable", "workflow"):
                        with self.assertRaises((ValueError, FileNotFoundError, subprocess.CalledProcessError)):
                            upstream.runtime(args)
                        self.assertNotIn("runtime", json.loads(args.manifest.read_text()))
                    else:
                        upstream.runtime(args)
                        result = json.loads(args.manifest.read_text())["runtime"]
                        if failure in ("unavailable", "workflow"):
                            self.assertEqual(result, {"source": "build-required"})
                            self.assertEqual(list(args.output.iterdir()), [])
                            self.assertEqual(helpers, [])
                        else:
                            self.assertEqual(result["source"], "upstream-payload")
                            self.assertEqual(result["rustc"], "rustc 1.95.0 (published)")
                            self.assertIn("host: x86_64-unknown-linux-gnu", (args.output / "rustc-version.txt").read_text())
                            self.assertEqual(result["run_attempt"], 1 if failure == "prior-attempt" else 2)
                            self.assertEqual(pulls, [2, 1] if failure == "prior-attempt" else [2])
                            self.assertEqual((result["run_id"], result["reference"]), (7, reference))
                            self.assertEqual(len(result["binaries"]), 7)
                            self.assertEqual(helpers, ["ci-payload.sh", "prepare-runtime-artifacts.sh"])
                            for name in result["binaries"]:
                                self.assertTrue(os.access(args.output / name, os.X_OK))
                    self.assertEqual(api.call_args.args[1], manifest["repository"])
                    for path in exports:
                        self.assertFalse(path.exists())
