# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Real HTTP response ownership checks; no native proving or live devnet calls."""
import base64
import contextlib
import hmac
import importlib.util
import io
import json
from pathlib import Path
import socket
import struct
import threading
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

        def upstream(request, endpoint, *_):
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


if __name__ == "__main__":
    unittest.main()
