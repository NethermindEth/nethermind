# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

import capture_spam
import plot_spam


class CaptureChecks(unittest.TestCase):
    def test_fixed_reports_strip_payloads_and_record_exact_source_hash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            run = root / "spam-stage1-finalized"
            run.mkdir()
            report = {"completed": True, "measurementScope": "recovery only",
                      "rawTransaction": "never-export", "jwt": "never-export",
                      "admissions": [{"admissionUncertain": True, "rawTransaction": "never-export"}],
                      "blocks": [{"blockHash": "0x1234", "transactionCount": 2,
                                  "proof": {"proofBytes": 100, "proofSha256": "public-hash", "proof": "never-export"},
                                  "aggregation": {"signatures": 2, "privateKey": "never-export"},
                                  "managedInspection": {"dependencyHash": "public-hash", "rawTransaction": "never-export"}}]}
            raw = json.dumps(report).encode()
            (run / "report.json").write_bytes(raw)
            (run / "historical-proof-metadata.json").write_text(json.dumps({"capturedUtc": "earlier", "proof": "never-export"}))
            (root / "jwt.hex").write_text("never-export")
            (root / "spam-production-cache-deployment.json").write_text(json.dumps({
                "sourceRevision": "final-source", "nativeUnchanged": True,
                "nodes": [{"memoryPlusSwapLimitBytes": 5, "additionalSwapBytes": 0}],
            }))
            (root / "spam-production-cache-recovery-replay.json").write_text(json.dumps({
                "measurementScope": "recovery only", "attempts": [{"nonce": 7, "submitted": False,
                "receiptsBeforeBoth": [True, True], "rawTransaction": "never-export"}],
            }))
            (root / "spam-production-fix-deployment.json").write_text(json.dumps({
                "sourceRevision": "actual-source", "jwt": "never-export",
                "nodes": [{"hashes": {"libnethermind_lean.so": "actual-binary", "jwt": "never-export"}}],
                "health": {"hostAvailableBytes": 123, "jwt": "never-export"},
            }))
            (root / "spam-beacon-timeout-diagnostic.json").write_text(json.dumps({
                "cpuDiagnostic": {"invalidEarlySample": "discarded", "rawPerf": "never-export",
                                  "simultaneousAllThreadProfile": {"sampleCount": 5, "file": "never-export"}},
            }))
            exported = capture_spam.export(root)
            saved = exported["runs"]["spam-stage1-finalized"]
            self.assertTrue(saved["admissions"][0]["admissionUncertain"])
            cpu = exported["diagnostics"]["spam-beacon-timeout-diagnostic.json"]["data"]["cpuDiagnostic"]
            self.assertEqual(cpu["invalidEarlySample"], "discarded")
            self.assertEqual(cpu["simultaneousAllThreadProfile"]["sampleCount"], 5)
            self.assertEqual(saved["reportSha256"], hashlib.sha256(raw).hexdigest())
            self.assertEqual(saved["blocks"][0]["proof"]["proofBytes"], 100)
            self.assertEqual(saved["proofMetadataSource"]["capturedUtc"], "earlier")
            self.assertNotIn("never-export", json.dumps(exported))
            deployment = exported["resourceEvidence"]["spam-production-fix-deployment.json"]["data"]
            self.assertEqual(deployment["sourceRevision"], "actual-source")
            self.assertEqual(exported["resourceEvidence"]["spam-production-cache-deployment.json"]["data"]["sourceRevision"], "final-source")
            recovery = exported["diagnostics"]["spam-production-cache-recovery-replay.json"]["data"]
            self.assertFalse(recovery["attempts"][0]["submitted"])
            self.assertEqual(deployment["nodes"][0]["hashes"]["libnethermind_lean.so"], "actual-binary")

    def test_recovery_is_not_new_load_and_repeated_blocks_are_deduplicated(self):
        block = {"blockHash": "same", "number": "0x1", "slot": 2, "transactionCount": 1,
                 "aggregation": {"signatures": 1, "genericStarks": 0, "rawDeclarations": 1},
                 "proof": {"proofBytes": 100, "proofSha256": "same"}}
        run = {"startedUtc": "first", "completed": False, "acceptedTransactions": 1,
               "includedTransactions": 1, "goodputIncludingDrainTxPerSecond": 99,
               "admissions": [{"accepted": True}], "blocks": [block]}
        recovery = {**run, "startedUtc": "later", "recoveryFrom": {"sha256": "prior"},
                    "completed": True, "blocks": [{**block, "finalizedBoth": True}]}
        stages, blocks = plot_spam.summarize({"runs": {"load": run, "recovery": recovery}})
        self.assertEqual([item["newAccepted"] for item in stages], [1, 0])
        self.assertTrue(all(item["goodputIncludingDrain"] is None for item in stages))
        self.assertEqual(len(blocks), 1)
        self.assertTrue(blocks[0]["finalizedBoth"])


    def test_busy_negative_probe_is_not_a_valid_transaction_offer(self):
        evidence = {"runs": {"retry": {"admissions": [
            {"accepted": True, "negative": {"outcome": "proofRejected"}},
            {"accepted": False, "negative": {"outcome": "boundedBusy"}},
        ]}, "invalid": {"invalidOnly": True, "admissions": [
            {"validSubmissionOffered": False, "negative": {"outcome": "proofRejected"}},
        ]}}}
        stages, _ = plot_spam.summarize(evidence)
        self.assertEqual([row["newOffers"] for row in stages], [1, 0])
        self.assertEqual([row["probeCycles"] for row in stages], [2, 1])


    def test_healthy_drain_waiting_for_finality_is_not_labelled_failed(self):
        rows, _ = plot_spam.summarize({"runs": {"slow": {
            "inclusionVerified": True, "completed": False, "completedFinality": False,
            "acceptedTransactions": 4, "includedTransactions": 4, "goodputIncludingDrainTxPerSecond": .03,
        }}})
        self.assertEqual(rows[0]["state"], "finality pending")
        self.assertIsNone(rows[0]["goodputIncludingDrain"])


    def test_receipt_goodput_preserves_original_clock_after_later_finality_failure(self):
        admissions = [{"accepted": True, "validSubmissionOffered": True}] * 4
        run = {"uniqueTransactions": 4, "acceptedTransactions": 4, "includedTransactions": 4,
               "inclusionVerified": True, "completed": False, "timedRunSeconds": 130,
               "goodputIncludingDrainTxPerSecond": 4 / 130, "receiptDrainCompletedUtc": "2026-10-03T22:00:00Z",
               "stopReason": "beacon observer timed out", "stopEvents": [{"utc": "2026-10-03T22:05:00Z", "phase": "observation"}]}
        self.assertEqual(plot_spam.receipt_goodput(run, admissions), 4 / 130)
        self.assertIsNone(plot_spam.receipt_goodput({**run, "recoveryFrom": {"sha256": "prior"}}, admissions))
        self.assertIsNone(plot_spam.receipt_goodput({**run, "uniqueTransactions": 8}, admissions))
        self.assertIsNone(plot_spam.receipt_goodput({**run, "stopEvents": [{"utc": "2026-10-03T21:59:59Z"}]}, admissions))
        self.assertIsNone(plot_spam.receipt_goodput(run, [{"accepted": True}] * 4))
        rows, _ = plot_spam.summarize({"runs": {"original": {**run, "admissions": admissions}}})
        self.assertFalse(rows[0]["completed"])
        self.assertEqual(rows[0]["state"], "failed")
        self.assertEqual(rows[0]["receiptGoodput"], 4 / 130)
        self.assertIsNone(rows[0]["goodputIncludingDrain"])


if __name__ == "__main__":
    unittest.main()
