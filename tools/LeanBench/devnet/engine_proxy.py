#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Devnet-only Engine proof pass-through. Does not manufacture proofs or payload verdicts."""
import argparse
import base64
import collections
import hmac
import json
import os
import re
import threading
import tempfile
import time
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

SOCKET_TIMEOUT = 65
MAX_PROOF = 8 * 1024 * 1024
MAX_BODY = 32 * 1024 * 1024
MAX_CACHE_BYTES = 64 * 1024 * 1024
MAX_CACHE_RECORDS = 128
MAX_CAPTURE_BYTES = 64 * 1024 * 1024
MAX_CAPTURE_RECORDS = 16
HASH = re.compile(r"0x[0-9a-fA-F]{64}\Z")
PROOF = "recursiveStarkProof"
DEPS = "recursiveStarkBlockDepsHash"


class ProxyError(Exception):
    pass


def decode_hex(value, limit):
    if not isinstance(value, str) or not value.startswith("0x") or len(value) > 2 + 2 * limit:
        raise ProxyError("Invalid proof field")
    try:
        result = bytes.fromhex(value[2:])
    except ValueError as error:
        raise ProxyError("Invalid proof field") from error
    if not result or len(result) > limit:
        raise ProxyError("Invalid proof field")
    return result


def block_hash(payload):
    value = payload.get("blockHash")
    if not isinstance(value, str) or not HASH.fullmatch(value):
        raise ProxyError("Invalid payload block hash")
    return value.lower()


class ProofCache:
    def __init__(self, directory):
        self.directory = directory
        self.lock = threading.Lock()
        self.records = collections.OrderedDict()
        self.bytes = 0
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        for path in sorted(directory.glob("*.bin"), key=lambda item: item.stat().st_mtime):
            if not re.fullmatch(r"[0-9a-f]{64}\.bin", path.name):
                continue
            with path.open("rb") as source:
                data = source.read(MAX_PROOF + 37)
            if data[:4] != b"NLC1" or not 36 < len(data) <= MAX_PROOF + 36:
                raise ProxyError("Invalid persisted proof cache")
            self._insert("0x" + path.stem, data[36:], data[4:36])

    def _insert(self, key, proof, deps):
        self.records[key] = (proof, deps)
        self.bytes += len(proof) + 96
        while self.bytes > MAX_CACHE_BYTES or len(self.records) > MAX_CACHE_RECORDS:
            removed, (old, _) = self.records.popitem(last=False)
            self.bytes -= len(old) + 96
            (self.directory / (removed[2:] + ".bin")).unlink(missing_ok=True)

    def remember(self, payload):
        key = block_hash(payload)
        proof = decode_hex(payload.get(PROOF), MAX_PROOF)
        deps = decode_hex(payload.get(DEPS), 32)
        if len(deps) != 32:
            raise ProxyError("Invalid dependency commitment")
        with self.lock:
            previous = self.records.get(key)
            if previous is not None:
                if previous != (proof, deps):
                    raise ProxyError("Conflicting proofs for the same block hash")
                self.records.move_to_end(key)
                return
            path = self.directory / (key[2:] + ".bin")
            temporary = path.with_suffix(".tmp")
            with temporary.open("wb") as destination:
                os.chmod(temporary, 0o600)
                destination.write(b"NLC1" + deps + proof)
                destination.flush()
                os.fsync(destination.fileno())
            temporary.replace(path)
            self._insert(key, proof, deps)

    def restore(self, payload):
        key = block_hash(payload)
        supplied = (payload.get(PROOF), payload.get(DEPS))
        if supplied != (None, None):
            if None in supplied:
                raise ProxyError("Incomplete proof fields")
            # The Runner must validate caller-supplied bytes, including invalid proofs.
            decode_hex(supplied[0], MAX_PROOF)
            if len(decode_hex(supplied[1], 32)) != 32:
                raise ProxyError("Invalid dependency commitment")
            return False
        with self.lock:
            record = self.records.get(key)
            if record is None:
                raise ProxyError("Proof cache miss: cannot import this beacon payload without its EL proof")
            self.records.move_to_end(key)
            proof, deps = record
            payload[PROOF], payload[DEPS] = "0x" + proof.hex(), "0x" + deps.hex()
            return True


class PayloadCapture:
    """Bounded private request archive; capture failure never changes the Engine verdict."""
    def __init__(self, directory):
        self.directory = directory
        self.lock = threading.Lock()
        self.records = collections.OrderedDict()
        self.bytes = 0
        directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        if directory.is_symlink():
            raise ProxyError("Capture directory must be owned storage")
        for path in directory.iterdir():
            if path.name.startswith(".capture-") and not path.is_dir():
                path.unlink()
            elif not re.fullmatch(r"[0-9a-f]{64}\.json", path.name):
                raise ProxyError("Capture directory contains foreign entries")
        for path in sorted(directory.glob("*.json"), key=lambda item: (item.stat().st_mtime_ns, item.name)):
            if not re.fullmatch(r"[0-9a-f]{64}\.json", path.name):
                continue
            if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_BODY:
                raise ProxyError("Invalid persisted payload capture")
            with path.open("rb") as source:
                data = source.read(MAX_BODY + 1)
            if len(data) > MAX_BODY:
                raise ProxyError("Oversized persisted payload capture")
            try:
                key = self._key(json.loads(data))
            except (ValueError, TypeError, AttributeError) as error:
                raise ProxyError("Invalid persisted payload capture") from error
            if key is None or key[2:] != path.stem:
                raise ProxyError("Invalid persisted payload capture")
            self.records[key] = len(data)
            self.bytes += len(data)
            self._trim()

    @staticmethod
    def _key(request):
        if not isinstance(request, dict) or request.get("jsonrpc") != "2.0" \
                or request.get("method") != "engine_newPayloadV5":
            raise ProxyError("Invalid capture request")
        params = request.get("params")
        if not isinstance(params, list) or len(params) != 4 or not isinstance(params[0], dict):
            raise ProxyError("Invalid capture parameters")
        proof = decode_hex(params[0].get(PROOF), MAX_PROOF)
        if len(decode_hex(params[0].get(DEPS), 32)) != 32:
            raise ProxyError("Invalid capture dependency commitment")
        return block_hash(params[0]) if len(proof) > 12 else None

    def _trim(self, incoming_bytes=0, incoming_records=0):
        while self.bytes + incoming_bytes > MAX_CAPTURE_BYTES \
                or len(self.records) + incoming_records > MAX_CAPTURE_RECORDS:
            removed, size = self.records.popitem(last=False)
            self.bytes -= size
            (self.directory / (removed[2:] + ".json")).unlink(missing_ok=True)

    def remember(self, request):
        try:
            key = self._key(request)
            if key is None:
                return
            with self.lock:
                if key in self.records:
                    return
                # Only the JSON-RPC envelope is saved, never HTTP headers or credentials.
                envelope = {name: request[name] for name in ("jsonrpc", "id", "method", "params") if name in request}
                data = json.dumps(envelope, separators=(",", ":"), allow_nan=False).encode()
                if len(data) > MAX_BODY or len(data) > MAX_CAPTURE_BYTES:
                    raise ProxyError("Payload capture exceeds size bound")
                self._trim(len(data), 1)
                temporary = None
                try:
                    with tempfile.NamedTemporaryFile(dir=self.directory, prefix=".capture-", delete=False) as destination:
                        temporary = Path(destination.name)
                        destination.write(data)
                        destination.flush()
                        os.fsync(destination.fileno())
                    temporary.replace(self.directory / (key[2:] + ".json"))
                    temporary = None
                    self.records[key] = len(data)
                    self.bytes += len(data)
                    self._trim()
                finally:
                    if temporary is not None:
                        temporary.unlink(missing_ok=True)
        except (OSError, ProxyError, ValueError, TypeError):
            print(json.dumps({"event": "capture_error", "error": "Payload capture unavailable"}), flush=True)


def authenticate(header, secret, now=None):
    if not header.startswith("Bearer "):
        return False
    try:
        parts = header[7:].split(".")
        if len(parts) != 3:
            return False
        decode = lambda value: base64.urlsafe_b64decode(value + "=" * (-len(value) % 4))
        claims = json.loads(decode(parts[1]))
        algorithm = json.loads(decode(parts[0])).get("alg")
        issued = claims.get("iat")
        current = time.time() if now is None else now
        return algorithm == "HS256" and type(issued) is int and abs(current - issued) <= 60 \
            and hmac.compare_digest(decode(parts[2]), hmac.digest(secret, (parts[0] + "." + parts[1]).encode(), "sha256"))
    except (ValueError, KeyError, TypeError, AttributeError):
        return False


def forward(endpoint, request, authorization):
    encoded = json.dumps(request, separators=(",", ":")).encode()
    if len(encoded) > MAX_BODY:
        raise ProxyError("Engine request exceeds proxy size bound")
    message = urllib.request.Request(endpoint, encoded,
        {"Content-Type": "application/json", "Authorization": authorization})
    with urllib.request.urlopen(message, timeout=60) as response:
        data = response.read(MAX_BODY + 1)
    if len(data) > MAX_BODY:
        raise ProxyError("Engine response exceeds proxy size bound")
    return json.loads(data)


def handle(request, endpoint, authorization, cache, send=forward, capture=None):
    if not isinstance(request, dict) or request.get("jsonrpc") != "2.0":
        raise ProxyError("Single JSON-RPC request required")
    method = request.get("method")
    if not isinstance(method, str) or not (method.startswith("engine_") or method in
            ("eth_syncing", "eth_chainId", "eth_getBlockByNumber", "eth_getBlockByHash", "net_version")):
        raise ProxyError("Unsupported proxy method")
    if method == "engine_newPayloadV6":
        raise ProxyError("Bogota newPayloadV6 is unsupported by the Amsterdam proof relay")
    if method == "engine_newPayloadV5":
        params = request.get("params")
        if not isinstance(params, list) or len(params) != 4 or not isinstance(params[0], dict):
            raise ProxyError("Invalid Amsterdam newPayload parameters")
        restored = cache.restore(params[0])
        if restored:
            print(json.dumps({"event": "proof_restored", "blockHash": block_hash(params[0])}), flush=True)
        if capture is not None:
            capture.remember(request)
    result = send(endpoint, request, authorization)
    if not isinstance(result, dict):
        raise ProxyError("Invalid Engine response")
    if method == "engine_getPayloadV6" and "error" not in result:
        envelope = result.get("result")
        if not isinstance(envelope, dict):
            raise ProxyError("Invalid getPayload result")
        payload = envelope.get("executionPayload")
        if not isinstance(payload, dict):
            raise ProxyError("Missing execution payload")
        cache.remember(payload)
        print(json.dumps({"event": "proof_cached", "blockHash": block_hash(payload),
            "proofBytes": (len(payload[PROOF]) - 2) // 2}), flush=True)
    return result


def handler(endpoint, secret, cache, capacity=None, capture=None):
    if capacity is None:
        capacity = threading.BoundedSemaphore(2)
    class Handler(BaseHTTPRequestHandler):
        def do_POST(self):
            self.connection.settimeout(SOCKET_TIMEOUT)
            request_id = None
            leased = False
            try:
                try:
                    authorization = self.headers.get("Authorization", "")
                    if len(authorization) > 4096 or not authenticate(authorization, secret):
                        self.send_error(401)
                        return
                    length = int(self.headers.get("Content-Length", "0"))
                    if not 0 < length <= MAX_BODY or self.headers.get("Transfer-Encoding"):
                        self.send_error(413)
                        return
                    if not capacity.acquire(blocking=False):
                        self.send_error(503, "Proxy at bounded capacity")
                        return
                    leased = True
                    body = self.rfile.read(length)
                    if len(body) != length:
                        raise ProxyError("Incomplete request body")
                    request = json.loads(body)
                    if isinstance(request, dict):
                        request_id = request.get("id")
                    response = handle(request, endpoint, authorization, cache, capture=capture)
                    encoded = json.dumps(response, separators=(",", ":")).encode()
                except (ProxyError, ValueError, OSError, urllib.error.URLError) as error:
                    message = str(error) if isinstance(error, ProxyError) else "Engine proxy request failed"
                    print(json.dumps({"event": "proxy_error", "error": message}), flush=True)
                    encoded = json.dumps({"jsonrpc": "2.0", "id": request_id,
                        "error": {"code": -32000, "message": message}}).encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(encoded)))
                self.end_headers()
                self.wfile.write(encoded)
            except OSError:
                print(json.dumps({"event": "proxy_error", "error": "Engine proxy response failed"}), flush=True)
            finally:
                # Response objects and encoded bytes remain owned until the bounded socket write finishes.
                if leased:
                    capacity.release()

        def log_message(self, *_):
            pass
    return Handler


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--jwt", type=Path, required=True)
    parser.add_argument("--cache", type=Path, required=True)
    parser.add_argument("--capture-dir", type=Path, help="Private bounded newPayloadV5 request archive")
    parser.add_argument("--listen1", type=int, default=19451)
    parser.add_argument("--listen2", type=int, default=19452)
    parser.add_argument("--upstream1", type=int, default=19151)
    parser.add_argument("--upstream2", type=int, default=19251)
    parser.add_argument("--bind", default="127.0.0.1")
    parser.add_argument("--upstream-url1")
    parser.add_argument("--upstream-url2")
    args = parser.parse_args()
    if any(not 1 <= port <= 65535 for port in (args.listen1, args.listen2, args.upstream1, args.upstream2)):
        parser.error("Ports must be in 1..65535")
    secret = bytes.fromhex(args.jwt.read_text().strip().removeprefix("0x"))
    if len(secret) != 32:
        parser.error("Engine JWT must contain exactly 32 bytes")
    cache = ProofCache(args.cache)
    capture = PayloadCapture(args.capture_dir) if args.capture_dir is not None else None

    endpoints = (args.upstream_url1 or f"http://127.0.0.1:{args.upstream1}",
        args.upstream_url2 or f"http://127.0.0.1:{args.upstream2}")
    if any(not endpoint.startswith("http://") for endpoint in endpoints):
        parser.error("Upstream endpoints must use HTTP on the private devnet network")
    servers = [ThreadingHTTPServer((args.bind, listen), handler(endpoint, secret, cache, capture=capture))
        for listen, endpoint in zip((args.listen1, args.listen2), endpoints)]
    threads = [threading.Thread(target=server.serve_forever, daemon=True) for server in servers]
    for thread in threads:
        thread.start()
    try:
        while True:
            time.sleep(1)
    except KeyboardInterrupt:
        pass
    finally:
        for server in servers:
            server.shutdown()
            server.server_close()
        for thread in threads:
            thread.join()


if __name__ == "__main__":
    main()
