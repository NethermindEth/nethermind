# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from pathlib import Path
import json
import tempfile
import unittest

from validate_collection import validate


class CollectionValidationTests(unittest.TestCase):
    def test_delivery_errors_and_missing_shutdown_prevent_conversion(self):
        good = "| 1 | 100 | 1.0 |\n| 2 | 100 | 2.0 |\nNethermind is shut down\nevent=\"Cleanup completed\"\n"
        variants = ((good, 0, True), (good, 1, False),
                    (good + "Exception failed\n", 0, False), (good + "Invalid Blocks\n", 0, False),
                    (good + "Fatal\n", 0, False), (good.replace("Nethermind is shut down", ""), 0, False),
                    (good.replace("Cleanup completed", ""), 0, False),
                    (good.replace("| 2 |", "| 1 |"), 0, False))
        with tempfile.TemporaryDirectory() as directory:
            log = Path(directory) / "run.log"
            manifest = Path(directory) / "manifest.json"
            headers = [{"index": i, "hash": "0x" + str(i) * 64} for i in range(3)]
            manifest.write_text(json.dumps({"replay_window": {"warmup": 1, "amount": 2, "headers": headers}}))
            evidence = "".join(f"EXPB_ENGINE_RESULT idx={i} warmup={int(i == 0)} kind={kind} "
                f"status=VALID latest_valid_hash={headers[i]['hash']}\n"
                for i in range(3) for kind in ("newPayload", "forkchoiceUpdated"))
            for text, exit_code, valid in variants:
                with self.subTest(text=text, exit_code=exit_code):
                    log.write_text(text + evidence)
                    report = validate(log, 2, exit_code, manifest)
                    self.assertEqual(report["status"] == "valid", valid)
            broken = ("", evidence + evidence, evidence.replace("warmup=1", "warmup=0"),
                      evidence.replace("idx=2", "idx=3"), evidence.replace("status=VALID", "status=INVALID"),
                      evidence.replace(headers[2]["hash"], "0x" + "f" * 64),
                      "\n".join(evidence.splitlines()[:-1]))
            for text in broken:
                with self.subTest(evidence=text):
                    log.write_text(good + text)
                    self.assertEqual(validate(log, 2, 0, manifest)["status"], "failed")
            log.write_text(good + evidence)
            self.assertEqual(validate(log, 2, 0)["status"], "failed")
            log.write_text(good.replace("| 2 |", "| 9 |") + evidence)
            self.assertEqual(validate(log, 2, 0, manifest)["status"], "failed")


if __name__ == "__main__":
    unittest.main()
