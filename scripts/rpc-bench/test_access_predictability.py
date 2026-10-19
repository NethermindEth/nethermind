import json
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

import access_predictability as ap
import corpus_results

OWNER = "0x" + "11" * 20
SPENDER = "0x" + "22" * 20
TOKEN = "0x" + "33" * 20


def _address_word(address: str) -> bytes:
    return b"\x00" * 12 + bytes.fromhex(address[2:])


def _call(selector: str, *addresses: str) -> dict:
    data = selector + "".join(_address_word(a).hex() for a in addresses)
    return {"from": OWNER, "to": TOKEN, "data": data}


def _slot(*parts: bytes) -> int:
    return int.from_bytes(ap.KECCAK(b"".join(parts)), "big")


class KeccakTests(unittest.TestCase):
    def test_known_vectors(self):
        self.assertEqual(ap._keccak256(b"").hex(), "c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470")
        self.assertEqual(ap._keccak256(b"abc").hex(), "4e03657aea45a94fc7d47ba826c8d667c0d1e6e33a64a036ec44f58fa12d6c45")
        self.assertEqual(ap._keccak256(b"a" * 200), ap.KECCAK(b"a" * 200))


class DeriveTests(unittest.TestCase):
    def test_balance_mapping_slot_is_found_from_the_argument(self):
        words = ap.call_words(_call("0x70a08231", SPENDER))
        balance = _slot(_address_word(SPENDER), ap._word(3))

        found = ap.derive({balance}, words, ap.HashCache())

        self.assertEqual(found[balance][0], ap.SINGLE)
        self.assertEqual(ap.evaluate(found[balance], words, ap.HashCache()), balance)

    def test_nested_allowance_slot_is_found(self):
        words = ap.call_words(_call("0xdd62ed3e", OWNER, SPENDER))
        allowance = _slot(_address_word(SPENDER), _slot(_address_word(OWNER), ap._word(4)).to_bytes(32, "big"))

        found = ap.derive({allowance}, words, ap.HashCache())

        self.assertEqual(found[allowance][0], ap.NESTED)

    def test_a_struct_field_past_the_mapping_slot_is_found(self):
        words = ap.call_words(_call("0x70a08231", SPENDER))
        field = _slot(_address_word(SPENDER), ap._word(5)) + 2

        found = ap.derive({field}, words, ap.HashCache())

        self.assertEqual(found[field][4], 2)


class PredictorTests(unittest.TestCase):
    def test_a_second_call_with_another_argument_is_predicted_from_the_learned_pattern(self):
        model, totals, keccak = ap.Model(), ap.Totals(), ap.HashCache()
        total_supply = 2

        def prestate(holder: str) -> dict:
            balance = _slot(_address_word(holder), ap._word(0))
            return {TOKEN: {"code": "0x6000", "storage": {hex(balance): "0x1", hex(total_supply): "0x5"}},
                    OWNER: {"balance": "0x0"}}

        ap.score(model, totals, _call("0x70a08231", SPENDER), prestate(SPENDER), keccak)
        third = "0x" + "44" * 20
        ap.score(model, totals, _call("0x70a08231", third), prestate(third), keccak)

        result = ap.report(totals, model, 0, 0.0, keccak)
        self.assertEqual(result["warm_records"], 1)
        self.assertEqual(result["warm"]["recall_slots_static"], 0.5, "totalSupply comes back, the balance slot is new")
        self.assertEqual(result["warm"]["recall_slots_static_and_derived"], 1.0)
        self.assertEqual(result["warm"]["recall_reads"], 1.0)

    def test_report_holds_numbers_only(self):
        result = ap.report(ap.Totals(), ap.Model(), 0, 0.0, ap.HashCache())

        def numbers(value):
            if isinstance(value, dict):
                return all(numbers(v) for v in value.values())
            return isinstance(value, (int, float))

        self.assertTrue(numbers(result))


class TraceTests(unittest.TestCase):
    def _serve(self, reply: dict) -> str:
        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                self.rfile.read(int(self.headers["Content-Length"]))
                body = json.dumps(reply).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        server = HTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}"

    def test_a_node_without_the_debug_module_reports_the_rpc_error_code(self):
        url = self._serve({"jsonrpc": "2.0", "id": 1, "error": {"code": -32601, "message": "not found"}})

        self.assertEqual(ap._trace(url, [{}, "latest", {}]), (None, "rpc_error", -32601))

    def test_a_prestate_object_is_returned(self):
        url = self._serve({"jsonrpc": "2.0", "id": 1, "result": {TOKEN: {"balance": "0x1"}}})

        self.assertEqual(ap._trace(url, [{}, "latest", {}])[0], {TOKEN: {"balance": "0x1"}})


class StagingTests(unittest.TestCase):
    def test_the_report_passes_the_staging_validator(self):
        result = ap.report(ap.Totals(), ap.Model(), 0, 0.0, ap.HashCache())
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "access-predictability.json"
            path.write_text(json.dumps(result))
            corpus_results._validate_access_predictability(path)

    def test_the_staging_validator_accepts_failure_counts(self):
        result = ap.report(ap.Totals(), ap.Model(), 3, 0.0, ap.HashCache())
        result["failures"] = {reason: 1 for reason in ap.FAILURE_REASONS}
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "access-predictability.json"
            path.write_text(json.dumps(result))
            corpus_results._validate_access_predictability(path)

    def test_the_staging_validator_refuses_a_string(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "access-predictability.json"
            path.write_text(json.dumps({"records": 1, "warm": {"reads": "0xabc"}}))
            with self.assertRaises(corpus_results.CorpusResultsError):
                corpus_results._validate_access_predictability(path)


if __name__ == "__main__":
    unittest.main()
