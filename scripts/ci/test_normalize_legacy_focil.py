#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import copy
import json
import tempfile
import unittest
from pathlib import Path

from normalize_legacy_focil import LEGACY_RELEASE, is_blob_transaction, normalize, prepare


BLOB = "0x03ce" + "80" * 8 + "c080c0808080"
ORDINARY = "0x02c0"


def document(entries, included=(), expected=False, **extra):
    return {"fixture": {"engineNewPayloads": [{
        "params": [{"transactions": list(included), "stateRoot": "unchanged"}, [], "0x00", [], entries],
        "newPayloadVersion": "6", "forkchoiceUpdatedVersion": "5",
        "inclusionListSatisfied": expected, **extra,
    }]}}


class LegacyFocilTests(unittest.TestCase):
    def test_changes_only_blob_only_expectation(self):
        fixture = document([BLOB, ORDINARY], included=[ORDINARY])
        expected = copy.deepcopy(fixture)
        expected["fixture"]["engineNewPayloads"][0]["inclusionListSatisfied"] = True
        self.assertEqual(normalize(fixture), 1)
        self.assertEqual(fixture, expected)

    def test_preserves_ordinary_mixed_included_and_invalid_payload_assertions(self):
        cases = [
            document([ORDINARY]), document([BLOB, ORDINARY]), document([BLOB], included=[BLOB]),
            document([]), document([BLOB], expected=True), document([BLOB], expected=None),
            document([BLOB], validationError="INVALID"), document([BLOB], errorCode=-32602),
            document(["0x03"]), document(["0x03c0"]),
        ]
        for fixture in cases:
            with self.subTest(fixture=fixture):
                before = copy.deepcopy(fixture)
                self.assertEqual(normalize(fixture), 0)
                self.assertEqual(fixture, before)

    def test_requires_complete_blob_envelope(self):
        self.assertTrue(is_blob_transaction(BLOB))
        for invalid in (BLOB + "00", BLOB[:-2], "0x03c0", "0x0380", "0x03ff", "0x03zz", ORDINARY):
            with self.subTest(transaction=invalid):
                self.assertFalse(is_blob_transaction(invalid))

    def test_keeps_cached_source_and_unaffected_files_unchanged(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            source = root / "source"
            source.mkdir()
            blob_file = source / "blob.json"
            original = json.dumps(document([BLOB]), indent=2)
            blob_file.write_text(original)
            ordinary_file = source / "ordinary.json"
            ordinary = json.dumps(document([ORDINARY]), indent=2)
            ordinary_file.write_text(ordinary)
            target = root / "prepared"
            self.assertEqual(prepare(LEGACY_RELEASE, source, target), 1)
            self.assertEqual(blob_file.read_text(), original)
            self.assertEqual((target / "ordinary.json").read_text(), ordinary)
            self.assertTrue(json.loads((target / "blob.json").read_text())["fixture"]["engineNewPayloads"][0]["inclusionListSatisfied"])

    def test_rejects_other_releases_without_creating_output(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for release in ("tests-heze-devnet@v2.0.0", "tests-focil-devnet@v0.2.0"):
                with self.subTest(release=release), self.assertRaises(ValueError):
                    prepare(release, root, root / "prepared")
            self.assertFalse((root / "prepared").exists())


if __name__ == "__main__":
    unittest.main()
