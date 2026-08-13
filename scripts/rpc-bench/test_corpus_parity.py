#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import contextlib
import csv
import gzip
import io
import json
import os
import sys
import tempfile
import threading
import time
import unittest
import unittest.mock
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_parity  # noqa: E402

SENTINEL = "SENTINEL_PRIVATE_CALLDATA"


class RpcServer:
    """Minimal JSON-RPC test double; responder(id) -> result hex | ('error',) | ('http', status)
    | ('http_json_error', status) | bytes. Answers the node-identity calls itself."""

    def __init__(self, responder, head=25_490_000, chain=1, block_hash=None):
        outer = self
        self.head, self.chain = head, chain
        # Distinct per head by default so a same-height/different-chain-segment case is expressible.
        self.block_hash = block_hash or ("0x" + f"{head:064x}")

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):  # noqa: N802
                request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                if request.get("method") in ("eth_blockNumber", "eth_chainId", "eth_getBlockByNumber"):
                    if request["method"] == "eth_getBlockByNumber":
                        result = {"number": hex(outer.head), "hash": outer.block_hash}
                    else:
                        result = hex(outer.head if request["method"] == "eth_blockNumber" else outer.chain)
                    body = json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)
                    return
                verdict = outer.responder(request["id"])
                if isinstance(verdict, tuple) and verdict[0] == "http":
                    body = b""
                    self.send_response(verdict[1])
                elif isinstance(verdict, tuple) and verdict[0] == "http_json_error":
                    body = json.dumps({"jsonrpc": "2.0", "id": request["id"],
                                       "error": {"code": -32000, "message": SENTINEL}}).encode()
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

    def test_concurrency_only_failures_are_retried_and_not_reported_as_defects(self):
        """A node that fails only while requests overlap must not produce parity defects."""
        corpus = self.write_corpus(30)
        corpus_parity.REPLAY_CONCURRENCY = 1
        self.run_baseline(corpus, lambda i: "0x" + f"{i:04x}")

        state = {"inflight": 0, "flaked": 0}
        guard = threading.Lock()

        def flaky(i):
            with guard:
                state["inflight"] += 1
                overlapped = state["inflight"] > 1
            try:
                time.sleep(0.01)
                if overlapped:
                    with guard:
                        state["flaked"] += 1
                    return ("http", 503)
                return "0x" + f"{i:04x}"
            finally:
                with guard:
                    state["inflight"] -= 1

        corpus_parity.REPLAY_CONCURRENCY = 8
        clean, report, _ = self.run_compare(corpus, flaky)
        self.assertGreater(state["flaked"], 0, "test did not actually induce overlap failures")
        self.assertTrue(clean)
        self.assertEqual(report["matched"], 30)
        self.assertEqual(report["candidate_transport_failures"], 0)

    def test_reproducible_divergence_survives_the_retry(self):
        """The retry must not mask a client that is genuinely wrong."""
        corpus = self.write_corpus(30)
        corpus_parity.REPLAY_CONCURRENCY = 1
        self.run_baseline(corpus, lambda i: "0x" + f"{i:04x}")
        corpus_parity.REPLAY_CONCURRENCY = 8
        wrong = {5, 17}
        clean, report, _ = self.run_compare(
            corpus, lambda i: "0xdeadbeef" if i in wrong else "0x" + f"{i:04x}")
        self.assertFalse(clean)
        self.assertEqual(report["matched"], 28)
        self.assertEqual(sorted(d["index"] for d in report["divergences"]), sorted(wrong))

    def test_same_height_different_block_hash_is_a_fixture_error(self):
        """Height+chain alone cannot tell a reorg or mislabelled snapshot from client divergence."""
        corpus = self.write_corpus(3)
        self.run_baseline(corpus, lambda i: "0x" + f"{i:04x}")
        with RpcServer(lambda i: "0x" + f"{i:04x}", block_hash="0x" + "ab" * 32) as server,                 contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(corpus_parity.CorpusParityError) as caught:
                corpus_parity.compare(str(corpus), server.url, str(self.state), str(self.report),
                                      "base", "cand")
        self.assertIn("identity mismatch", str(caught.exception))

    def test_corrupt_corpus_never_leaks_bytes(self):
        """Truncated gzip and invalid UTF-8 must surface as content-free errors, not tracebacks."""
        truncated = self.dir / "trunc.jsonl.gz"
        full = self.write_corpus(200)
        truncated.write_bytes(full.read_bytes()[:200])
        bad_utf8 = self.dir / "bad.jsonl.gz"
        with gzip.open(bad_utf8, "wb") as handle:
            handle.write(b'{"method":"eth_call","params":["\xff\xfe' + SENTINEL.encode() + b'"]}\n')
        for path in (truncated, bad_utf8):
            with self.assertRaises(corpus_parity.CorpusParityError) as caught:
                corpus_parity.load_corpus(path)
            self.assertNotIn(SENTINEL, str(caught.exception))

    def test_over_cap_divergence_reports_the_full_word_count(self):
        """The preview is capped; the count must not be."""
        words = corpus_parity.MAX_DIFF_WORDS + 4
        described = corpus_parity._describe_divergence(
            1,
            "0x" + "".join(f"{i:064x}" for i in range(words)),
            "0x" + "".join(f"{i + 1:064x}" for i in range(words)))
        self.assertEqual(described["total_differing_words"], words)
        self.assertEqual(len(described["differing_words"]), corpus_parity.MAX_DIFF_WORDS)
        # and no response operands survive into the record
        self.assertTrue(all(set(w) <= {"word", "direction"} for w in described["differing_words"]))

    def test_zero_operand_divergence_cannot_be_reconstructed(self):
        """A zero baseline word must not let the candidate word be recovered from the record.

        A signed magnitude would be exactly the candidate here, which is why only a direction
        is published.
        """
        secret = "de" * 32
        described = corpus_parity._describe_divergence(1, "0x" + "00" * 32, "0x" + secret)
        serialized = json.dumps(described)
        self.assertNotIn(secret, serialized)
        self.assertNotIn(str(int(secret, 16)), serialized)
        self.assertEqual(described["differing_words"], [{"word": 0, "direction": "higher"}])
        # ...and symmetrically, when the candidate is the zero word
        reversed_case = corpus_parity._describe_divergence(1, "0x" + secret, "0x" + "00" * 32)
        self.assertNotIn(str(int(secret, 16)), json.dumps(reversed_case))
        self.assertEqual(reversed_case["differing_words"], [{"word": 0, "direction": "lower"}])

    def test_timings_writes_a_record_by_pass_matrix_without_content(self):
        corpus = self.write_corpus(3)
        out_csv = self.dir / "timings.csv"
        with RpcServer(lambda i: "0x" + "ab" * i) as server, contextlib.redirect_stdout(io.StringIO()) as out:
            corpus_parity.timings(str(corpus), server.url, str(out_csv),
                                  passes=4, rps=0.0, concurrency=4)
        rows = list(csv.reader(out_csv.read_text(encoding="utf-8").splitlines()))
        # header + one row per record; each pass contributes a duration AND an outcome, so a
        # failing run can never be mistaken for a fast one by a reader of the matrix alone.
        self.assertEqual(rows[0], ["record_index",
                                   "pass_1_ms", "pass_1_status", "pass_2_ms", "pass_2_status",
                                   "pass_3_ms", "pass_3_status", "pass_4_ms", "pass_4_status"])
        self.assertEqual(len(rows), 4)
        for position, row in enumerate(rows[1:], start=1):
            self.assertEqual(row[0], str(position))
            self.assertEqual(len(row), 9)
            for pass_index in range(4):
                self.assertGreaterEqual(float(row[1 + pass_index * 2]), 0.0)
                self.assertEqual(row[2 + pass_index * 2], "ok")
        self.assertNotIn(SENTINEL, out_csv.read_text(encoding="utf-8"))
        self.assertNotIn(SENTINEL, out.getvalue())
        self.assertIn("3 records x 4 passes = 12 requests", out.getvalue())

    def test_timings_records_failures_per_record_and_warns(self):
        """A load-shedding node must be visible in the matrix, not just in a stdout aggregate."""
        corpus = self.write_corpus(4)
        out_csv = self.dir / "timings.csv"
        with RpcServer(lambda i: ("error",) if i % 2 == 0 else "0x00") as server,                 contextlib.redirect_stdout(io.StringIO()) as out:
            corpus_parity.timings(str(corpus), server.url, str(out_csv), passes=1, rps=0.0, concurrency=2)
        rows = list(csv.reader(out_csv.read_text(encoding="utf-8").splitlines()))
        statuses = [row[2] for row in rows[1:]]
        self.assertTrue(any(s.startswith("rpc_error") for s in statuses), statuses)
        self.assertIn("ok", statuses)
        self.assertIn("did not return a result", out.getvalue())

    def test_timings_paces_to_the_requested_rate(self):
        corpus = self.write_corpus(5)
        out_csv = self.dir / "timings.csv"
        with RpcServer(lambda i: "0x00") as server, contextlib.redirect_stdout(io.StringIO()) as out:
            started = time.perf_counter()
            corpus_parity.timings(str(corpus), server.url, str(out_csv),
                                  passes=4, rps=40.0, concurrency=8)
            elapsed = time.perf_counter() - started
        # 20 requests at 40 rps cannot finish faster than ~0.475s of pacing
        self.assertGreater(elapsed, 0.4)
        self.assertIn("target 40", out.getvalue())

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

    def test_non_200_jsonrpc_error_body_is_an_rpc_error_not_transport(self):
        corpus = self.write_corpus(1)
        self.run_baseline(corpus, lambda i: "0xab")
        clean, report, stdout = self.run_compare(corpus, lambda i: ("http_json_error", 400))
        self.assertFalse(clean)
        self.assertEqual(report["candidate_rpc_errors"], 1)
        self.assertEqual(report["candidate_transport_failures"], 0)
        self.assertNotIn(SENTINEL, json.dumps(report) + stdout)

    def test_compare_reports_head_mismatch_instead_of_divergence(self):
        corpus = self.write_corpus(1)
        with RpcServer(lambda i: "0xab", head=100) as server:
            corpus_parity.baseline(str(corpus), server.url, str(self.state))
        with RpcServer(lambda i: "0xab", head=101) as server:
            with self.assertRaises(corpus_parity.CorpusParityError) as raised:
                corpus_parity.compare(str(corpus), server.url, str(self.state), str(self.report), "b", "c")
        self.assertIn("identity mismatch", str(raised.exception))
        self.assertIn("head=100", str(raised.exception))
        self.assertFalse(self.report.exists())

    def test_validate_subcommand_checks_loadability_only(self):
        corpus = self.write_corpus(2)
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(corpus_parity.main(["validate", "--corpus", str(corpus)]), 0)
        self.assertIn("2 records", out.getvalue())
        bad = self.write_corpus(lines=["{not json"])
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(corpus_parity.main(["validate", "--corpus", str(bad)]), 2)

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


class EnvironmentCapTests(unittest.TestCase):
    """The caps are read at import, so a malformed override must degrade, not abort the sweep."""

    def test_a_valid_override_is_used(self):
        with unittest.mock.patch.dict(os.environ, {"CAP": "250"}):
            self.assertEqual(corpus_parity._env_int("CAP", 10), 250)

    def test_malformed_and_out_of_range_overrides_fall_back(self):
        for value in ("250k", "", "abc", "1e3", "12.5", "0", "-5", " "):
            with self.subTest(value=value), unittest.mock.patch.dict(os.environ, {"CAP": value}):
                self.assertEqual(corpus_parity._env_int("CAP", 10), 10)

    def test_an_absent_override_uses_the_default(self):
        with unittest.mock.patch.dict(os.environ, {}, clear=True):
            self.assertEqual(corpus_parity._env_int("CAP", 10), 10)


if __name__ == "__main__":
    unittest.main()
