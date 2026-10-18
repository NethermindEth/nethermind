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
        cls.server = ThreadingHTTPServer(("127.0.0.1", 0), serve.handler(serve.Nodes([("el-1", upstream_url)], None)))
        cls.url = start(cls.server)

    @classmethod
    def tearDownClass(cls):
        cls.server.shutdown()
        cls.upstream.shutdown()

    def post(self, path, body):
        request = urllib.request.Request(self.url + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
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

    def test_lists_configured_nodes(self):
        with urllib.request.urlopen(self.url + "/api/nodes", timeout=5) as response:
            self.assertEqual([n["name"] for n in json.loads(response.read())], ["el-1"])


if __name__ == "__main__":
    unittest.main()
