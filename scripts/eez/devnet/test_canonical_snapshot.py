import contextlib
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

import verify


class Node:
    def __init__(self, role):
        self.role = role
        self.canonical_hash = "end"
        self.genesis_hash = "genesis"
        self.l1_hash = "snapshot"
        self.height = 1

    def call(self, method, *arguments):
        if method == "eth_chainId":
            return "0x1" if self.role == "l1" else "0x2"
        if method == "eth_getBlockByNumber":
            if self.role == "l1":
                return {"hash": "snapshot" if arguments[0] == "latest" else self.l1_hash, "number": "0x1"}
            if arguments[0] == "0x0":
                return {"hash": self.genesis_hash}
            return {"hash": self.canonical_hash, "number": hex(self.height)}
        if method == "eth_getBlockByHash":
            return {"hash": "end", "number": hex(self.height)}
        if method in ("eth_getBalance", "eth_getTransactionCount"):
            return "0x0"
        raise AssertionError("unexpected method " + method)


class CanonicalSnapshotTests(unittest.TestCase):
    def invoke(self, output, nodes, result, clock=None, extra=()):
        arguments = ["verify", "--l1", "l1", "--reference", "reference", "--candidate", "candidate", "--registry", "registry", "--registry-call", "calldata", "--genesis", "genesis", "--phase", "fixture", "--l1-chain-id", "1", "--l2-chain-id", "2", "--output", str(output), *extra]
        with contextlib.ExitStack() as stack:
            stack.enter_context(patch.object(sys, "argv", arguments))
            stack.enter_context(patch.object(verify, "Rpc", side_effect=lambda url, deadline: nodes[url]))
            stack.enter_context(patch.object(verify, "registry", return_value="end"))
            prefix = stack.enter_context(patch.object(verify, "verify_prefix", return_value=dict(result)))
            stack.enter_context(contextlib.redirect_stdout(io.StringIO()))
            stack.enter_context(patch.object(verify.time, "sleep"))
            if clock is not None:
                stack.enter_context(patch.object(verify.time, "monotonic", side_effect=clock))
            verify.main()
            return prefix

    def test_main_requires_canonical_l1_l2_genesis_and_nonzero_native(self):
        for failure in (None, "l1-reorg", "candidate-fork", "wrong-genesis", "no-native"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as directory:
                nodes = {role: Node(role) for role in ("l1", "reference", "candidate")}
                result = {"height": 1, "endpoint": "end", "verified_blocks": 2, "verified_receipts": 1, "verified_native_transactions": 1}
                if failure == "l1-reorg": nodes["l1"].l1_hash = "fork"
                if failure == "candidate-fork": nodes["candidate"].canonical_hash = "fork"
                if failure == "wrong-genesis": nodes["candidate"].genesis_hash = "other-genesis"
                if failure == "no-native": result["verified_native_transactions"] = 0
                output = Path(directory) / "result.json"
                if failure is not None:
                    with self.assertRaises(AssertionError):
                        self.invoke(output, nodes, result)
                    self.assertFalse(output.exists())
                else:
                    self.invoke(output, nodes, result)
                    record = json.loads(output.read_text())
                    self.assertEqual(record["canonical_l1_hash"], "snapshot")
                    self.assertEqual(record["result"], "pass")

    def test_main_does_not_accept_a_replaced_reference_endpoint(self):
        with tempfile.TemporaryDirectory() as directory:
            nodes = {role: Node(role) for role in ("l1", "reference", "candidate")}
            nodes["reference"].canonical_hash = "fork"
            output = Path(directory) / "result.json"
            result = {"height": 1, "endpoint": "end", "verified_native_transactions": 1}
            with self.assertRaises(TimeoutError):
                self.invoke(output, nodes, result, clock=[0, 0, 601])
            self.assertFalse(output.exists())

    def test_main_does_not_accept_height_before_required_recovery_progress(self):
        with tempfile.TemporaryDirectory() as directory:
            nodes = {role: Node(role) for role in ("l1", "reference", "candidate")}
            output = Path(directory) / "result.json"
            result = {"height": 1, "endpoint": "end", "verified_native_transactions": 1}
            with self.assertRaises(TimeoutError):
                self.invoke(output, nodes, result, clock=[0, 0, 601], extra=["--minimum-height", "2"])
            self.assertFalse(output.exists())


class PrefixNode:
    def __init__(self):
        self.blocks = {}
        for number, name, parent in ((0, "genesis", "zero"), (1, "end", "genesis")):
            block = {field: "0x0" for field in verify.HEADER}
            block.update({"hash": name, "number": hex(number), "parentHash": parent, "transactions": []})
            self.blocks[name] = block
        self.raw = "0x01"
        self.state_queries = []

    def call(self, method, *arguments):
        if method == "eth_getBlockByHash": return self.blocks.get(arguments[0])
        if method == "eth_getRawTransactionByHash": return self.raw
        if method in ("eth_getBalance", "eth_getTransactionCount"):
            self.state_queries.append((method, arguments))
            return "0x0"
        raise AssertionError("unexpected method " + method)


class PrefixRegressionTests(unittest.TestCase):
    def test_account_reads_cannot_observe_a_replaced_height_between_rechecks(self):
        source, candidate = PrefixNode(), PrefixNode()
        verify.verify_prefix(source, candidate, "end", "genesis", 2)
        expected = [(method, (verify.SYSTEM, {"blockHash": "end", "requireCanonical": True}))
                    for method in ("eth_getBalance", "eth_getTransactionCount")]
        self.assertEqual(source.state_queries, expected)
        self.assertEqual(candidate.state_queries, expected)

    def test_candidate_header_roots_and_missing_ancestors_cannot_pass(self):
        for field in ("hash", "number", "parentHash", "stateRoot", "transactionsRoot", "receiptsRoot", "gasUsed", "timestamp", None):
            with self.subTest(field=field):
                source, candidate = PrefixNode(), PrefixNode()
                if field is None:
                    del candidate.blocks["genesis"]
                else:
                    candidate.blocks["end"][field] = "wrong"
                with self.assertRaises(AssertionError):
                    verify.verify_prefix(source, candidate, "end", "genesis", 2)

    def test_wrong_genesis_and_empty_or_oversized_prefix_are_rejected(self):
        for endpoint, genesis, maximum in (("end", "wrong-genesis", 2), ("genesis", "genesis", 2), ("end", "genesis", 0)):
            with self.subTest(endpoint=endpoint, genesis=genesis, maximum=maximum):
                with self.assertRaises(AssertionError):
                    verify.verify_prefix(PrefixNode(), PrefixNode(), endpoint, genesis, maximum)

    def test_missing_raw_transaction_is_not_counted(self):
        source, candidate = PrefixNode(), PrefixNode()
        source.blocks["end"]["transactions"] = [{"hash": "tx", "type": "0x0"}]
        candidate.blocks["end"]["transactions"] = [{"hash": "tx", "type": "0x0"}]
        for raw in (None, "0x"):
            source.raw = raw
            with self.assertRaisesRegex(AssertionError, "encoded transaction"):
                verify.verify_prefix(source, candidate, "end", "genesis", 2)

    def test_candidate_only_receipt_mutations_are_rejected(self):
        source = {field: "0x0" for field in verify.RECEIPT}
        source.update({"blockHash": "block", "transactionHash": "tx", "transactionIndex": "0x0", "logs": []})
        class ReceiptNode:
            def call(self, *arguments): return self.receipt
        reference, candidate = ReceiptNode(), ReceiptNode()
        reference.receipt = source
        for field in ("gasUsed", "status", "logsBloom", "logs", "cumulativeGasUsed", "type", "transactionIndex"):
            candidate.receipt = dict(source, **{field: "wrong"})
            with self.subTest(field=field), self.assertRaises(AssertionError):
                verify.check_receipt(reference, candidate, {"hash": "tx", "type": "0x0"}, "block", 0)
        candidate.receipt = None
        with self.assertRaisesRegex(AssertionError, "missing settled receipt"):
            verify.check_receipt(reference, candidate, {"hash": "tx", "type": "0x0"}, "block", 0)


if __name__ == "__main__":
    unittest.main()
