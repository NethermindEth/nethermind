import contextlib
import copy
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from types import SimpleNamespace
from unittest.mock import Mock, patch

import embedded
from test_canonical_snapshot import Node


class EmbeddedCoverageTests(unittest.TestCase):
    def test_candidate_helper_only_references_the_selected_upstream_testkit_api(self):
        rollup0, native = [embedded.candidate_helper(profile) for profile in ("rollup0", "rollupx")]
        self.assertIn("world.follower_env()", rollup0)
        self.assertNotIn("world.partial_follower_env()", rollup0)
        self.assertIn("world.partial_follower_env()", native)
        self.assertNotIn("world.follower_env()", native)
        for source in (rollup0, native):
            self.assertEqual(1, source.count("pub async fn start(case:"))
            self.assertIn("pub async fn restart(", source)

    def test_live_native_verification_requires_receipts_above_l1_safe_and_canonical_roots(self):
        transaction = "0x" + "12" * 32
        for failure in (None, "settled-only", "missing-receipt", "bad-receipt", "state-root", "finalized-overclaim", "reorg"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                def l1_call(method, tag, full):
                    if tag in ("latest", "finalized"): return {"number": "0x2", "hash": "canonical"}
                    return {"number": "0x2", "hash": "fork" if failure == "reorg" else "canonical"}
                def call(role, method, *args):
                    if method == "eth_getTransactionReceipt":
                        if role == "candidate" and failure == "missing-receipt": return None
                        return {"transactionHash": transaction, "blockHash": "head", "blockNumber": "0x1", "status": "0x1",
                                "gasUsed": "0x2" if role == "candidate" and failure == "bad-receipt" else "0x1", "logs": []}
                    tag = args[0]
                    if tag in ("safe", "0x0"): return {"number": "0x0", "hash": "genesis"}
                    if tag == "finalized": return {"number": "0x0", "hash": "wrong" if failure == "finalized-overclaim" else "genesis"}
                    return {"number": "0x1", "hash": "head"}
                reference = Mock(call=Mock(side_effect=lambda method, *args: call("reference", method, *args)))
                candidate = Mock(call=Mock(side_effect=lambda method, *args: call("candidate", method, *args)))
                l1 = Mock(call=Mock(side_effect=l1_call))
                settings = {"unsafe_transactions": [transaction], "registry": "registry", "registry_deploy_block": 1, "case": "unsafe-history-reorg", "phase": "before-reorg",
                            "image_id": "image", "candidate_revision": "revision", "database_id": "database", "container": "container"}
                prefix = Mock(return_value={"verified_blocks": 2, "height": 1})
                if failure == "state-root": prefix.side_effect = AssertionError("state root mismatch")
                def commitment(*args): return "head" if failure == "settled-only" else "genesis"
                with patch("embedded.registry", side_effect=commitment), patch("embedded.verify_prefix", prefix), \
                     patch("embedded.time.monotonic", side_effect=[0, 2]), patch("embedded.time.sleep"), contextlib.redirect_stdout(io.StringIO()):
                    if failure is None:
                        embedded.verify_unsafe(settings, root, l1, reference, candidate, "genesis", "call", 1)
                        result = json.loads((root / "unsafe-history-reorg-before-reorg.json").read_text())
                        self.assertEqual(("unsafe", 1, 0), (result["mode"], result["unsafe_height"], result["safe_height"]))
                        prefix.assert_called_once_with(reference, candidate, "head", "genesis", 8192, "rollupx")
                    else:
                        with self.assertRaises((AssertionError, TimeoutError)):
                            embedded.verify_unsafe(settings, root, l1, reference, candidate, "genesis", "call", 1)
                        self.assertFalse(list(root.glob("*.json")))

    def test_native_adaptation_retains_every_original_byte_and_observes_both_reorgs(self):
        first, second = embedded.NATIVE_CASES["queue_reorg"]
        source = "use std::time::Duration;\n" + "\n".join(
            "#[tokio::test]\nasync fn " + name + "() {\n" + body + "\n}"
            for name, body in ((first, '''    let l1 = ProviderBuilder::new().connect_http(world.l1_rpc().parse().unwrap());
    assert!(ancestor.number >= setup_tip);
    assert_eq!(after_chain.state_root, before_chain.state_root);
    assert!(settled.status());'''), (second, '''    let l1 = ProviderBuilder::new().connect_http(world.l1_rpc().parse().unwrap());
    sources.push(send(2).await.unwrap());
    wait_for_safe_prefix_convergence(&[&world.node, &follower], covered, TIMEOUT)
        .await
        .unwrap();''')))
        adapted, restored = embedded.adapt_native(source)
        self.assertEqual(source, restored)
        self.assertEqual(6, adapted.count("candidate.verify_unsafe("))
        self.assertEqual(2, adapted.count("candidate.restart().await.unwrap();"))
        self.assertEqual(2, adapted.count("Candidate::start_native("))
        for marker in ("assert!(ancestor.number >= setup_tip);", "assert_eq!(after_chain.state_root, before_chain.state_root);", "assert!(settled.status());"):
            with self.subTest(marker=marker), self.assertRaises((ValueError, AssertionError)):
                embedded.adapt_native(source.replace(marker, "upstream boundary changed"))

    def test_native_report_rejects_settled_only_execution_and_fake_restart(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        for name, (label, phases) in embedded.NATIVE_CASES["queue_reorg"].items():
            for failure in (None, "settled-only", "missing-receipts", "missing-mode", "source", "image", "missing-phase", "database", "container", "zero-safe", "safe-regressed", "finalized-regressed"):
                if label != "native-source-reorg" and failure in ("zero-safe", "safe-regressed", "finalized-regressed"):
                    continue
                with self.subTest(label=label, failure=failure), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    inventory = root / "inventory.json"
                    inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/queue_reorg.rs",
                        "test": name, "candidate_status": "planned"}]}))
                    for index, phase in enumerate(phases):
                        observation = {"result": "pass", "case": label, "phase": phase, "verified_blocks": 5,
                            "candidate_revision": revision, "image_id": image, "database_id": "same",
                            "container": "restarted" if phase in ("after-restart", "after-settled-restart") or (label == "unsafe-history-reorg" and phase == "after-settlement") else "original", "mode": "unsafe",
                            "unsafe_height": 4, "safe_height": 2 if label == "native-source-reorg" else 0, "finalized_height": 1 if label == "native-source-reorg" else 0, "unsafe_transactions": ["0x" + "12" * 32]}
                        if phase == "after-reorg":
                            if failure == "zero-safe": observation["safe_height"] = 0
                            if failure == "settled-only": observation["safe_height"] = 4
                            if failure == "missing-receipts": observation["unsafe_transactions"] = []
                            if failure == "missing-mode": observation.pop("mode")
                            if failure == "source": observation["candidate_revision"] = "c" * 40
                            if failure == "image": observation["image_id"] = "sha256:" + "c" * 64
                            if failure == "missing-phase": continue
                        if phase in ("after-restart", "after-settled-restart"):
                            if failure == "safe-regressed": observation["safe_height"] = 1
                            if failure == "finalized-regressed": observation["finalized_height"] = 0
                            if failure == "database": observation["database_id"] = "fresh"
                            if failure == "container": observation["container"] = "original"
                        (root / (label + "-" + phase + ".json")).write_text(json.dumps(observation))
                    args = SimpleNamespace(inventory=inventory, directory=root)
                    with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image, "EEZ_SUITE": "embedded-queue_reorg"}):
                        if failure is None:
                            embedded.report(args)
                            self.assertEqual("passed", json.loads((root / "candidate-coverage.json").read_text())["cases"][0]["candidate_status"])
                        else:
                            with self.assertRaises((ValueError, AssertionError, FileNotFoundError)): embedded.report(args)

    def test_native_unsafe_dispatch_records_inspected_container_id_for_restart_evidence(self):
        revision, image, identity = "a" * 40, "sha256:" + "b" * 64, "c" * 64
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            settings = {"result_directory": str(root), "candidate_revision": revision, "image_id": image,
                        "container": "candidate-name", "reference": "reference", "candidate": "candidate", "l1": "l1",
                        "case": "unsafe-history-reorg", "phase": "before-reorg", "rollup_id": "1", "l1_chain_id": "1",
                        "unsafe_transactions": ["0x" + "12" * 32]}
            path = root / "settings.json"
            path.write_text(json.dumps(settings))
            def inspect(command, **kwargs):
                if command[:3] == ["docker", "image", "inspect"]:
                    return json.dumps([{"Id": image, "Config": {"Labels": {"org.opencontainers.image.revision": revision}}}])
                if command[:3] == ["docker", "container", "inspect"]:
                    return json.dumps([{"Image": image, "Id": identity}])
                self.assertEqual("cast", command[0])
                return "0xcall"
            l1 = Mock(call=Mock(return_value="0x1"))
            reference = Mock(call=Mock(side_effect=lambda method, *args: "0x2" if method == "eth_chainId" else {"hash": "genesis"}))
            candidate = Mock(call=Mock(return_value="0x2"))
            with patch.dict(os.environ, {"EEZ_EMBEDDED_RESULTS": str(root), "GITHUB_SHA": revision}), \
                 patch("embedded.subprocess.check_output", side_effect=inspect), patch("embedded.Rpc", side_effect=[l1, reference, candidate]), \
                 patch("embedded.verify_unsafe") as dispatch:
                embedded.verify(path)
            dispatch.assert_called_once()
            self.assertEqual(identity, dispatch.call_args.args[0]["container"])
            self.assertEqual("candidate-name", json.loads(path.read_text())["container"], "observation inputs remain unchanged")

    def test_native_accounting_checks_genesis_and_settled_state_on_both_nodes(self):
        expected = {"system_balance": "0x7", "system_nonce": "0x2", "recipient": "recipient", "recipient_balance": "0x10"}
        values = {("eth_getBalance", embedded.SYSTEM, "0x0"): "0x0",
                  ("eth_getTransactionCount", embedded.SYSTEM, "0x0"): "0x0",
                  ("eth_getBalance", embedded.SYSTEM, "settled"): "0x7",
                  ("eth_getTransactionCount", embedded.SYSTEM, "settled"): "0x2",
                  ("eth_getBalance", "recipient", "settled"): "0x10"}
        for failed_node in (0, 1):
            for changed in (None, *values):
                with self.subTest(failed_node=failed_node, changed=changed):
                    def node(index):
                        def call(method, address, context):
                            if isinstance(context, dict):
                                self.assertEqual({"blockHash": "end", "requireCanonical": True}, context)
                                context = "settled"
                            key = method, address, context
                            return "0xff" if index == failed_node and key == changed else values[key]
                        return Mock(call=Mock(side_effect=call))
                    nodes = node(0), node(1)
                    if changed is None:
                        embedded.check_native_state(*nodes, "end", expected)
                        self.assertEqual(5, nodes[0].call.call_count)
                        self.assertEqual(5, nodes[1].call.call_count)
                    else:
                        with self.assertRaises(AssertionError):
                            embedded.check_native_state(*nodes, "end", expected)

    def test_native_report_requires_new_database_and_identical_recovered_accounting(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        for mutation in (None, "fresh-database", "advanced-database", "fresh-state", "missing-native"):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                inventory = root / "inventory.json"
                inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/native_system.rs",
                    "test": "two_composers_two_keyless_followers", "candidate_status": "planned"}]}))
                phases = embedded.CASES["native_system"]["two_composers_two_keyless_followers"][1]
                for index, phase in enumerate(phases):
                    observation = {"result": "pass", "case": "native-system", "phase": phase,
                        "verified_blocks": 5, "verified_native_transactions": 1 if index == 0 else 2,
                        "candidate_revision": revision, "image_id": image,
                        "database_id": "fresh" if index == 2 else "existing",
                        "native_state": {"system_nonce": "0x1" if index == 0 else "0x2", "recipient_balance": "0x10"}}
                    if mutation == "fresh-database" and index == 2: observation["database_id"] = "existing"
                    if mutation == "advanced-database" and index == 1: observation["database_id"] = "wrong"
                    if mutation == "fresh-state" and index == 2: observation["native_state"]["recipient_balance"] = "0x0"
                    if mutation == "missing-native" and index == 1: observation["verified_native_transactions"] = 0
                    (root / ("native-system-" + phase + ".json")).write_text(json.dumps(observation))
                args = SimpleNamespace(inventory=inventory, directory=root)
                with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image, "EEZ_SUITE": "embedded-native_system"}):
                    if mutation is None:
                        embedded.report(args)
                        self.assertEqual("passed", json.loads((root / "candidate-coverage.json").read_text())["cases"][0]["candidate_status"])
                    else:
                        with self.assertRaises((ValueError, AssertionError)): embedded.report(args)

    def test_unsettled_tail_never_becomes_canonical_or_receipted_on_candidate(self):
        transaction = "0x" + "12" * 32
        genesis = {field: "0x0" for field in embedded.HEADER}
        genesis.update(hash="genesis", transactions=[])
        for phase in ("before-restart", "after-restart"):
            for failure in (None, "state", "latest", "safe", "finalized", "receipt", "reference-receipt", "transaction"):
                with self.subTest(phase=phase, failure=failure):
                    def call(role, method, *args):
                        if method == "eth_getBlockByNumber":
                            block = copy.deepcopy(genesis)
                            if role == "candidate" and args[0] == "0x0" and failure == "state":
                                block["stateRoot"] = "fork"
                            if role == "candidate" and args[0] == failure:
                                block["hash"] = "unsettled"
                            return block
                        self.assertEqual("eth_getTransactionReceipt", method)
                        self.assertEqual((transaction,), args)
                        exists = role == "reference" and phase == "before-restart"
                        if role == "candidate" and failure == "receipt": exists = True
                        if role == "reference" and failure == "reference-receipt": exists = not exists
                        return {"transactionHash": transaction} if exists else None
                    reference = Mock(call=Mock(side_effect=lambda method, *args: call("reference", method, *args)))
                    candidate = Mock(call=Mock(side_effect=lambda method, *args: call("candidate", method, *args)))
                    if failure is None:
                        result = embedded.check_unsettled(reference, candidate, "genesis", transaction, phase)
                        self.assertEqual((0, 1, 0), (result["height"], result["verified_blocks"], result["verified_receipts"]))
                        self.assertEqual(transaction, result["staged_transaction"])
                    else:
                        with self.assertRaises((AssertionError, ValueError)):
                            embedded.check_unsettled(reference, candidate, "genesis", "invalid" if failure == "transaction" else transaction, phase)

    def test_finalized_observation_requires_canonical_nonzero_anchor_on_both_nodes(self):
        for failure in (None, "candidate-lag", "reference-lag", "snapshot-lag", "anchor", "finalized-fork", "zero"):
            with self.subTest(failure=failure):
                def call(role, method, tag, full):
                    self.assertEqual("eth_getBlockByNumber", method)
                    if tag == "safe":
                        return {"hash": "block-3", "number": "0x3"}
                    if tag == "finalized":
                        number = 4 if failure == "snapshot-lag" else 3
                        if failure == role + "-lag": number = 0
                        return {"hash": "fork" if role == "candidate" and failure == "finalized-fork" else "block-" + str(number), "number": hex(number)}
                    number = int(tag, 16)
                    return {"hash": "fork" if role == "candidate" and number == 1 and failure == "anchor" else "block-" + str(number), "number": tag}
                nodes = [Mock(call=Mock(side_effect=lambda method, tag, full, role=role: call(role, method, tag, full)))
                         for role in ("reference", "candidate")]
                if failure in ("anchor", "finalized-fork", "zero"):
                    with self.assertRaises((AssertionError, ValueError)):
                        embedded.check_finalized(*nodes, 0 if failure == "zero" else 1, 3)
                else:
                    result = embedded.check_finalized(*nodes, 1, 3)
                    if failure is None:
                        self.assertEqual({"safe_height": 3, "safe_hash": "block-3", "finalized_height": 3, "finalized_hash": "block-3",
                                          "finalized_anchor_height": 1, "finalized_anchor_hash": "block-1"}, result)
                    else:
                        self.assertIsNone(result)

    def test_restart_finality_regression_fails_before_it_can_recover(self):
        previous = {"safe_height": 5, "finalized_height": 3, "finalized_hash": "old-finalized"}
        for advanced in (False, True):
            with self.subTest(advanced=advanced):
                reference = Mock(call=Mock(return_value={"hash": "reference", "number": "0x3"}))
                candidate = Mock(call=Mock(side_effect=[
                    {"hash": "safe", "number": "0x6" if advanced else "0x5"},
                    {"hash": "genesis", "number": "0x0"},
                    {"hash": "recovered", "number": "0x4"}]))
                if advanced:
                    with self.assertRaisesRegex(AssertionError, "finalized head regressed"):
                        embedded.check_finalized(reference, candidate, 3, 6, previous)
                    self.assertEqual(2, candidate.call.call_count)
                else:
                    self.assertIsNone(embedded.check_finalized(reference, candidate, 3, 6, previous))
                    candidate.call.assert_called_once_with("eth_getBlockByNumber", "safe", False)

    def test_new_durability_cases_require_own_restart_and_exact_state_evidence(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        cases = {name: config for name, config in embedded.CASES["durability"].items() if config[0] != "durability"}
        for name, (label, phases) in cases.items():
            failures = (None, "database", "container", "missing-phase", "no-blocks", "source", "state")
            for failure in failures:
                with self.subTest(label=label, failure=failure), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    inventory = root / "inventory.json"
                    inventory.write_text(json.dumps({"cases": [{"source": "crates/eez-node-e2e/tests/durability.rs",
                        "test": name, "candidate_status": "planned"}]}))
                    for index, phase in enumerate(phases):
                        observation = {"result": "pass", "case": label, "phase": phase,
                            "verified_blocks": 1 if label == "unsettled-restart" else 5,
                            "height": 0 if label == "unsettled-restart" else 4, "verified_receipts": 0,
                            "candidate_revision": revision, "image_id": image, "database_id": "same",
                            "container": "before" if index == 0 else "after",
                            "staged_transaction": "0x" + "12" * 32,
                            "finalized_anchor_height": 2, "finalized_anchor_hash": "0x" + "34" * 32,
                            "finalized_height": 3, "finalized_hash": "0x" + "56" * 32,
                            "safe_height": 3 + index, "safe_hash": "0x" + "78" * 32}
                        if index == 1:
                            if failure == "database": observation["database_id"] = "fresh"
                            if failure == "container": observation["container"] = "before"
                            if failure == "missing-phase": continue
                            if failure == "no-blocks": observation["verified_blocks"] = 0
                            if failure == "source": observation["candidate_revision"] = "c" * 40
                            if failure == "state":
                                if label == "unsettled-restart": observation["height"] = 1
                                else: observation["finalized_height"] = 1
                        (root / (label + "-" + phase + ".json")).write_text(json.dumps(observation))
                    args = SimpleNamespace(inventory=inventory, directory=root)
                    with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image, "EEZ_SUITE": "embedded-durability"}):
                        if failure is None:
                            embedded.report(args)
                            self.assertEqual("passed", json.loads((root / "candidate-coverage.json").read_text())["cases"][0]["candidate_status"])
                        else:
                            with self.assertRaises((ValueError, AssertionError, FileNotFoundError)):
                                embedded.report(args)
                            self.assertFalse((root / "candidate-coverage.json").exists())

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

    def test_partial_consumption_inventory_keeps_network_cases_and_helper_oracles_separate(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "crates/eez-node-e2e/tests/partial_consumption.rs"
            path.parent.mkdir(parents=True)
            path.write_text(''.join('#[tokio::test]\nasync fn ' + name + '() {}\n'
                                    for name in embedded.CASES["partial_consumption"])
                            + '#[test]\nfn settlement_height_oracle_rejects_a_non_sync_height() {}\n')
            cases = embedded.inventory(root)
            network = [case for case in cases if case["asynchronous"]]
            self.assertEqual(set(embedded.CASES["partial_consumption"]), {case["test"] for case in network})
            self.assertTrue(all(case["candidate_status"] == "planned" for case in network))
            self.assertEqual(["required_but_missing"], [case["candidate_status"] for case in cases if not case["asynchronous"]])

    def test_new_suites_require_exact_settlement_evidence_in_every_observation(self):
        revision, image = "a" * 40, "sha256:" + "b" * 64
        for suite in ("contract_post", "partial_consumption"):
            for mutation in (None, "missing-hash", "invalid-hash", "zero-height", "future-height"):
                with self.subTest(suite=suite, mutation=mutation), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    cases = []
                    for name, (label, phases) in embedded.CASES[suite].items():
                        cases.append({"source": "crates/eez-node-e2e/tests/" + suite + ".rs", "test": name, "candidate_status": "planned"})
                        for phase in phases:
                            observation = {"result": "pass", "case": label, "phase": phase, "verified_blocks": 3,
                                "height": 2, "settled_height": 1, "settled_hash": "0x" + "12" * 32,
                                "candidate_revision": revision, "image_id": image}
                            if mutation == "missing-hash": del observation["settled_hash"]
                            if mutation == "invalid-hash": observation["settled_hash"] = "0x12"
                            if mutation == "zero-height": observation["settled_height"] = 0
                            if mutation == "future-height": observation["settled_height"] = 3
                            (root / (label + "-" + phase + ".json")).write_text(json.dumps(observation))
                    inventory = root / "inventory.json"
                    inventory.write_text(json.dumps({"cases": cases}))
                    args = SimpleNamespace(inventory=inventory, directory=root)
                    with patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_CANDIDATE_IMAGE": image, "EEZ_SUITE": "embedded-" + suite}):
                        if mutation is not None:
                            with self.assertRaisesRegex(ValueError, "exact scenario settlement"): embedded.report(args)
                            self.assertFalse((root / "candidate-coverage.json").exists())
                        else:
                            embedded.report(args)
                            self.assertTrue(all(case["candidate_status"] == "passed" for case in json.loads((root / "candidate-coverage.json").read_text())["cases"]))

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
        for case in ("resumption", "native-system", "contract-post", "partial-prefix", "anchor-only", "finalized-restart"):
            failures = (None, "image", "source", "container", "l1-reorg", "candidate-fork", "prefix")
            if case == "native-system": failures += ("native-state", "native-count")
            if case == "finalized-restart": failures += ("finality-lag", "finality-fork", "restart-finality")
            scenario = case in ("contract-post", "partial-prefix", "anchor-only")
            if scenario: failures += ("scenario-fork", "scenario-missing", "scenario-lag")
            for failure in failures:
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
                    if failure == "restart-finality":
                        nodes["reference"].height = nodes["candidate"].height = 2
                    if failure in ("finality-fork", "restart-finality"):
                        original = nodes["candidate"].call
                        def finality_call(method, *args):
                            if method == "eth_getBlockByNumber" and args[0] == "finalized":
                                return {"hash": "genesis" if failure == "restart-finality" else "fork",
                                        "number": "0x0" if failure == "restart-finality" else "0x1"}
                            return original(method, *args)
                        nodes["candidate"].call = finality_call
                    phase = {"native-system": "after-outbound", "contract-post": "after-contract-post",
                             "partial-prefix": "after-partial-settlement", "anchor-only": "after-anchor-settlement",
                             "finalized-restart": "before-restart"}.get(case, "after-resumption")
                    settled_hash = "0x" + "12" * 32
                    if scenario:
                        for role in ("reference", "candidate"):
                            node = nodes[role]
                            node.height = 3
                            original = node.call
                            def call(method, *arguments, role=role, original=original):
                                if method == "eth_getBlockByHash" and arguments[0] == settled_hash:
                                    if failure == "scenario-missing" and role == "reference": return None
                                    return {"hash": settled_hash, "number": "0x2"}
                                if method == "eth_getBlockByHash" and arguments[0] == "behind":
                                    return {"hash": "behind", "number": "0x1"}
                                if method == "eth_getBlockByNumber" and arguments[0] == "0x2":
                                    return {"hash": "fork" if failure == "scenario-fork" and role == "candidate" else settled_hash, "number": "0x2"}
                                return original(method, *arguments)
                            node.call = call
                    settings = {"case": case, "phase": phase, "native_state": {"system_nonce": "0x2"}, "candidate_revision": revision,
                        "image_id": image, "container": "container-name", "database_id": "database",
                        "result_directory": directory, "rollup_id": "1", "l1_chain_id": "1",
                        "registry": "registry", "minimum_height": 1, **{role: role for role in nodes}}
                    if scenario: settings["settled_hash"] = settled_hash
                    if case == "finalized-restart": settings["finalized_minimum"] = 1
                    if failure == "restart-finality":
                        phase = settings["phase"] = "after-restart"
                        before = {"result": "pass", "case": case, "phase": "before-restart",
                            "candidate_revision": revision, "image_id": image, "database_id": "database",
                            "container": "previous-container", "safe_height": 1,
                            "finalized_height": 1, "finalized_hash": "old-finalized"}
                        (root / "finalized-restart-before-restart.json").write_text(json.dumps(before))
                    settings_path = root / "settings.json"
                    settings_path.write_text(json.dumps(settings))
                    result = {"height": 3 if scenario else 1, "endpoint": "end", "verified_blocks": 4 if scenario else 2, "verified_receipts": 1, "verified_native_transactions": 2 if case == "native-system" and failure != "native-count" else 1}
                    with contextlib.ExitStack() as stack:
                        stack.enter_context(patch.dict(os.environ, {"GITHUB_SHA": revision, "EEZ_EMBEDDED_RESULTS": directory}))
                        stack.enter_context(patch("embedded.subprocess.check_output", side_effect=[json.dumps([image_info]), json.dumps([container]), "calldata"]))
                        stack.enter_context(patch("embedded.Rpc", side_effect=lambda role, deadline: nodes[role]))
                        stack.enter_context(patch("embedded.registry", side_effect=["behind", "end"] if failure == "scenario-lag" else None, return_value="end"))
                        sleep = stack.enter_context(patch("embedded.time.sleep"))
                        if failure == "restart-finality":
                            stack.enter_context(patch("embedded.time.monotonic", side_effect=[0, 0, 181]))
                        if failure == "finality-lag":
                            stack.enter_context(patch("embedded.check_finalized", side_effect=[None,
                                {"finalized_height": 1, "finalized_hash": "end", "finalized_anchor_height": 1,
                                 "finalized_anchor_hash": "end"}]))
                        prefix = stack.enter_context(patch("embedded.verify_prefix", return_value=dict(result)))
                        native_state = stack.enter_context(patch("embedded.check_native_state"))
                        if failure == "native-state": native_state.side_effect = AssertionError("native state differs")
                        stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
                        if failure == "prefix": prefix.side_effect = AssertionError("actual prefix differs")
                        output = root / (case + "-" + phase + ".json")
                        if failure in (None, "scenario-lag", "finality-lag"):
                            embedded.verify(settings_path)
                            prefix.assert_called_once_with(nodes["reference"], nodes["candidate"], "end", "genesis", 8192, "rollup0")
                            observation = json.loads(output.read_text())
                            self.assertEqual("container-id", observation["container"])
                            if scenario:
                                self.assertEqual((settled_hash, 2), (observation["settled_hash"], observation["settled_height"]))
                            if failure in ("scenario-lag", "finality-lag"): sleep.assert_called_once_with(1)
                            if case == "finalized-restart": self.assertEqual(1, observation["finalized_height"])
                            if case == "native-system":
                                native_state.assert_called_once_with(nodes["reference"], nodes["candidate"], "end", settings["native_state"])
                            else:
                                native_state.assert_not_called()
                        else:
                            expected = {"image": "candidate image", "source": "candidate image source",
                                "container": "running candidate image", "l1-reorg": "canonical L1 recheck",
                                "candidate-fork": "canonical settled endpoint", "prefix": "actual prefix differs",
                                "native-state": "native state differs", "native-count": "native scenario did not execute", "scenario-fork": "canonical scenario settlement",
                                "scenario-missing": "reference is missing the scenario settlement",
                                "finality-fork": "canonical finalized block",
                                "restart-finality": "candidate finalized head regressed"}[failure]
                            if case == "finalized-restart" and failure == "candidate-fork": expected = "finalized anchor"
                            with self.assertRaisesRegex(ValueError if failure in ("native-count", "scenario-missing") else AssertionError, expected): embedded.verify(settings_path)
                            self.assertFalse(output.exists())

    def test_adaptation_cannot_remove_an_upstream_assertion(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            path = root / "crates/eez-node-e2e/tests/durability.rs"
            path.parent.mkdir(parents=True)
            path.write_text(''.join('#[tokio::test]\nasync fn ' + name + '() { assert!(original_condition); }\n'
                                    for name in embedded.CASES["durability"]))
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
