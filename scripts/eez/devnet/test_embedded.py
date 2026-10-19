import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import patch

import embedded
from test_canonical_snapshot import Node


class EmbeddedCoverageTests(unittest.TestCase):
    def test_new_upstream_case_is_missing_and_ignored_case_is_not_passed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "crates/eez-node-e2e/tests/durability.rs"
            path.parent.mkdir(parents=True)
            path.write_text('#[tokio::test]\nasync fn settled_safe_prefix_survives_restart_and_fresh_follower_replay() {}\n'
                            '#[test]\n#[ignore = "known upstream defect"]\nfn ignored_case() {}\n'
                            '#[tokio::test]\nasync fn new_upstream_case() {}\n')
            cases = {case["test"]: case for case in embedded.inventory(root)}
            self.assertEqual("planned", cases["settled_safe_prefix_survives_restart_and_fresh_follower_replay"]["candidate_status"])
            self.assertEqual("required_but_missing", cases["new_upstream_case"]["candidate_status"])
            self.assertTrue(cases["ignored_case"]["upstream_ignored"])
            self.assertEqual("required_but_missing", cases["ignored_case"]["candidate_status"])

    def test_observation_insertion_rejects_missing_and_duplicate_boundaries(self):
        for source in ("changed assertion", "expected assertion\nexpected assertion"):
            with self.subTest(source=source), self.assertRaisesRegex(ValueError, "boundary changed"):
                embedded.insert(source, "expected assertion", "candidate assertion")

    def test_reports_require_every_candidate_observation_and_exact_provenance(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        valid = {"result": "pass", "case": "resumption", "phase": "before-resumption",
                 "verified_blocks": 5, "candidate_revision": revision, "image_id": image}
        mutations = ((None, None), ("result", "fail"), ("candidate_revision", "c" * 40),
                     ("image_id", "sha256:" + "d" * 64), ("verified_blocks", 1), ("phase", "other"))
        for field, value in mutations:
            with self.subTest(field=field), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                inventory = root / "inventory.json"
                inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/resumption.rs",
                    "test": "resumed_batch_settles_only_its_new_suffix", "candidate_status": "planned"}]}))
                before, after = copy.deepcopy(valid), copy.deepcopy(valid)
                after["phase"] = "after-resumption"
                if field is not None: after[field] = value
                (root / "resumption-before-resumption.json").write_text(json.dumps(before))
                (root / "resumption-after-resumption.json").write_text(json.dumps(after))
                (root / "contracts.json").write_text("{\"upstream-contract-results\": true}")
                args = SimpleNamespace(inventory=inventory, directory=root)
                with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image}):
                    if field is None:
                        embedded.report(args)
                        result = json.loads((root / "candidate-coverage.json").read_text())
                        self.assertEqual("passed", result["cases"][0]["candidate_status"])
                    else:
                        with self.assertRaises((AssertionError, ValueError)):
                            embedded.report(args)

    def test_durability_requires_same_database_after_restart_and_new_database_for_fresh_recovery(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        for mutation in (None, "restart-database", "fresh-database", "restart-container"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                inventory = root / "inventory.json"
                inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/durability.rs",
                    "test": "settled_safe_prefix_survives_restart_and_fresh_follower_replay", "candidate_status": "planned"}]}))
                phases = embedded.CASES["durability"]["settled_safe_prefix_survives_restart_and_fresh_follower_replay"][1]
                for index, phase in enumerate(phases):
                    observation = {"result": "pass", "case": "durability", "phase": phase,
                        "verified_blocks": 5, "candidate_revision": revision, "image_id": image,
                        "database_id": "fresh" if index == 3 else "existing",
                        "container": "before" if index == 0 else "restarted"}
                    if mutation == "restart-database" and index == 1: observation["database_id"] = "wrong"
                    if mutation == "fresh-database" and index == 3: observation["database_id"] = "existing"
                    if mutation == "restart-container" and index == 1: observation["container"] = "before"
                    (root / ("durability-" + phase + ".json")).write_text(json.dumps(observation))
                (root / "contracts.json").write_text("{\"upstream-contract-results\": true}")
                args = SimpleNamespace(inventory=inventory, directory=root)
                with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image}):
                    if mutation is None:
                        embedded.report(args)
                        result = json.loads((root / "candidate-coverage.json").read_text())
                        self.assertEqual("passed", result["cases"][0]["candidate_status"])
                    else:
                        with self.assertRaises(ValueError): embedded.report(args)

    def test_new_case_in_adapted_suite_requires_observations(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "crates/eez-node-e2e/tests/durability.rs"
            path.parent.mkdir(parents=True)
            path.write_text('#[tokio::test]\nasync fn settled_safe_prefix_survives_restart_and_fresh_follower_replay() {}\n'
                            '#[tokio::test]\nasync fn new_upstream_case() {}\n')
            manifest = root / "upstream.json"
            manifest.write_text(json.dumps({"profile": "rollup0", "revision": "a" * 40, "core_revision": "a" * 40}))
            args = SimpleNamespace(upstream=root, manifest=manifest, output=root / "inventory.json")
            with patch("embedded.subprocess.check_output", return_value="a" * 40), self.assertRaisesRegex(ValueError, "suite inventory changed"):
                embedded.prepare(args)

    def test_actual_verification_checks_running_image_source_and_canonical_prefix(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        for failure in (None, "image", "source", "container", "l1-reorg", "candidate-fork", "prefix"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                image_info = {"Id": image, "Config": {"Labels": {"org.opencontainers.image.revision": revision}}}
                container = {"Id": "container-id", "Image": image}
                if failure == "image": image_info["Id"] = "other-image"
                if failure == "source": image_info["Config"]["Labels"]["org.opencontainers.image.revision"] = "c" * 40
                if failure == "container": container["Image"] = "other-image"
                nodes = {role: Node(role) for role in ("l1", "reference", "candidate")}
                if failure == "l1-reorg": nodes["l1"].l1_hash = "fork"
                if failure == "candidate-fork": nodes["candidate"].canonical_hash = "fork"
                settings = {"case": "resumption", "phase": "after-resumption", "candidate_revision": revision,
                    "image_id": image, "container": "container-name", "database_id": "database",
                    "result_directory": directory, "rollup_id": "1", "l1_chain_id": "1",
                    "registry": "registry", "minimum_height": 1, **{role: role for role in nodes}}
                settings_path = root / "settings.json"
                settings_path.write_text(json.dumps(settings))
                result = {"height": 1, "endpoint": "end", "verified_blocks": 2, "verified_receipts": 1, "verified_native_transactions": 1}
                with contextlib.ExitStack() as stack:
                    stack.enter_context(patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_EMBEDDED_RESULTS": directory}))
                    stack.enter_context(patch("embedded.subprocess.check_output", side_effect=[json.dumps([image_info]), json.dumps([container]), "calldata"]))
                    stack.enter_context(patch("embedded.Rpc", side_effect=lambda role, deadline: nodes[role]))
                    stack.enter_context(patch("embedded.registry", return_value="end"))
                    prefix = stack.enter_context(patch("embedded.verify_prefix", return_value=dict(result)))
                    stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                    if failure == "prefix": prefix.side_effect = AssertionError("actual prefix differs")
                    output = root / "resumption-after-resumption.json"
                    if failure is None:
                        embedded.verify(settings_path)
                        prefix.assert_called_once_with(nodes["reference"], nodes["candidate"], "end", "genesis", 8192)
                        self.assertEqual("container-id", json.loads(output.read_text())["container"])
                    else:
                        expected = {"image": "candidate image", "source": "candidate image source",
                            "container": "running candidate image", "l1-reorg": "canonical L1 recheck",
                            "candidate-fork": "canonical settled endpoint", "prefix": "actual prefix differs"}[failure]
                        with self.assertRaisesRegex(AssertionError, expected): embedded.verify(settings_path)
                        self.assertFalse(output.exists())

    def test_adaptation_cannot_remove_an_upstream_assertion(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "crates/eez-node-e2e/tests/durability.rs"
            path.parent.mkdir(parents=True)
            path.write_text('#[tokio::test]\nasync fn settled_safe_prefix_survives_restart_and_fresh_follower_replay() { assert!(original_condition); }\n')
            manifest = root / "upstream.json"
            manifest.write_text(json.dumps({"profile": "rollup0", "revision": "a" * 40, "core_revision": "a" * 40}))
            args = SimpleNamespace(upstream=root, manifest=manifest, output=root / "inventory.json")
            with patch("embedded.subprocess.check_output", return_value="a" * 40), patch("embedded.adapt", return_value=("rewritten", "assertion removed")), self.assertRaisesRegex(AssertionError, "all original upstream bytes"):
                embedded.prepare(args)
            self.assertIn("assert!(original_condition)", path.read_text())

    def test_missing_candidate_phase_cannot_pass(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inventory = root / "inventory.json"
            inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/resumption.rs",
                "test": "resumed_batch_settles_only_its_new_suffix", "candidate_status": "planned"}]}))
            args = SimpleNamespace(inventory=inventory, directory=root)
            with self.assertRaises(FileNotFoundError): embedded.report(args)
