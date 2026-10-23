import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import upstream
import verify


class VerificationTests(unittest.TestCase):
    def test_registry_uses_exact_canonical_snapshot(self):
        class Source:
            def call(self, *arguments):
                self.arguments = arguments
                return "0x" + "00" * 12 + "11" * 20 + "22" * 32 + "00" * 32
        source = Source()
        self.assertEqual(verify.registry(source, "registry", "calldata", {"hash": "snapshot"}), "0x" + "22" * 32)
        self.assertEqual(source.arguments, ("eth_call", {"to": "registry", "data": "calldata"}, {"blockHash": "snapshot", "requireCanonical": True}))

    def test_missing_or_invalid_registration_is_rejected(self):
        class Source:
            def call(self, *arguments):
                return self.value
        for value in ("0x", "0x" + "00" * 96, "0x" + "11" * 96):
            with self.subTest(value=value[:20]):
                source = Source()
                source.value = value
                with self.assertRaises(ValueError):
                    verify.registry(source, "registry", "calldata", {"hash": "snapshot"})

    def test_complete_receipt_and_native_gas_are_required(self):
        base = {field: None for field in verify.RECEIPT}
        base.update({"transactionHash": "tx", "transactionIndex": "0x0", "blockHash": "block", "status": "0x1", "gasUsed": "0x1", "effectiveGasPrice": "0x0", "logs": []})
        class Source:
            def call(self, *arguments):
                return self.value
        reference, candidate = Source(), Source()
        reference.value, candidate.value = base, dict(base)
        transaction = {"hash": "tx", "type": "0x76"}
        verify.check_receipt(reference, candidate, transaction, "block", 0)
        for field, value in (("status", "0x0"), ("gasUsed", "0x0"), ("effectiveGasPrice", "0x1"), ("blockHash", "other")):
            with self.subTest(field=field):
                reference.value = dict(base, **{field: value})
                candidate.value = dict(reference.value)
                with self.assertRaises(AssertionError):
                    verify.check_receipt(reference, candidate, transaction, "block", 0)
        reference.value = base
        candidate.value = dict(base)
        del candidate.value["logsBloom"]
        with self.assertRaises(AssertionError):
            verify.check_receipt(reference, candidate, transaction, "block", 0)

    def test_source_and_candidate_block_bytes_cannot_differ(self):
        genesis = {field: "0x0" for field in verify.HEADER}
        genesis.update({"hash": "genesis", "number": "0x0", "transactions": [], "parentHash": "zero"})
        end = dict(genesis, hash="end", number="0x1", parentHash="genesis", transactions=[{"hash": "tx", "type": "0x0"}])
        class Source:
            def call(self, method, *arguments):
                if method == "eth_getBlockByHash":
                    return {"genesis": genesis, "end": end}[arguments[0]]
                if method == "eth_getRawTransactionByHash":
                    return self.raw
                return "0x0"
        reference, candidate = Source(), Source()
        reference.raw, candidate.raw = "0x01", "0x02"
        with self.assertRaisesRegex(AssertionError, "transaction encoding"):
            verify.verify_prefix(reference, candidate, "end", "genesis", 2)
        candidate.raw = reference.raw
        with patch.object(verify, "check_receipt") as receipt:
            result = verify.verify_prefix(reference, candidate, "end", "genesis", 2)
        self.assertEqual(result["verified_blocks"], 2)
        self.assertEqual(result["verified_receipts"], 1)
        receipt.assert_called_once()


class RevisionTests(unittest.TestCase):
    def test_invalid_revision_cannot_enter_api_paths(self):
        for value in ("main", "../private", "a" * 39, "g" * 40):
            with self.subTest(value=value):
                with self.assertRaises(ValueError):
                    upstream.sha(value)

    def test_resolves_source_and_matching_core_once(self):
        class Args:
            revision = ""
        with tempfile.TemporaryDirectory() as directory:
            args = Args()
            args.output = Path(directory) / "manifest"
            output = Path(directory) / "outputs"
            with patch.dict(upstream.os.environ, {"GITHUB_OUTPUT": str(output)}), patch.object(upstream, "api", side_effect=[{"sha": "a" * 40}, {"tree": [{"path": "eez-core-protocol", "mode": "160000", "sha": "b" * 40}]}]) as api:
                upstream.resolve(args)
            manifest = json.loads(args.output.read_text())
            self.assertEqual(manifest["revision"], "a" * 40)
            self.assertEqual(manifest["core_revision"], "b" * 40)
            self.assertEqual(api.call_count, 2)
            self.assertEqual(output.read_text(), "revision=" + "a" * 40 + "\n")

    def test_replay_outside_main_history_is_rejected(self):
        class Args:
            revision = "b" * 40
        with patch.object(upstream, "api", side_effect=[{"sha": "a" * 40}, {"merge_base_commit": {"sha": "c" * 40}}]):
            with self.assertRaisesRegex(ValueError, "main history"):
                upstream.resolve(Args())

    def test_fork_run_artifact_cannot_be_reused(self):
        class Args:
            pass
        with tempfile.TemporaryDirectory() as directory:
            args = Args()
            args.manifest = Path(directory) / "manifest"
            args.output = Path(directory) / "runtime"
            args.manifest.write_text(json.dumps({"repository": upstream.REPOSITORY, "revision": "a" * 40}))
            forged = {"workflow_runs": [{"head_sha": "a" * 40, "event": "pull_request", "head_branch": "main", "conclusion": "success", "repository": {"full_name": upstream.REPOSITORY}}]}
            with patch.object(upstream, "api", return_value=forged):
                with self.assertRaisesRegex(ValueError, "provenance"):
                    upstream.runtime(args)


if __name__ == "__main__":
    unittest.main()
