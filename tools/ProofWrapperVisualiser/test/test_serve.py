# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
# Run with: python3 -m unittest discover -s tools/ProofWrapperVisualiser/test -p 'test_*.py'
import json
import sys
import threading
import unittest
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import serve  # noqa: E402

INSPECT = """\
========================================== User Services ==========================================
UUID           Name                                             Ports                                         Status
889fc66465b6   cl-1-lighthouse-nethermind                       http: 4000/tcp -> http://127.0.0.1:41552       RUNNING
020bdf659e0d   el-1-nethermind-lighthouse                       engine-rpc: 8551/tcp -> 127.0.0.1:41551        RUNNING
                                                                rpc: 8545/tcp -> 127.0.0.1:41445
                                                                ws: 8546/tcp -> 127.0.0.1:41446
1a73d44f21cd   el-2-nethermind-lighthouse                       engine-rpc: 8551/tcp -> 127.0.0.1:41651        RUNNING
                                                                rpc: 8545/tcp -> http://127.0.0.1:41545
"""


class Upstream(BaseHTTPRequestHandler):
    received = []

    def do_POST(self):
        body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
        Upstream.received.append(body)
        reply = json.dumps({"jsonrpc": "2.0", "id": body["id"], "result": {"events": [], "latest": "0x0"}}).encode()
        self.send_response(200)
        self.send_header("Content-Length", str(len(reply)))
        self.end_headers()
        self.wfile.write(reply)

    def log_message(self, *_):
        pass


def start(server):
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return f"http://127.0.0.1:{server.server_address[1]}"


class ServeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.upstream = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        upstream_url = start(cls.upstream)
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), serve.handler(serve.Nodes([("el-1", upstream_url)], None), 0))
        port = cls.server.server_address[1]
        cls.server.RequestHandlerClass = serve.handler(serve.Nodes([("el-1", upstream_url)], None), port)
        cls.url = start(cls.server)
        cls.port = port

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.upstream.shutdown()

    def post(self, path, body, headers=None):
        request = urllib.request.Request(self.url + path, json.dumps(body).encode(), {"Content-Type": "application/json", **(headers or {})})
        try:
            with urllib.request.urlopen(request, timeout=5) as response:
                return response.status, json.loads(response.read())
        except urllib.error.HTTPError as error:
            return error.code, json.loads(error.read())

    def test_parses_execution_rpc_ports_from_enclave_inspect(self):
        self.assertEqual(serve.parse_inspect(INSPECT), [("el-1", "http://127.0.0.1:41445"), ("el-2", "http://127.0.0.1:41545")])

    def test_serves_only_the_page_files(self):
        self.assertEqual(serve.static_path("/").name, "index.html")
        self.assertEqual(serve.static_path("/js/app.js?x=1").name, "app.js")
        for path in ("/serve.py", "/../serve.py", "/js/../serve.py", "/js/../../README.md", "/test/test_serve.py"):
            self.assertIsNone(serve.static_path(path), path)

    def test_proxies_allowed_methods_to_known_nodes(self):
        status, body = self.post("/rpc/el-1", {"jsonrpc": "2.0", "id": 7, "method": "debug_proofWrapperEvents", "params": ["0x0", 10]})
        self.assertEqual(status, 200)
        self.assertEqual(body["result"]["latest"], "0x0")
        self.assertEqual(Upstream.received[-1]["params"], ["0x0", 10])

    def test_refuses_other_methods_and_unknown_nodes(self):
        sent = len(Upstream.received)
        self.assertEqual(self.post("/rpc/el-1", {"method": "eth_sendRawTransaction", "params": ["0x00"]})[0], 403)
        self.assertEqual(self.post("/rpc/el-1", {"method": "admin_addPeer", "params": []})[0], 403)
        self.assertEqual(self.post("/rpc/elsewhere", {"method": "debug_proofWrapperEvents"})[0], 404)
        self.assertEqual(len(Upstream.received), sent, "nothing reaches the node")

    def test_rejects_rebound_hosts_and_cross_origin_posts(self):
        sent = len(Upstream.received)
        call = {"method": "debug_proofWrapperEvents", "params": []}
        self.assertEqual(self.post("/rpc/el-1", call, {"Host": f"attacker.example:{self.port}"})[0], 421)
        self.assertEqual(self.post("/rpc/el-1", call, {"Host": "127.0.0.1:1"})[0], 421)
        self.assertEqual(self.post("/rpc/el-1", call, {"Origin": "http://attacker.example"})[0], 403)
        self.assertEqual(self.post("/rpc/el-1", call, {"Content-Type": "text/plain"})[0], 415)
        self.assertEqual(len(Upstream.received), sent, "nothing reaches the node")
        self.assertEqual(self.post("/rpc/el-1", call, {"Origin": f"http://127.0.0.1:{self.port}"})[0], 200)
        request = urllib.request.Request(self.url + "/api/nodes", headers={"Host": f"rebound.example:{self.port}"})
        with self.assertRaises(urllib.error.HTTPError) as refused:
            urllib.request.urlopen(request, timeout=5)
        self.assertEqual(refused.exception.code, 421)

    def test_host_and_origin_rules(self):
        self.assertTrue(serve.host_allowed("localhost:8437", 8437, frozenset()))
        self.assertTrue(serve.host_allowed("[::1]:8437", 8437, frozenset()))
        self.assertTrue(serve.host_allowed("devbox:8437", 8437, frozenset({"devbox"})))
        self.assertFalse(serve.host_allowed("devbox:8437", 8437, frozenset()))
        self.assertFalse(serve.host_allowed(None, 8437, frozenset()))
        self.assertTrue(serve.origin_allowed(None, "localhost:8437"))
        self.assertFalse(serve.origin_allowed("null", "localhost:8437"))

    def test_lists_configured_nodes(self):
        with urllib.request.urlopen(self.url + "/api/nodes", timeout=5) as response:
            self.assertEqual([n["name"] for n in json.loads(response.read())], ["el-1"])


class TokenTests(unittest.TestCase):
    TOKEN = "s3cret-token-0123456789"

    @classmethod
    def setUpClass(cls):
        cls.upstream = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
        upstream_url = start(cls.upstream)
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), serve.handler(serve.Nodes([], None), 0))
        cls.port = cls.server.server_address[1]
        cls.server.RequestHandlerClass = serve.handler(serve.Nodes([("el-1", upstream_url)], None), cls.port, token=cls.TOKEN)
        cls.url = start(cls.server)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.upstream.shutdown()

    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, *_):
            return None

    def request(self, path, data=None, headers=None):
        opener = urllib.request.build_opener(self.NoRedirect)
        request = urllib.request.Request(self.url + path, data, {"Content-Type": "application/json", **(headers or {})})
        try:
            with opener.open(request, timeout=5) as response:
                return response.status, response.headers, response.read()
        except urllib.error.HTTPError as error:
            return error.code, error.headers, error.read()

    def test_exchanges_a_valid_query_token_for_a_strict_http_only_cookie(self):
        status, headers, body = self.request(f"/js/app.js?token={self.TOKEN}&x=1")
        self.assertEqual(status, 200)
        self.assertIn(b'content="0;url=/js/app.js?x=1"', body, "reloads the same path without the token")
        self.assertNotIn(self.TOKEN.encode(), body)
        self.assertEqual(headers["Referrer-Policy"], "no-referrer")
        self.assertIn(b"url=/evil.example/", self.request(f"//evil.example/?token={self.TOKEN}")[2], "the reload stays on this server")
        cookie = headers["Set-Cookie"]
        self.assertTrue(cookie.startswith(f"pwv_token={self.TOKEN};"))
        self.assertIn("HttpOnly", cookie)
        self.assertIn("SameSite=Strict", cookie)

    def test_accepts_the_cookie_for_pages_and_proxied_calls(self):
        jar = {"Cookie": f"pwv_token={self.TOKEN}"}
        self.assertEqual(self.request("/", headers=jar)[0], 200)
        body = json.dumps({"method": "debug_proofWrapperEvents", "params": []}).encode()
        self.assertEqual(self.request("/rpc/el-1", body, {**jar, "Origin": f"http://127.0.0.1:{self.port}"})[0], 200)

    def test_rejects_missing_or_wrong_tokens(self):
        sent = len(Upstream.received)
        body = json.dumps({"method": "debug_proofWrapperEvents", "params": []}).encode()
        for path, headers in (("/", {}), ("/?token=wrong", {}), ("/api/nodes", {"Cookie": "pwv_token=wrong"}),
                              ("/css/app.css", {"Cookie": "other=1"})):
            self.assertEqual(self.request(path, headers=headers)[0], 401, path)
        self.assertEqual(self.request("/rpc/el-1", body)[0], 401)
        self.assertEqual(self.request(f"/rpc/el-1?token={self.TOKEN}", body)[0], 401, "the query token is only exchanged on GET")
        self.assertEqual(len(Upstream.received), sent, "nothing reaches the node")

    def test_still_checks_host_and_origin_with_a_valid_token(self):
        jar = {"Cookie": f"pwv_token={self.TOKEN}"}
        self.assertEqual(self.request("/", headers={**jar, "Host": f"rebound.example:{self.port}"})[0], 421)
        body = json.dumps({"method": "debug_proofWrapperEvents", "params": []}).encode()
        self.assertEqual(self.request("/rpc/el-1", body, {**jar, "Origin": "http://attacker.example"})[0], 403)

    def test_compares_tokens_exactly(self):
        self.assertTrue(serve.token_matches(self.TOKEN, self.TOKEN))
        self.assertFalse(serve.token_matches(self.TOKEN[:-1], self.TOKEN))
        self.assertFalse(serve.token_matches(None, self.TOKEN))


if __name__ == "__main__":
    unittest.main()
