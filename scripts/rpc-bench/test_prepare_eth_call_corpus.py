#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import contextlib
import gzip
import importlib.util
import io
import json
import os
import stat
import tempfile
import unittest
from pathlib import Path

SCRIPT_PATH = Path(__file__).with_name("prepare-eth-call-corpus.py")
SPECIFICATION = importlib.util.spec_from_file_location("prepare_eth_call_corpus", SCRIPT_PATH)
CONVERTER = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(CONVERTER)


def call(selector: str, extra: str = "") -> dict:
    return {"method": "eth_call", "params": [{"to": "0x1", "data": selector + extra}, "latest"]}


class PrepareEthCallCorpusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)
        self.destination = self.directory / "fixtures"

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def run_converter(self, source: Path) -> tuple[int, str]:
        standard_error = io.StringIO()
        with contextlib.redirect_stderr(standard_error), contextlib.redirect_stdout(io.StringIO()):
            status = CONVERTER.main([str(source), str(self.destination)])
        return status, standard_error.getvalue()

    def write_jsonl(self, name: str, records) -> Path:
        source = self.directory / name
        text = "".join(json.dumps(r) + "\n" for r in records)
        if name.endswith(".gz"):
            with gzip.open(source, "wt", encoding="utf-8", newline="\n") as output:
                output.write(text)
        else:
            source.write_text(text, encoding="utf-8")
        return source

    def test_splits_by_selector_ranked_by_count(self) -> None:
        source = self.write_jsonl("calls.jsonl.gz", [
            call("0xaaaaaaaa"), call("0xbbbbbbbb", "ff" * 40_000), call("0xaaaaaaaa", "00"),
            {"method": "eth_call", "params": [{"to": "0x1"}]},
        ])
        status, standard_error = self.run_converter(source)
        self.assertEqual(status, 0, standard_error)
        classes = json.loads((self.destination / "classes.json").read_text(encoding="utf-8"))
        self.assertEqual(classes, {"class_1": 2, "class_2": 1, "class_3": 1})
        class_1 = json.loads((self.destination / "class_1.json").read_text(encoding="utf-8"))
        self.assertEqual([r["params"][0]["data"] for r in class_1], ["0xaaaaaaaa", "0xaaaaaaaa00"])
        self.assertEqual(json.loads((self.destination / "class_3.json").read_text(encoding="utf-8")),
                         [{"method": "eth_call", "params": [{"to": "0x1"}]}])
        blob = (self.destination / "class_1.json").read_text(encoding="utf-8")
        self.assertNotIn("0xbbbbbbbb", blob)
        mode = stat.S_IMODE((self.destination / "class_1.json").stat().st_mode)
        self.assertTrue(mode & stat.S_IROTH)
        if os.name == "posix":
            self.assertEqual(mode, 0o644)

    def test_discards_unneeded_fields(self) -> None:
        source = self.write_jsonl("calls.jsonl", [
            {"jsonrpc": "2.0", "id": 1, "method": "eth_call", "params": [{"to": "0x1"}]},
            {"trace": "discarded", "method": "eth_call", "params": [{"data": "0x2"}, "0x10"]},
        ])
        status, standard_error = self.run_converter(source)
        self.assertEqual(status, 0, standard_error)
        self.assertEqual(json.loads((self.destination / "class_1.json").read_text(encoding="utf-8")), [
            {"method": "eth_call", "params": [{"to": "0x1"}]},
            {"method": "eth_call", "params": [{"data": "0x2"}, "0x10"]},
        ])

    def test_rejects_invalid_records_with_source_line_numbers(self) -> None:
        cases = (
            ("malformed JSON", '{"method":', "invalid JSON"),
            ("non-object", '["eth_call", []]', "record must be a JSON object"),
            ("wrong method", '{"method":"eth_getBalance","params":[]}', "method must be exactly 'eth_call'"),
            ("invalid params", '{"method":"eth_call","params":{}}', "params must be a JSON array"),
        )
        for name, invalid_record, expected_message in cases:
            with self.subTest(name=name):
                source = self.directory / f"{name}.jsonl"
                source.write_text(f"\n{invalid_record}\n", encoding="utf-8")
                status, standard_error = self.run_converter(source)
                self.assertEqual(status, 1)
                self.assertIn(f"{source}: line 2:", standard_error)
                self.assertIn(expected_message, standard_error)
                self.assertEqual(list(self.destination.glob("class_*")), [])

    def test_rejects_empty_input(self) -> None:
        source = self.directory / "empty.jsonl"
        source.write_text("\n \t\n", encoding="utf-8")
        status, standard_error = self.run_converter(source)
        self.assertEqual(status, 1)
        self.assertIn(f"{source}: line 0:", standard_error)
        self.assertIn("no nonblank JSON records", standard_error)

    def test_failure_removes_temporary_output(self) -> None:
        source = self.directory / "calls.jsonl"
        source.write_text('{"method":"eth_call","params":[{"to":"0x1"}]}\nnot json\n', encoding="utf-8")
        status, standard_error = self.run_converter(source)
        self.assertEqual(status, 1)
        self.assertIn(f"{source}: line 2:", standard_error)
        self.assertEqual(list(self.destination.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
