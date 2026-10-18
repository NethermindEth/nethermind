#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Serves the proof wrapper visualiser and proxies a fixed set of read-only JSON-RPC methods to known nodes.

Nodes come from --node NAME=URL and, with --kurtosis ENCLAVE, from the enclave's execution-layer services. The proxy only
forwards to those URLs and only the methods the page needs, so it is not an open relay. Requests must name an allowed Host
(loopback unless --allow-host adds more), which defeats DNS rebinding, and proxied POSTs must come from the page's own origin.
"""
import argparse
import json
import mimetypes
import re
import socket
import subprocess
import threading
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
STATIC = {"index.html", "css", "js", "demo"}
ALLOWED_METHODS = {"debug_proofWrapperEvents", "admin_nodeInfo", "admin_peers", "eth_blockNumber"}
MAX_REQUEST = 64 * 1024
MAX_RESPONSE = 32 * 1024 * 1024
LOOPBACK = {"localhost", "127.0.0.1", "[::1]"}
SERVICE = re.compile(r"^[0-9a-f]{12}\s+(\S+)")
RPC_PORT = re.compile(r"(?<![\w-])rpc:\s*\d+/tcp\s*->\s*(\S+)")


def parse_inspect(output, prefix="el-"):
    """Execution-layer RPC endpoints from `kurtosis enclave inspect` output, as (name, url) pairs."""
    nodes, service = [], ""
    for line in output.splitlines():
        match = SERVICE.match(line)
        if match:
            service = match.group(1)
        rpc = RPC_PORT.search(line)
        if rpc and service.startswith(prefix):
            url = rpc.group(1)
            nodes.append((re.sub(r"-nethermind.*$", "", service), url if url.startswith("http") else f"http://{url}"))
    return nodes


def parse_node(value):
    name, sep, url = value.partition("=")
    if not sep or not name or not re.match(r"^https?://", url):
        raise argparse.ArgumentTypeError(f"expected NAME=http://host:port, got {value!r}")
    return name, url


def static_path(request_path):
    """The file a GET path maps to, or None outside the page's own files."""
    relative = urllib.request.unquote(request_path.split("?", 1)[0]).lstrip("/") or "index.html"
    path = (ROOT / relative).resolve()
    if ROOT not in path.parents or path.relative_to(ROOT).parts[0] not in STATIC or not path.is_file():
        return None
    return path


class Nodes:
    def __init__(self, fixed, enclave, kurtosis="kurtosis"):
        self.fixed, self.enclave, self.kurtosis = dict(fixed), enclave, kurtosis
        self.discovered, self.at, self.lock = {}, 0.0, threading.Lock()

    def all(self):
        """Configured and discovered nodes; one request at a time refreshes discovery while others use the last result."""
        with self.lock:
            refresh = bool(self.enclave) and time.monotonic() - self.at > 10
            if refresh:
                self.at = time.monotonic()
        if refresh:
            try:
                output = subprocess.run([self.kurtosis, "enclave", "inspect", self.enclave], capture_output=True, text=True, timeout=20, check=True).stdout
                discovered = dict(parse_inspect(output))
                with self.lock:
                    self.discovered = discovered
            except (OSError, subprocess.SubprocessError) as error:
                print(f"kurtosis discovery failed: {error}")
        with self.lock:
            return {**self.discovered, **self.fixed}


def host_allowed(host, port, allowed):
    """Whether a Host header names this server: a loopback or explicitly allowed name, with the server's port."""
    if not host:
        return False
    name, sep, given = host.rpartition(":") if not host.endswith("]") else (host, "", "")
    if not sep:
        name, given = host, "80"
    return given == str(port) and name.lower() in LOOPBACK | allowed


def origin_allowed(origin, host):
    """Whether a request's Origin is the page itself; requests without one do not come from a browser page."""
    return origin is None or origin == f"http://{host}"


def handler(nodes, port, allowed_hosts=frozenset()):
    class Handler(BaseHTTPRequestHandler):
        def refused(self):
            """Rejects requests for another host name (DNS rebinding) or, for POST, from another origin (CSRF)."""
            host = self.headers.get("Host")
            if not host_allowed(host, port, allowed_hosts):
                self.send(421, b'{"error":"unexpected Host"}')
                return True
            if self.command == "POST" and not origin_allowed(self.headers.get("Origin"), host):
                self.send(403, b'{"error":"cross-origin request"}')
                return True
            return False

        def send(self, status, body, content_type="application/json"):
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.send_header("X-Content-Type-Options", "nosniff")
            self.send_header("Content-Security-Policy", "default-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' http: https:; img-src 'self' data:; frame-ancestors 'none'")
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self):
            if self.refused():
                return
            if self.path.split("?", 1)[0] == "/api/nodes":
                return self.send(200, json.dumps([{"name": n, "url": u} for n, u in nodes.all().items()]).encode())
            path = static_path(self.path)
            if path is None:
                return self.send(404, b'{"error":"not found"}')
            self.send(200, path.read_bytes(), mimetypes.guess_type(path.name)[0] or "application/octet-stream")

        def do_POST(self):
            if self.refused():
                return
            if (self.headers.get("Content-Type") or "").split(";")[0].strip() != "application/json":
                return self.send(415, b'{"error":"expected application/json"}')
            match = re.fullmatch(r"/rpc/([^/?]+)", self.path)
            url = nodes.all().get(urllib.request.unquote(match.group(1))) if match else None
            if url is None:
                return self.send(404, b'{"error":"unknown node"}')
            length = int(self.headers.get("Content-Length") or 0)
            if length <= 0 or length > MAX_REQUEST:
                return self.send(413, b'{"error":"request size"}')
            try:
                request = json.loads(self.rfile.read(length))
            except ValueError:
                return self.send(400, b'{"error":"invalid JSON"}')
            if not isinstance(request, dict) or request.get("method") not in ALLOWED_METHODS:
                return self.send(403, b'{"error":"method not allowed"}')
            forward = json.dumps({"jsonrpc": "2.0", "id": request.get("id", 1), "method": request["method"], "params": request.get("params", [])}).encode()
            try:
                with urllib.request.urlopen(urllib.request.Request(url, forward, {"Content-Type": "application/json"}), timeout=30) as response:
                    body = response.read(MAX_RESPONSE + 1)
            except (urllib.error.URLError, OSError) as error:
                return self.send(502, json.dumps({"error": f"node unreachable: {error}"}).encode())
            if len(body) > MAX_RESPONSE:
                return self.send(502, b'{"error":"response too large"}')
            self.send(200, body)

        def log_message(self, *_):
            pass

    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bind", default="127.0.0.1", help="listen address; anything but loopback also needs --allow-host")
    parser.add_argument("--allow-host", action="append", default=[], help="extra Host name the page may be reached as, e.g. a LAN name; repeatable")
    parser.add_argument("--port", type=int, default=8437)
    parser.add_argument("--node", action="append", type=parse_node, default=[], help="NAME=URL of a node's JSON-RPC endpoint; repeatable")
    parser.add_argument("--kurtosis", metavar="ENCLAVE", help="discover el-* RPC endpoints with `kurtosis enclave inspect ENCLAVE`")
    args = parser.parse_args()
    if args.bind not in ("127.0.0.1", "localhost", "::1") and not args.allow_host:
        parser.error("--bind to a non-loopback address also needs --allow-host NAME for the name browsers will use")
    nodes = Nodes(args.node, args.kurtosis)
    print(f"Nodes: {nodes.all() or 'none yet; add them in the page'}")
    server_class = ThreadingHTTPServer
    if ":" in args.bind:
        server_class = type("ThreadingHTTPServer6", (ThreadingHTTPServer,), {"address_family": socket.AF_INET6})
    server = server_class((args.bind, args.port), handler(nodes, args.port, frozenset(h.lower() for h in args.allow_host)))
    print(f"Open http://{args.bind}:{args.port}/")
    server.serve_forever()


if __name__ == "__main__":
    main()
