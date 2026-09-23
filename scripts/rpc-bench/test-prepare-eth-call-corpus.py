#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import contextlib
import gzip
import importlib.util
import io
import json
import os
from pathlib import Path
import stat
import tempfile
import unittest
import unittest.mock


SCRIPT_PATH = Path(__file__).with_name("prepare-eth-call-corpus.py")
SPECIFICATION = importlib.util.spec_from_file_location("prepare_eth_call_corpus", SCRIPT_PATH)
if SPECIFICATION is None or SPECIFICATION.loader is None:
    raise RuntimeError(f"Unable to load {SCRIPT_PATH}")
CONVERTER = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(CONVERTER)


class PrepareEthCallCorpusTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def run_converter(self, source: Path, destination: Path) -> tuple[int, str]:
        standard_error = io.StringIO()
        with contextlib.redirect_stderr(standard_error):
            status = CONVERTER.main([str(source), str(destination)])
        return status, standard_error.getvalue()

    def test_converts_gzip_jsonl_with_a_record_larger_than_64_kib(self) -> None:
        source = self.directory / "calls.jsonl.gz"
        destination = self.directory / "calls.json"
        request = {
            "jsonrpc": "2.0",
            "id": 7,
            "method": "eth_call",
            "params": [{"data": "0x" + "ab" * 40_000}, "latest"],
        }
        self.assertGreater(len(json.dumps(request)), 64 * 1024)
        with gzip.open(source, "wt", encoding="utf-8", newline="\n") as output:
            output.write(json.dumps(request))
            output.write("\n")

        status, standard_error = self.run_converter(source, destination)

        self.assertEqual(status, 0, standard_error)
        self.assertEqual(
            json.loads(destination.read_text(encoding="utf-8")),
            [{"method": "eth_call", "params": request["params"]}],
        )
        mode = stat.S_IMODE(destination.stat().st_mode)
        self.assertTrue(mode & stat.S_IROTH)
        if os.name == "posix":
            self.assertEqual(mode, 0o644)

    def test_converts_plain_jsonl_and_discards_unneeded_fields(self) -> None:
        source = self.directory / "calls.jsonl"
        destination = self.directory / "calls.json"
        first = {"jsonrpc": "2.0", "id": 1, "method": "eth_call", "params": [{"to": "0x1"}]}
        second = {"trace": "discarded", "method": "eth_call", "params": [{"data": "0x2"}, "0x10"]}
        source.write_text(f"\n{json.dumps(first)}\n\n{json.dumps(second)}\n", encoding="utf-8")

        status, standard_error = self.run_converter(source, destination)

        self.assertEqual(status, 0, standard_error)
        self.assertEqual(
            json.loads(destination.read_text(encoding="utf-8")),
            [
                {"method": "eth_call", "params": first["params"]},
                {"method": "eth_call", "params": second["params"]},
            ],
        )

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
                destination = self.directory / f"{name}.json"
                source.write_text(f"\n{invalid_record}\n", encoding="utf-8")

                status, standard_error = self.run_converter(source, destination)

                self.assertEqual(status, 1)
                self.assertIn(f"{source}: line 2:", standard_error)
                self.assertIn(expected_message, standard_error)
                self.assertFalse(destination.exists())

    def test_rejects_empty_input(self) -> None:
        source = self.directory / "empty.jsonl"
        destination = self.directory / "calls.json"
        source.write_text("\n \t\n", encoding="utf-8")

        status, standard_error = self.run_converter(source, destination)

        self.assertEqual(status, 1)
        self.assertIn(f"{source}: line 0:", standard_error)
        self.assertIn("no nonblank JSON records", standard_error)
        self.assertFalse(destination.exists())

    def test_failure_preserves_existing_destination_and_removes_temporary_output(self) -> None:
        source = self.directory / "calls.jsonl"
        destination = self.directory / "calls.json"
        source.write_text(
            '{"method":"eth_call","params":[{"to":"0x1"}]}\nnot json\n',
            encoding="utf-8",
        )
        original_destination = '["existing corpus"]\n'
        destination.write_text(original_destination, encoding="utf-8")

        status, standard_error = self.run_converter(source, destination)

        self.assertEqual(status, 1)
        self.assertIn(f"{source}: line 2:", standard_error)
        self.assertEqual(destination.read_text(encoding="utf-8"), original_destination)
        self.assertEqual(list(self.directory.glob(f".{destination.name}.*.tmp")), [])


class TraceCallFixtureTests(unittest.TestCase):
    """The k6 fixture must carry the same rewrite the parity replay applies."""

    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def convert(self, records: list[dict], tracer: str = "callTracer") -> tuple[int, str, Path]:
        source = self.directory / "calls.jsonl"
        destination = self.directory / "calls.json"
        source.write_text("\n".join(json.dumps(r) for r in records) + "\n", encoding="utf-8")
        standard_error = io.StringIO()
        environment = {"RPC_BENCH_CORPUS_METHOD": "debug_traceCall", "RPC_BENCH_CORPUS_TRACER": tracer}
        with unittest.mock.patch.dict(os.environ, environment), \
                contextlib.redirect_stderr(standard_error):
            status = CONVERTER.main([str(source), str(destination)])
        return status, standard_error.getvalue(), destination

    def test_each_record_becomes_a_debug_trace_call_with_the_overrides_in_the_options(self) -> None:
        status, standard_error, destination = self.convert([
            {"method": "eth_call", "params": [{"to": "0x1"}, "0x10", {"0xc": {"balance": "0x1"}}]},
            {"method": "eth_call", "params": [{"to": "0x2"}]},
        ])

        self.assertEqual(status, 0, standard_error)
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), [
            {"method": "debug_traceCall", "params": [
                {"to": "0x1"}, "0x10",
                {"tracer": "callTracer", "stateOverrides": {"0xc": {"balance": "0x1"}}},
            ]},
            {"method": "debug_traceCall", "params": [{"to": "0x2"}, "latest", {"tracer": "callTracer"}]},
        ])

    def test_a_record_that_cannot_be_rewritten_names_its_line_and_writes_no_fixture(self) -> None:
        status, standard_error, destination = self.convert([
            {"method": "eth_call", "params": [{"to": "0x1"}]},
            {"method": "eth_call", "params": []},
        ])

        self.assertEqual(status, 1)
        self.assertIn("line 2:", standard_error)
        self.assertFalse(destination.exists())

    def test_an_unknown_tracer_is_rejected_without_writing_a_fixture(self) -> None:
        status, standard_error, destination = self.convert(
            [{"method": "eth_call", "params": [{"to": "0x1"}]}], tracer="gethTracer")

        self.assertEqual(status, 1)
        self.assertIn("unknown tracer", standard_error)
        self.assertFalse(destination.exists())

    def test_the_parity_method_produces_trace_call_records(self) -> None:
        source = self.directory / "calls.jsonl"
        destination = self.directory / "calls.json"
        source.write_text(json.dumps(
            {"method": "eth_call", "params": [{"to": "0x1"}, "0x10", {"0xc": {"balance": "0x1"}}]},
        ) + "\n", encoding="utf-8")
        environment = {"RPC_BENCH_CORPUS_METHOD": "trace_call", "RPC_BENCH_CORPUS_TRACE_TYPES": "trace"}
        with unittest.mock.patch.dict(os.environ, environment), contextlib.redirect_stderr(io.StringIO()):
            status = CONVERTER.main([str(source), str(destination)])

        self.assertEqual(status, 0)
        self.assertEqual(json.loads(destination.read_text(encoding="utf-8")), [
            {"method": "trace_call", "params": [
                {"to": "0x1"}, ["trace"], "0x10", {"0xc": {"balance": "0x1"}},
            ]},
        ])


if __name__ == "__main__":
    unittest.main()
