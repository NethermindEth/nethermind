# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
from pathlib import Path
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
            for text, exit_code, valid in variants:
                with self.subTest(text=text, exit_code=exit_code):
                    log.write_text(text)
                    report = validate(log, 2, exit_code)
                    self.assertEqual(report["status"] == "valid", valid)


if __name__ == "__main__":
    unittest.main()
