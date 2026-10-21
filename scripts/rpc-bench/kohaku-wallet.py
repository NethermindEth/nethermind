#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Capture Kohaku Sepolia dry-run RPCs, then replay the same ordered requests.

Wallet copies and raw recordings belong in ignored repository-local data directories.
The original CLI installation and wallet are only read. Passwords are loaded from
the local runbook in memory and are never included in command metadata or logs.
Run summaries preserve wallet exit codes and report completed RPC outcomes,
failed/pending request counts, and an ok/degraded/failed status. Raw JSONL rows
remain authoritative when requests are still pending at the summary snapshot.
"""

from __future__ import annotations

import argparse
import contextlib
import datetime
import http.client
import http.server
import json
import os
import re
import shutil
import socket
import subprocess
import tempfile
import threading
import time
import urllib.parse
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TOOLS = Path("C:/base/projects/blank/fv-lc-reseach/tools")
USER_AGENT = "Mozilla/5.0"
READ_METHODS = frozenset({
    "eth_chainId", "eth_blockNumber", "eth_call", "eth_estimateGas",
    "eth_gasPrice", "eth_maxPriorityFeePerGas", "eth_feeHistory",
    "eth_getBalance", "eth_getTransactionCount", "eth_getCode", "eth_getStorageAt",
    "eth_getProof", "eth_getLogs", "eth_getBlockByNumber", "eth_getBlockByHash",
    "eth_getTransactionByHash", "eth_getTransactionByBlockNumberAndIndex",
    "eth_getTransactionReceipt", "eth_getBlockReceipts", "eth_createAccessList",
    "eth_syncing", "net_version", "web3_clientVersion",
})


def timestamp():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def secrets_from_runbook(path):
    contents = path.read_text(encoding="utf-8")
    password = re.search(r"\| Wallet password \| `([^`]+)`", contents)
    if not password:
        raise ValueError("Runbook has no wallet-password table entry")
    secrets = [match.group(1) for match in re.finditer(
        r"\| [^|]*(?:password|Seed phrase|private key)[^|]* \| `([^`]+)`", contents)]
    return password.group(1), secrets


def redact(text, secrets):
    for secret in secrets:
        text = text.replace(secret, "<redacted>")
    return text


def output_dir(raw):
    path = Path(raw).resolve()
    if not any(path.is_relative_to(ROOT / name) for name in ("artifacts", "data")):
        raise ValueError("Output directory must be under repository artifacts/ or data/")
    ignored = subprocess.run(["git", "check-ignore", "--quiet", "--", str(path.relative_to(ROOT) / ".wallet-output")],
                             cwd=ROOT, capture_output=True)
    if ignored.returncode:
        raise ValueError("Output directory must be gitignored before wallet data is copied")
    path.mkdir(parents=True, exist_ok=True)
    return path


def copy_wallet(source, destination, secrets):
    encrypted_fields = {"v", "salt", "iv", "tag", "ciphertext"}
    names = (".encrypted-seed.json", "public-accounts.json", "stealth-accounts.json",
             "rg-storage.json", "ppv1-storage.json", "tc-storage.json")
    if (source / ".wallet-type").read_text(encoding="utf-8").strip() != "testnet":
        raise ValueError("Only a testnet wallet can be copied")
    for name in names:
        path = source / name
        if not path.exists():
            continue
        raw = path.read_text(encoding="utf-8")
        value = json.loads(raw)
        if name == ".encrypted-seed.json":
            if set(value) != {"kind", "version", "crypto"}:
                raise ValueError("Unrecognized encrypted seed structure")
            value = value["crypto"]
        if not isinstance(value, dict) or set(value) != encrypted_fields or any(secret in raw for secret in secrets):
            raise ValueError(f"Refusing to copy unrecognized or plaintext wallet data: {name}")
    destination.mkdir(parents=True)
    for name in (*names, ".wallet-type", ".stealth-start-block"):
        if (source / name).exists():
            if name == ".stealth-start-block":
                raw = (source / name).read_text(encoding="utf-8").strip()
                if not re.fullmatch(r"(?:0x[0-9a-fA-F]+|[0-9]+)", raw):
                    raise ValueError("Unrecognized stealth start block")
            shutil.copyfile(source / name, destination / name)


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def pin_block(request, block):
    positions = {"eth_call": 1, "eth_estimateGas": 1, "eth_getBalance": 1,
                 "eth_getTransactionCount": 1, "eth_getCode": 1,
                 "eth_getStorageAt": 2, "eth_getProof": 2}
    request = json.loads(json.dumps(request))
    rewrites = []
    calls = request if isinstance(request, list) else [request]
    for call_index, call in enumerate(calls):
        position = positions.get(call.get("method"))
        params = call.get("params", [])
        if position is None or len(params) < position:
            continue
        previous = params[position] if len(params) > position else None
        if previous not in (None, "latest"):
            continue
        if len(params) == position:
            params.append(block)
        else:
            params[position] = block
        rewrites.append({"call": call_index, "parameter": position, "from": previous, "to": block})
    return request, rewrites


def response_error(payload, response, omitted=False):
    # JSON-RPC 2.0 sections 4–6: https://www.jsonrpc.org/specification
    calls = payload if isinstance(payload, list) else [payload]
    expected = [call["id"] for call in calls if "id" in call]
    if not expected:
        return None if omitted else "Upstream replied to a notification"
    if isinstance(payload, list) != isinstance(response, list):
        return "Upstream response does not match singleton/batch request shape"
    responses = response if isinstance(response, list) else [response]
    if not responses or any(not isinstance(item, dict) or item.get("jsonrpc") != "2.0" or
            "id" not in item or ("result" in item) == ("error" in item) for item in responses):
        return "Upstream returned an invalid JSON-RPC response"
    actual = [item["id"] for item in responses]
    if any(type(identifier) not in (str, int, float, type(None)) for identifier in expected + actual):
        return "Upstream response contains an invalid JSON-RPC id type"
    if Counter((isinstance(identifier, str), identifier) for identifier in expected) != \
            Counter((isinstance(identifier, str), identifier) for identifier in actual):
        return "Upstream response ids do not match the request ids"
    return None


class Recorder:
    def __init__(self, upstream, path, tag, timeout=120, secrets=()):
        self.upstream, self.path, self.tag = upstream, path, tag
        self.timeout, self.secrets = timeout, secrets
        self.flow = "proxy"
        self.sequence = 0
        self.outcomes = Counter()
        self.lock = threading.Lock()
        self.local = threading.local()
        self.connections = set()
        self.endpoint = urllib.parse.urlsplit(upstream)
        if self.endpoint.scheme not in ("http", "https") or not self.endpoint.hostname:
            raise ValueError("Upstream must be an HTTP or HTTPS URL")
        self.request_path = urllib.parse.urlunsplit(("", "", self.endpoint.path or "/", self.endpoint.query, ""))

    def connection(self):
        connection = getattr(self.local, "connection", None)
        if connection is None:
            connection_type = http.client.HTTPSConnection if self.endpoint.scheme == "https" else http.client.HTTPConnection
            connection = connection_type(self.endpoint.hostname, self.endpoint.port, timeout=self.timeout)
            self.local.connection = connection
            with self.lock:
                self.connections.add(connection)
        return connection

    def discard_connection(self, connection):
        connection.close()
        self.local.connection = None
        with self.lock:
            self.connections.discard(connection)

    def close(self):
        with self.lock:
            connections, self.connections = self.connections, set()
        for connection in connections:
            connection.close()

    def forward(self, payload, **metadata):
        started = time.perf_counter()
        with self.lock:
            self.sequence += 1
            sequence = self.sequence
        row = {"sequence": sequence, "tag": self.tag, "flow": self.flow,
               "started_utc": timestamp(), "request": payload, **metadata}
        calls = payload if isinstance(payload, list) else [payload]
        blocked = not calls or any(not isinstance(call, dict) or
            call.get("method") not in READ_METHODS for call in calls)
        if blocked:
            response = [{"jsonrpc": "2.0", "id": call.get("id") if isinstance(call, dict) else None,
                         "error": {"code": -32099, "message": "Read-only benchmark proxy: method blocked"}}
                        for call in calls]
            if not isinstance(payload, list):
                response = response[0]
            body, status = json.dumps(response).encode(), 200
            row["blocked"] = True
        else:
            connection = self.connection()
            try:
                connection.request("POST", self.request_path, body=json.dumps(payload).encode(),
                                   headers={"Content-Type": "application/json", "User-Agent": USER_AGENT})
                response = connection.getresponse()
                status, body = response.status, response.read()
                if response.will_close or status >= 400:
                    self.discard_connection(connection)
            except (OSError, http.client.HTTPException) as error:
                self.discard_connection(connection)
                row["transport_error"] = str(error)
                status = 502
                body = json.dumps({"jsonrpc": "2.0", "id": None, "error": {
                    "code": -32098, "message": "Upstream transport failure"}}).encode()
        row["elapsed_ms"] = (time.perf_counter() - started) * 1000
        row["http_status"] = status
        try:
            omitted = not body.strip()
            response = None if omitted else json.loads(body)
            row["response"] = response
            responses = response if isinstance(response, list) else [response]
            row["rpc_errors"] = [item["error"] for item in responses
                                 if isinstance(item, dict) and "error" in item]
            if not blocked and not row.get("transport_error"):
                invalid = response_error(payload, response, omitted)
                if invalid:
                    row["protocol_error"] = invalid
        except (ValueError, UnicodeDecodeError):
            row["protocol_error"] = "Upstream returned non-JSON content"
            row["response_text"] = body.decode("utf-8", errors="replace")
        row["outcome"] = ("blocked" if blocked else "transport_failure" if row.get("transport_error")
                          else "http_error" if status >= 400 else "invalid_response" if row.get("protocol_error")
                          else "rpc_error" if row.get("rpc_errors") else "ok")
        with self.lock:
            with self.path.open("a", encoding="utf-8") as stream:
                stream.write(redact(json.dumps(row), self.secrets) + "\n")
            self.outcomes[row["flow"], row["outcome"]] += 1
        return status, body, row


@contextlib.contextmanager
def proxy(recorder):
    class Handler(http.server.BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def handle(self):
            try:
                super().handle()
            except (ConnectionResetError, ConnectionAbortedError):
                pass

        def finish(self):
            try:
                super().finish()
            finally:
                connection = getattr(recorder.local, "connection", None)
                if connection is not None:
                    recorder.discard_connection(connection)

        def do_POST(self):
            try:
                payload = json.loads(self.rfile.read(int(self.headers.get("Content-Length", 0))))
                status, body, _ = recorder.forward(payload)
            except (ValueError, UnicodeDecodeError):
                status, body = 400, b'{"error":"Invalid JSON request"}'
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            try:
                self.wfile.write(body)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

        def log_message(self, *_args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        yield f"http://127.0.0.1:{server.server_port}"
    finally:
        server.shutdown()
        server.server_close()
        thread.join()
        recorder.close()


def capture(args):
    out = output_dir(args.output)
    if (out / "rpc.jsonl").exists() or (out / "run.json").exists():
        raise ValueError("Capture output already exists; choose a new output directory")
    password, secrets = secrets_from_runbook(args.runbook)
    home = out / "home"
    data = home / ".kohaku-cli"
    source = args.data_source / args.wallet
    destination = data / args.wallet
    copy_wallet(source, destination, secrets)
    if args.reset_private_state:
        (destination / "rg-storage.json").unlink(missing_ok=True)
    temporary = out / "temp"
    temporary.mkdir()
    environment = dict(os.environ, HOME=str(home), USERPROFILE=str(home),
                       TEMP=str(temporary), TMP=str(temporary), KOHAKU_WITHOUT_TOR="1")
    recorder = Recorder(args.upstream, out / "rpc.jsonl", args.tag, args.rpc_timeout, secrets)
    flows = []
    with proxy(recorder) as rpc:
        environment["RPC_URL"] = rpc
        common = ["--wallet", args.wallet, "--password", password, "--non-interactive",
                  "--without-tor", "--dataDir", str(data), "--rpc-url", rpc]
        commands = {
            "shield": ["shield", "--protocol", "railgun", "--from", "0", "--amount-formatted", "0.01"],
            "transfer": ["transfer", "--from", "0", "--to", args.recipient, "--amount-formatted", "0.0001"],
            "balances": ["balances", "--include", "railgun"],
        }
        for flow in args.flows:
            recorder.flow = flow
            started = time.perf_counter()
            with recorder.lock:
                before = recorder.sequence
                before_outcomes = recorder.outcomes.copy()
            result = {"flow": flow, "tag": args.tag, "started_utc": timestamp()}
            try:
                process = subprocess.run([args.node, str(args.cli), *commands[flow], *common],
                    cwd=ROOT, env=environment, capture_output=True, timeout=args.flow_timeout)
                stdout, stderr = process.stdout, process.stderr
                result["exit_code"] = process.returncode
            except subprocess.TimeoutExpired as error:
                stdout, stderr = error.stdout or b"", error.stderr or b""
                result["exit_code"], result["timed_out"] = None, True
            result["elapsed_ms"] = (time.perf_counter() - started) * 1000
            with recorder.lock:
                result["rpc_requests"] = recorder.sequence - before
                completed = recorder.outcomes - before_outcomes
                result["rpc_outcomes"] = {outcome: count for (recorded_flow, outcome), count in completed.items()
                                          if recorded_flow == flow}
            result["rpc_completed_requests"] = sum(result["rpc_outcomes"].values())
            result["rpc_pending_requests"] = result["rpc_requests"] - result["rpc_completed_requests"]
            result["rpc_failed_requests"] = sum(count for outcome, count in result["rpc_outcomes"].items()
                                                 if outcome != "ok")
            result["status"] = ("failed" if result.get("timed_out") or result["exit_code"] != 0 else
                                "degraded" if result["rpc_failed_requests"] or result["rpc_pending_requests"] else "ok")
            for label, content in (("stdout", stdout), ("stderr", stderr)):
                (out / f"{flow}.{label}.txt").write_text(
                    redact(content.decode("utf-8", errors="replace"), secrets), encoding="utf-8")
            flows.append(result)
            status = "failed" if any(item["status"] == "failed" for item in flows) else \
                     "degraded" if any(item["status"] == "degraded" for item in flows) else "ok"
            write_json(out / "run.json", {"tag": args.tag, "flows": flows, "status": status,
                "private_state": "reset" if args.reset_private_state else "copied",
                "tor": False, "broadcast": False, "user_agent": USER_AGENT,
                "transport": "HTTP/1.1 persistent connections per proxy worker"})
            print(json.dumps(result), flush=True)
    return int(any(flow["status"] != "ok" for flow in flows))


def replay(args):
    out = output_dir(args.output)
    if (out / "rpc.jsonl").exists() or (out / "run.json").exists():
        raise ValueError("Replay output already exists; choose a new output directory")
    recorder = Recorder(args.upstream, out / "rpc.jsonl", args.tag, args.rpc_timeout)
    rows = sorted((json.loads(line) for line in args.input.read_text(encoding="utf-8").splitlines()
                   if line.strip()), key=lambda row: row["sequence"])
    started, errors = time.perf_counter(), 0
    try:
        for source in rows:
            recorder.flow = source["flow"]
            request, rewrites = pin_block(source["request"], args.block) if args.block else (source["request"], [])
            _, _, result = recorder.forward(request, source_sequence=source["sequence"], selector_rewrites=rewrites)
            if result.get("transport_error") or result.get("protocol_error") or result.get("rpc_errors") \
                    or result["http_status"] >= 400:
                errors += 1
    finally:
        recorder.close()
    summary = {"tag": args.tag, "requests": len(rows), "error_responses": errors,
               "elapsed_ms": (time.perf_counter() - started) * 1000,
               "source": str(args.input.resolve()), "mode": "sequential",
               "pinned_block": args.block, "user_agent": USER_AGENT,
               "transport": "HTTP/1.1 persistent connection"}
    write_json(out / "run.json", summary)
    print(json.dumps(summary))
    return int(errors != 0)


def self_test():
    import unittest

    class ProxyTests(unittest.TestCase):
        def test_capture_replay_errors_and_send_guard(self):
            received = []
            client_ports = []

            class Fake(http.server.BaseHTTPRequestHandler):
                protocol_version = "HTTP/1.1"

                def do_POST(self):
                    request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                    received.append(request)
                    client_ports.append(self.client_address[1])
                    response = {"jsonrpc": "2.0", "id": request["id"]}
                    status = 200
                    if request["method"] == "eth_call":
                        response["error"] = {"code": -32000, "message": "test revert"}
                    elif request["method"] == "eth_getCode":
                        status, response["error"] = 503, {"code": -32001, "message": "unavailable"}
                    else:
                        response["result"] = "0xaa36a7"
                    if request["method"] == "eth_blockNumber":
                        response["id"] = 999
                    body = json.dumps(response).encode()
                    self.send_response(status)
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

                def log_message(self, *_args):
                    pass

            base = output_dir(ROOT / "artifacts" / "rpc-wallet-self-test")
            with tempfile.TemporaryDirectory(dir=base) as temporary:
                folder = Path(temporary)
                fake = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Fake)
                thread = threading.Thread(target=fake.serve_forever, daemon=True)
                thread.start()
                upstream = f"http://127.0.0.1:{fake.server_port}"
                try:
                    recorder = Recorder(upstream, folder / "source.jsonl", "cold")
                    with proxy(recorder) as rpc:
                        endpoint = urllib.parse.urlsplit(rpc)
                        client = http.client.HTTPConnection(endpoint.hostname, endpoint.port)
                        for number, method in enumerate(("eth_chainId", "eth_call", "eth_getCode", "eth_chainId",
                                                         "eth_blockNumber", "eth_sendRawTransaction")):
                            payload = {"jsonrpc": "2.0", "id": number, "method": method, "params": []}
                            client.request("POST", "/", body=json.dumps(payload).encode())
                            response = client.getresponse()
                            json.loads(response.read())
                            self.assertEqual(response.status, 503 if method == "eth_getCode" else 200)
                        client.close()
                    rows = [json.loads(line) for line in recorder.path.read_text().splitlines()]
                    self.assertEqual([row["request"]["id"] for row in rows], list(range(6)))
                    self.assertEqual(len(received), 5)
                    self.assertEqual(client_ports[0], client_ports[1])
                    self.assertNotEqual(client_ports[2], client_ports[3])
                    self.assertFalse(recorder.connections)
                    self.assertEqual(rows[1]["rpc_errors"][0]["code"], -32000)
                    self.assertEqual(rows[2]["http_status"], 503)
                    self.assertNotIn("transport_error", rows[2])
                    self.assertEqual(rows[4]["outcome"], "invalid_response")
                    self.assertIn("ids do not match", rows[4]["protocol_error"])
                    self.assertTrue(rows[5]["blocked"])
                    self.assertEqual(replay(argparse.Namespace(output=folder / "replayed", upstream=upstream,
                        input=recorder.path, tag="warm", rpc_timeout=2, block=None)), 1)
                    self.assertEqual(received[:5], received[5:])
                finally:
                    fake.shutdown()
                    fake.server_close()
                    thread.join()
                with socket.socket() as dead:
                    dead.bind(("127.0.0.1", 0))
                    dead_url = f"http://127.0.0.1:{dead.getsockname()[1]}"
                failed = Recorder(dead_url, folder / "failed.jsonl", "test", .1)
                _, _, failure = failed.forward(
                    {"jsonrpc": "2.0", "id": 1, "method": "eth_chainId", "params": []})
                self.assertIn("transport_error", failure)
                self.assertEqual(failure["http_status"], 502)
                self.assertFalse(failed.connections)
                self.assertEqual(redact("a password b", ["password"]), "a <redacted> b")

        def test_response_id_multisets_batch_shape_and_notifications(self):
            request = lambda identifier: {"jsonrpc": "2.0", "method": "eth_chainId", "id": identifier}
            response = lambda identifier: {"jsonrpc": "2.0", "result": "0xaa36a7", "id": identifier}
            notification = {"jsonrpc": "2.0", "method": "eth_chainId"}
            batch = [request(1), notification, request(2)]
            for name, payload, result, omitted, valid in (
                ("correct singleton", request(1), response(1), False, True),
                ("wrong singleton id", request(1), response(999), False, False),
                ("string versus number", request(1), response("1"), False, False),
                ("boolean id", request(1), response(True), False, False),
                ("singleton returned array", request(1), [response(1)], False, False),
                ("batch returned object", [request(1)], response(1), False, False),
                ("reordered mixed batch", batch, [response(2), response(1)], False, True),
                ("missing batch response", batch, [response(1)], False, False),
                ("duplicate batch response", batch, [response(1), response(1)], False, False),
                ("notification extra response", batch, [response(1), response(2), response(None)], False, False),
                ("duplicate requested ids", [request(1), request(1)], [response(1), response(1)], False, True),
                ("notification omitted", notification, None, True, True),
                ("notification null response", notification, None, False, False),
                ("notification batch omitted", [notification], None, True, True),
                ("notification batch empty array", [notification], [], False, False),
            ):
                with self.subTest(name=name):
                    self.assertEqual(response_error(payload, result, omitted) is None, valid)

        def test_wallet_copy_rejects_plaintext_and_unignored_output(self):
            with self.assertRaises(ValueError):
                output_dir(ROOT / "scripts" / "wallet-copy-test")
            base = output_dir(ROOT / "artifacts" / "rpc-wallet-self-test")
            with tempfile.TemporaryDirectory(dir=base) as temporary:
                source = Path(temporary) / "source"
                source.mkdir()
                (source / ".wallet-type").write_text("testnet")
                (source / "public-accounts.json").write_text('{"private_key":"plaintext-secret"}')
                destination = Path(temporary) / "destination"
                with self.assertRaises(ValueError):
                    copy_wallet(source, destination, ["plaintext-secret"])
                self.assertFalse(destination.exists())

        def test_capture_degrades_rpc_failure_when_wallet_exits_successfully(self):
            import sys

            class Errors(http.server.BaseHTTPRequestHandler):
                protocol_version = "HTTP/1.1"

                def do_POST(self):
                    request = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
                    body = json.dumps({"jsonrpc": "2.0", "id": request["id"],
                                       "error": {"code": -32000, "message": "synthetic failure"}}).encode()
                    self.send_response(200)
                    self.send_header("Content-Length", str(len(body)))
                    self.end_headers()
                    self.wfile.write(body)

                def log_message(self, *_args):
                    pass

            base = output_dir(ROOT / "artifacts" / "rpc-wallet-self-test")
            with tempfile.TemporaryDirectory(dir=base) as temporary:
                folder = Path(temporary)
                wallet = folder / "wallets" / "synthetic"
                wallet.mkdir(parents=True)
                (wallet / ".wallet-type").write_text("testnet")
                runbook = folder / "runbook.md"
                runbook.write_text("| Wallet password | `synthetic-test-password` |")
                cli = folder / "wallet.py"
                cli.write_text("import json, os, urllib.request\n"
                    "request = urllib.request.Request(os.environ['RPC_URL'], data=json.dumps("
                    "{'jsonrpc':'2.0','id':1,'method':'eth_call','params':[{}]}).encode())\n"
                    "with urllib.request.urlopen(request) as response: response.read()\n"
                    "print('{}')\n")
                server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Errors)
                thread = threading.Thread(target=server.serve_forever, daemon=True)
                thread.start()
                try:
                    result = capture(argparse.Namespace(output=folder / "captured", runbook=runbook,
                        data_source=folder / "wallets", wallet="synthetic", reset_private_state=False,
                        upstream=f"http://127.0.0.1:{server.server_port}", tag="synthetic", rpc_timeout=2,
                        node=sys.executable, cli=cli, recipient="0x1", flows=["transfer"], flow_timeout=10))
                finally:
                    server.shutdown()
                    server.server_close()
                    thread.join()
                summary = json.loads((folder / "captured" / "run.json").read_text())
                flow = summary["flows"][0]
                raw = json.loads((folder / "captured" / "rpc.jsonl").read_text())
                self.assertEqual(result, 1)
                self.assertEqual(summary["status"], "degraded")
                self.assertEqual(flow["exit_code"], 0)
                self.assertEqual(flow["status"], "degraded")
                self.assertEqual(flow["rpc_outcomes"], {"rpc_error": 1})
                self.assertEqual(flow["rpc_failed_requests"], 1)
                self.assertEqual(flow["rpc_completed_requests"], 1)
                self.assertEqual(flow["rpc_pending_requests"], 0)
                self.assertEqual(raw["outcome"], "rpc_error")
                self.assertEqual(raw["rpc_errors"][0]["code"], -32000)

        def test_only_latest_and_omitted_block_selectors_are_pinned(self):
            for method, params, expected in (
                ("eth_call", [{}, "latest"], [{}, "0x123"]),
                ("eth_estimateGas", [{}], [{}, "0x123"]),
                ("eth_getBalance", ["0x1", "pending"], ["0x1", "pending"]),
                ("eth_getStorageAt", ["0x1", "0x0", "0x100"], ["0x1", "0x0", "0x100"]),
                ("eth_getProof", ["0x1", []], ["0x1", [], "0x123"]),
                ("eth_feeHistory", [1, "latest", []], [1, "latest", []]),
            ):
                with self.subTest(method=method):
                    request = {"method": method, "params": params}
                    pinned, changes = pin_block(request, "0x123")
                    self.assertEqual(pinned["params"], expected)
                    self.assertEqual(bool(changes), params != expected)
                    self.assertEqual(request["params"], params)

    return int(not unittest.TextTestRunner().run(unittest.defaultTestLoader.loadTestsFromTestCase(ProxyTests)).wasSuccessful())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    capture_parser = commands.add_parser("capture", help="Run real wallet dry runs through a recording proxy")
    replay_parser = commands.add_parser("replay", help="Replay a capture without running the wallet")
    commands.add_parser("self-test", help="Verify proxy, replay, RPC/HTTP/transport errors and broadcast guard locally")
    for command in (capture_parser, replay_parser):
        command.add_argument("--upstream", required=True)
        command.add_argument("--output", required=True, type=Path)
        command.add_argument("--tag", required=True)
        command.add_argument("--rpc-timeout", type=float, default=120)
    capture_parser.add_argument("--runbook", type=Path, default=TOOLS / "secrets" / "AGENT-RUNBOOK.md")
    capture_parser.add_argument("--cli", type=Path, default=TOOLS / "src" / "kohaku-cli" / "bin" / "kohaku.mjs")
    capture_parser.add_argument("--data-source", type=Path, default=TOOLS / "data" / "kohaku-home" / ".kohaku-cli")
    capture_parser.add_argument("--wallet", default="fvlab-sepolia")
    capture_parser.add_argument("--recipient", default="0xb004ADd4372a096d0b57a6821b07B01F5b29BA36")
    capture_parser.add_argument("--node", default="node")
    capture_parser.add_argument("--flow-timeout", type=float, default=900)
    capture_parser.add_argument("--flows", nargs="+", choices=("shield", "transfer", "balances"),
                                default=["transfer", "shield", "balances"])
    capture_parser.add_argument("--reset-private-state", action="store_true", help="Remove only the copied Railgun sync state")
    replay_parser.add_argument("--input", required=True, type=Path)
    replay_parser.add_argument("--block", help="Pin omitted/latest state block selectors to a shared hex block")
    args = parser.parse_args()
    if args.command == "replay" and args.block and not re.fullmatch(r"0x[0-9a-fA-F]+", args.block):
        parser.error("--block must be a hex block number")
    if args.command == "capture":
        return capture(args)
    if args.command == "replay":
        return replay(args)
    return self_test()


if __name__ == "__main__":
    raise SystemExit(main())
