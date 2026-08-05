#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import contextlib
import gzip
import io
import json
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_parity  # noqa: E402

SENTINEL = "SENTINEL_PRIVATE_CALLDATA"


class RpcServer:
    """Minimal JSON-RPC test double; responder(id) -> result hex | ('error',) | ('http', status) | bytes."""

    def __init__(self, responder):
        outer = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                verdict = outer.responder(request["id"])
                if isinstance(verdict, tuple) and verdict[0] == "http":
                    body = b""
                    self.send_response(verdict[1])
                elif isinstance(verdict, tuple) and verdict[0] == "error":
                    body = json.dumps({"jsonrpc": "2.0", "id": request["id"],
                                       "error": {"code": -32000, "message": SENTINEL}}).encode()
                    self.send_response(200)
                elif isinstance(verdict, bytes):
                    body = verdict
                    self.send_response(200)
                else:
                    body = json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": verdict}).encode()
                    self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):  # noqa: A003
                return

        self.responder = responder
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)

    @property
    def url(self):
        return f"http://127.0.0.1:{self.server.server_port}"

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *exc):
        self.server.shutdown()
        self.thread.join()
        self.server.server_close()


class CorpusParityTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)
        self.state = self.dir / "state.json.gz"
        self.report = self.dir / "parity.json"

    def tearDown(self):
        self.tmp.cleanup()

    def write_corpus(self, count=3, gz=True, lines=None):
        path = self.dir / ("corpus.jsonl.gz" if gz else "corpus.jsonl")
        if lines is None:
            lines = [json.dumps({"method": "eth_call", "params": [{"to": "0x1", "data": SENTINEL}, "latest"]})
                     for _ in range(count)]
        text = "\n".join(lines) + "\n"
        if gz:
            with gzip.open(path, "wt", encoding="utf-8") as f:
                f.write(text)
        else:
            path.write_text(text, encoding="utf-8")
        return path

    def run_baseline(self, corpus, responder):
        with RpcServer(responder) as server, contextlib.redirect_stdout(io.StringIO()) as out:
            corpus_parity.baseline(str(corpus), server.url, str(self.state))
        return out.getvalue()

    def run_compare(self, corpus, responder):
        with RpcServer(responder) as server, contextlib.redirect_stdout(io.StringIO()) as out:
            clean = corpus_parity.compare(str(corpus), server.url, str(self.state), str(self.report),
                                          "base_client", "cand_client")
        return clean, json.loads(self.report.read_text(encoding="utf-8")), out.getvalue()

    def test_baseline_then_matching_compare_is_clean_and_content_free(self):
        corpus = self.write_corpus(3)
        stdout = self.run_baseline(corpus, lambda i: "0x" + "ab" * i)
        clean, report, compare_stdout = self.run_compare(corpus, lambda i: "0x" + "ab" * i)
        self.assertTrue(clean)
        self.assertEqual(report["matched"], 3)
        self.assertEqual(report["total"], 3)
        self.assertEqual(report["divergences"], [])
        self.assertEqual(
            set(report),
            set(corpus_parity.PARITY_COUNTER_FIELDS) | set(corpus_parity.PARITY_LABEL_FIELDS) | {"divergences"},
        )
        for text in (stdout, compare_stdout, json.dumps(report)):
            self.assertNotIn(SENTINEL, text)
        # The VM-local state holds response hex only — never request params.
        with gzip.open(self.state, "rt", encoding="utf-8") as f:
            self.assertNotIn(SENTINEL, f.read())

    def test_compare_classifies_defects_without_leaking(self):
        cases = (
            (lambda i: "0xffff", "content_mismatches"),          # same length, different bytes
            (lambda i: "0xababcd", "baseline_shorter"),          # baseline is a strict prefix
            (lambda i: "0x", "candidate_shorter"),               # candidate is a strict prefix
            (lambda i: "0xcccccc", "length_mismatches"),         # different length, no prefix relation
            (lambda i: ("error",), "candidate_rpc_errors"),
            (lambda i: ("http", 503), "candidate_transport_failures"),
            (lambda i: b"not json", "candidate_invalid_responses"),
            (lambda i: json.dumps({"jsonrpc": "2.0", "id": 999, "result": "0xabab"}).encode(),
             "candidate_invalid_responses"),                     # id mismatch
            (lambda i: json.dumps({"jsonrpc": "2.0", "id": i, "result": 7}).encode(),
             "candidate_invalid_responses"),                     # non-string result
            (lambda i: json.dumps({"jsonrpc": "2.0", "id": i, "result": "0xzz"}).encode(),
             "candidate_invalid_responses"),                     # non-hex result
        )
        for responder, field in cases:
            with self.subTest(field=field):
                corpus = self.write_corpus(1)
                self.run_baseline(corpus, lambda i: "0xabab")
                clean, report, stdout = self.run_compare(corpus, responder)
                self.assertFalse(clean)
                self.assertEqual(report[field], 1, report)
                self.assertEqual(report["matched"], 0)
                self.assertEqual(len(report["divergences"]), 1)
                self.assertEqual(report["divergences"][0]["index"], 1)
                self.assertNotIn(SENTINEL, json.dumps(report) + stdout)

    def test_baseline_tolerates_rpc_errors_and_compare_scores_agreement(self):
        # Captured corpora legitimately contain calls that fail at the pinned head; a call
        # both clients reject counts as agreement, a one-sided rejection as divergence.
        corpus = self.write_corpus(3)
        stdout = self.run_baseline(corpus, lambda i: ("error",) if i == 2 else "0xab")
        self.assertIn("1 rpc_error", stdout)
        self.assertNotIn(SENTINEL, stdout)
        with gzip.open(self.state, "rt", encoding="utf-8") as f:
            self.assertNotIn(SENTINEL, f.read())

        clean, report, _ = self.run_compare(corpus, lambda i: ("error",) if i == 2 else "0xab")
        self.assertTrue(clean)
        self.assertEqual((report["matched"], report["both_rpc_errors"]), (2, 1))

        clean, report, _ = self.run_compare(corpus, lambda i: "0xab")
        self.assertFalse(clean)
        self.assertEqual(report["baseline_rpc_errors"], 1)
        self.assertEqual(report["matched"], 2)

    def test_baseline_still_aborts_on_transport_failures_with_counts_only_error(self):
        corpus = self.write_corpus(3)
        with RpcServer(lambda i: ("http", 503) if i == 2 else "0xab") as server:
            with self.assertRaises(corpus_parity.CorpusParityError) as raised:
                corpus_parity.baseline(str(corpus), server.url, str(self.state))
        self.assertIn("transport_failure=1", str(raised.exception))
        self.assertNotIn(SENTINEL, str(raised.exception))
        self.assertFalse(self.state.exists())

    def test_compare_requires_matching_baseline_state(self):
        corpus = self.write_corpus(2)
        self.run_baseline(corpus, lambda i: "0xab")
        bigger = self.write_corpus(3)
        with self.assertRaises(corpus_parity.CorpusParityError):
            self.run_compare(bigger, lambda i: "0xab")
        with self.assertRaises(corpus_parity.CorpusParityError):
            corpus_parity.compare(str(corpus), "http://127.0.0.1:1", str(self.dir / "missing.gz"),
                                  str(self.report), "b", "c")

    def test_load_corpus_accepts_plain_jsonl_and_rejects_bad_records(self):
        plain = self.write_corpus(2, gz=False)
        self.assertEqual(len(corpus_parity.load_corpus(plain)), 2)
        for name, lines in (
            ("bad json", ["{nope"]),
            ("wrong method", [json.dumps({"method": "eth_getBalance", "params": []})]),
            ("no params list", [json.dumps({"method": "eth_call", "params": {}})]),
            ("empty", [""]),
        ):
            with self.subTest(name=name):
                with self.assertRaises(corpus_parity.CorpusParityError):
                    corpus_parity.load_corpus(self.write_corpus(lines=lines))


if __name__ == "__main__":
    unittest.main()
