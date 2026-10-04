# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Real HTTP response ownership checks; no native proving or live devnet calls."""
import base64
import contextlib
import hmac
import importlib.util
import io
import json
import os
from pathlib import Path
import socket
import struct
import threading
import tempfile
import time
import unittest
from unittest.mock import patch
import urllib.error
import urllib.request


SPEC = importlib.util.spec_from_file_location("engine_proxy", Path(__file__).with_name("engine_proxy.py"))
proxy = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(proxy)
SECRET = b"s" * 32


def authorization():
    def encode(value):
        return base64.urlsafe_b64encode(value).rstrip(b"=").decode()
    header = encode(b'{"alg":"HS256"}')
    claims = encode(json.dumps({"iat": int(time.time())}).encode())
    signed = header + "." + claims
    return "Bearer " + signed + "." + encode(hmac.digest(SECRET, signed.encode(), "sha256"))


def wait_for(predicate, timeout=4):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if predicate():
            return True
        time.sleep(.01)
    return False


class ResponseOwnershipChecks(unittest.TestCase):
    @contextlib.contextmanager
    def server(self, capacity, headers_sent, endpoint="http://offline"):
        base = proxy.handler(endpoint, SECRET, None, capacity)

        class Handler(base):
            def setup(self):
                super().setup()
                self.connection.setsockopt(socket.SOL_SOCKET, socket.SO_SNDBUF, 4096)

            def end_headers(self):
                super().end_headers()
                headers_sent()

        server = proxy.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield server.server_address
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=3)
            self.assertFalse(thread.is_alive())

    def send_without_reading(self, address):
        connection = socket.socket()
        connection.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 1024)
        connection.settimeout(3)
        connection.connect(address)
        body = b'{"jsonrpc":"2.0","id":1,"method":"eth_chainId","params":[]}'
        request = ("POST / HTTP/1.0\r\nAuthorization: " + authorization()
                   + "\r\nContent-Length: " + str(len(body)) + "\r\n\r\n").encode() + body
        connection.sendall(request)
        return connection

    def test_slow_readers_hold_capacity_until_write_timeout(self):
        capacity = threading.BoundedSemaphore(2)
        ready = threading.Event()
        lock = threading.Lock()
        header_count = 0

        def headers_sent():
            nonlocal header_count
            with lock:
                header_count += 1
                if header_count == 2:
                    ready.set()

        response = {"jsonrpc": "2.0", "id": 1, "result": "x" * (8 * 1024 * 1024)}
        clients = []
        with contextlib.redirect_stdout(io.StringIO()), patch.object(proxy, "SOCKET_TIMEOUT", 1.5), \
                patch.object(proxy, "handle", return_value=response) as upstream, self.server(capacity, headers_sent) as address:
            try:
                clients = [self.send_without_reading(address) for _ in range(2)]
                self.assertTrue(ready.wait(3), "both large responses must reach the socket write")
                request = urllib.request.Request(f"http://{address[0]}:{address[1]}", b"{}",
                                                 {"Authorization": authorization()})
                with self.assertRaises(urllib.error.HTTPError) as rejected:
                    urllib.request.urlopen(request, timeout=3)
                self.assertEqual(rejected.exception.code, 503)
                rejected.exception.close()
                self.assertEqual(upstream.call_count, 2, "a third response must not be materialized")

                def capacity_restored():
                    if not capacity.acquire(blocking=False):
                        return False
                    if not capacity.acquire(blocking=False):
                        capacity.release()
                        return False
                    capacity.release()
                    capacity.release()
                    return True

                self.assertTrue(wait_for(capacity_restored), "stalled writes must time out and release both leases")
                upstream.return_value = {"jsonrpc": "2.0", "id": 1, "result": "0x1"}
                with urllib.request.urlopen(request, timeout=3) as recovered:
                    self.assertEqual(json.load(recovered)["result"], "0x1")
            finally:
                for connection in clients:
                    connection.close()

    def test_disconnected_reader_releases_response_lease(self):
        capacity = threading.BoundedSemaphore(1)
        ready = threading.Event()
        response = {"jsonrpc": "2.0", "id": 1, "result": "x" * (8 * 1024 * 1024)}
        with contextlib.redirect_stdout(io.StringIO()), patch.object(proxy, "SOCKET_TIMEOUT", 5), \
                patch.object(proxy, "handle", return_value=response), self.server(capacity, ready.set) as address:
            connection = self.send_without_reading(address)
            try:
                self.assertTrue(ready.wait(3))
                acquired = capacity.acquire(blocking=False)
                if acquired:
                    capacity.release()
                self.assertFalse(acquired, "the active response must retain its lease")
                connection.setsockopt(socket.SOL_SOCKET, socket.SO_LINGER, struct.pack("ii", 1, 0))
                connection.close()

                def capacity_restored():
                    if not capacity.acquire(blocking=False):
                        return False
                    capacity.release()
                    return True

                self.assertTrue(wait_for(capacity_restored, timeout=2), "failed writes must release before the socket deadline")
            finally:
                connection.close()

    def test_saturated_listener_does_not_block_other_execution_node(self):
        ready = threading.Event()
        lock = threading.Lock()
        header_count = 0

        def headers_sent():
            nonlocal header_count
            with lock:
                header_count += 1
                if header_count == 2:
                    ready.set()

        large_response = {"jsonrpc": "2.0", "id": 1, "result": "x" * (8 * 1024 * 1024)}

        def upstream(request, endpoint, *_, **__):
            return large_response if endpoint == "http://slow" else {"jsonrpc": "2.0", "id": 1, "result": "0x1"}

        clients = []
        with contextlib.redirect_stdout(io.StringIO()), patch.object(proxy, "SOCKET_TIMEOUT", 1.5), \
                patch.object(proxy, "handle", side_effect=upstream), \
                self.server(None, headers_sent, "http://slow") as slow, \
                self.server(None, lambda: None, "http://other") as other:
            try:
                clients = [self.send_without_reading(slow) for _ in range(2)]
                self.assertTrue(ready.wait(3))
                request = urllib.request.Request(f"http://{other[0]}:{other[1]}", b"{}",
                                                 {"Authorization": authorization()})
                with urllib.request.urlopen(request, timeout=3) as response:
                    self.assertEqual(json.load(response)["result"], "0x1")
            finally:
                for connection in clients:
                    connection.close()

    def test_non_dictionary_get_payload_result_is_a_proxy_error(self):
        request = {"jsonrpc": "2.0", "id": 1, "method": "engine_getPayloadV6", "params": []}
        for result in (None, [], "malformed", 1):
            with self.subTest(result=result), self.assertRaisesRegex(proxy.ProxyError, "Invalid getPayload result"):
                proxy.handle(request, "http://offline", "", None,
                             send=lambda *_: {"jsonrpc": "2.0", "id": 1, "result": result})

    def test_bogota_import_is_rejected_before_forwarding(self):
        request = {"jsonrpc": "2.0", "id": 1, "method": "engine_newPayloadV6", "params": []}

        def unexpected_forward(*_):
            self.fail("the Amsterdam relay must not silently forward an unsupported proof-bearing import")

        with self.assertRaisesRegex(proxy.ProxyError, "Bogota newPayloadV6 is unsupported"):
            proxy.handle(request, "http://offline", "", None, send=unexpected_forward)


class PayloadCaptureChecks(unittest.TestCase):
    @staticmethod
    def request(index=1, proof_bytes=13):
        return {"jsonrpc": "2.0", "id": index, "method": "engine_newPayloadV5", "params": [
            {"blockHash": "0x" + f"{index:064x}", proxy.PROOF: "0x" + "ab" * proof_bytes,
             proxy.DEPS: "0x" + "cd" * 32, "transactions": ["0x7f01"], "blockAccessList": "0x1234"},
            ["0x" + "ef" * 32], "0x" + "12" * 32, ["0x020304"]]}

    def test_restored_complete_request_is_captured_without_credentials_or_verdict_changes(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            cache = proxy.ProofCache(Path(directory) / "proofs")
            capture = proxy.PayloadCapture(Path(directory) / "captures")
            original = self.request()
            cache.remember(original["params"][0])
            request = json.loads(json.dumps(original))
            del request["params"][0][proxy.PROOF]
            del request["params"][0][proxy.DEPS]
            secret = authorization()
            request["Authorization"] = secret
            verdict = {"jsonrpc": "2.0", "id": 1, "result": {"status": "INVALID"}}

            def forward(_, actual, header):
                self.assertEqual(header, secret)
                self.assertEqual(actual["params"], original["params"])
                self.assertEqual(json.loads(next(capture.directory.glob("*.json")).read_text()), original)
                return verdict

            self.assertIs(proxy.handle(request, "http://offline", secret, cache,
                                       send=forward, capture=capture), verdict)
            path = next(capture.directory.glob("*.json"))
            self.assertNotIn(secret, path.read_text())
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)
            request["params"][3] = ["0xff"]
            capture.remember(request)
            self.assertEqual(json.loads(path.read_text()), original, "the first record for a block must be immutable")

    def test_empty_proofs_do_not_evict_and_shared_capture_serializes_duplicate_requests(self):
        with tempfile.TemporaryDirectory() as directory:
            capture = proxy.PayloadCapture(Path(directory))
            with patch.object(proxy, "MAX_CAPTURE_RECORDS", 1):
                threads = [threading.Thread(target=capture.remember, args=(self.request(),)) for _ in range(8)]
                for thread in threads:
                    thread.start()
                for thread in threads:
                    thread.join(timeout=2)
                    self.assertFalse(thread.is_alive())
                capture.remember(self.request(2, proof_bytes=12))
                self.assertEqual(len(capture.records), 1)
                self.assertEqual(len(list(capture.directory.glob("*.json"))), 1)
                self.assertIn("0x" + f"{1:064x}", capture.records)

    def test_count_byte_and_restart_bounds_remove_oldest_records(self):
        for tied in (False, True):
            with self.subTest(tied=tied), tempfile.TemporaryDirectory() as directory:
                path = Path(directory)
                capture = proxy.PayloadCapture(path)
                for index in (2, 4, 1, 3):
                    capture.remember(self.request(index))
                    stamp = 1_700_000_000 + (0 if tied else index)
                    os.utime(path / (f"{index:064x}" + ".json"), (stamp, stamp))
                with patch.object(proxy, "MAX_CAPTURE_RECORDS", 3), \
                        patch.object(proxy, "MAX_CAPTURE_BYTES", capture.bytes // 2):
                    restarted = proxy.PayloadCapture(path)
                    self.assertLessEqual(restarted.bytes, proxy.MAX_CAPTURE_BYTES)
                    self.assertLessEqual(len(restarted.records), proxy.MAX_CAPTURE_RECORDS)
                    self.assertEqual(list(restarted.records), ["0x" + f"{index:064x}" for index in (3, 4)])
                    restarted.remember(self.request(5))
                    self.assertEqual(list(restarted.records), ["0x" + f"{index:064x}" for index in (4, 5)])
                    self.assertEqual(sum(item.stat().st_size for item in path.glob("*.json")), restarted.bytes)

    def test_oversize_or_failed_capture_preserves_forwarding(self):
        with tempfile.TemporaryDirectory() as directory, contextlib.redirect_stdout(io.StringIO()):
            capture = proxy.PayloadCapture(Path(directory))
            request = self.request()
            with patch.object(proxy, "MAX_BODY", 64):
                capture.remember(request)
                self.assertEqual(list(capture.directory.iterdir()), [])
            verdict = {"jsonrpc": "2.0", "id": 1, "result": {"status": "VALID"}}
            with patch.object(proxy.tempfile, "NamedTemporaryFile", side_effect=OSError("private disk path")):
                self.assertIs(proxy.handle(request, "http://offline", "", proxy.ProofCache(Path(directory) / "proofs"),
                                           send=lambda *_: verdict, capture=capture), verdict)
            self.assertEqual(capture.records, {})

    def test_startup_rejects_oversized_or_foreign_owned_record(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / (f"{1:064x}" + ".json")
            path.write_text(json.dumps(self.request(2)))
            with self.assertRaisesRegex(proxy.ProxyError, "Invalid persisted payload capture"):
                proxy.PayloadCapture(Path(directory))
            path.write_text("x" * 65)
            with patch.object(proxy, "MAX_BODY", 64), \
                    self.assertRaisesRegex(proxy.ProxyError, "Invalid persisted payload capture"):
                proxy.PayloadCapture(Path(directory))


if __name__ == "__main__":
    unittest.main()
