"""Minimal JSON-RPC client. One instance per execution node, so every measurement stays
attributable to the node that produced it."""
from __future__ import annotations

import itertools
import json
import threading
import time
import urllib.error
import urllib.request
from typing import Any


class RpcError(Exception):
    """A JSON-RPC error response. Carries the code so callers can tell an admission refusal
    (which is data) from a transport failure (which is a broken run)."""

    def __init__(self, code: int, message: str):
        super().__init__("{0} ({1})".format(message, code))
        self.code = code
        self.message = message


class RpcClient:
    def __init__(self, name: str, client: str, url: str, timeout: float = 20.0):
        self.name = name
        self.client = client
        self.url = url
        self.timeout = timeout
        self._ids = itertools.count(1)
        self._lock = threading.Lock()

    def call(self, method: str, params: list[Any] | None = None) -> Any:
        with self._lock:
            request_id = next(self._ids)
        payload = json.dumps(
            {"jsonrpc": "2.0", "id": request_id, "method": method, "params": params or []}
        ).encode()
        request = urllib.request.Request(
            self.url, data=payload, headers={"Content-Type": "application/json"}
        )
        with urllib.request.urlopen(request, timeout=self.timeout) as response:
            body = json.loads(response.read())
        if "error" in body and body["error"] is not None:
            error = body["error"]
            raise RpcError(int(error.get("code", 0)), str(error.get("message", "")))
        return body.get("result")

    # ---- convenience wrappers -------------------------------------------------------

    def chain_id(self) -> int:
        return int(self.call("eth_chainId"), 16)

    def block_number(self) -> int:
        return int(self.call("eth_blockNumber"), 16)

    def get_block(self, number: int | str = "latest", full: bool = False) -> dict | None:
        tag = number if isinstance(number, str) else hex(number)
        return self.call("eth_getBlockByNumber", [tag, full])

    def nonce(self, address: str, tag: str = "pending") -> int:
        return int(self.call("eth_getTransactionCount", [address, tag]), 16)

    def balance(self, address: str, tag: str = "latest") -> int:
        return int(self.call("eth_getBalance", [address, tag]), 16)

    def base_fee(self) -> int:
        block = self.get_block("latest")
        return int(block.get("baseFeePerGas", "0x0"), 16) if block else 0

    def send_raw(self, raw: bytes) -> str:
        return self.call("eth_sendRawTransaction", ["0x" + raw.hex()])

    def receipt(self, tx_hash: str) -> dict | None:
        return self.call("eth_getTransactionReceipt", [tx_hash])

    def code(self, address: str, tag: str = "latest") -> str:
        return self.call("eth_getCode", [address, tag])

    def wait_for_receipt(self, tx_hash: str, timeout: float, poll: float = 0.5) -> dict | None:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            receipt = self.receipt(tx_hash)
            if receipt is not None:
                return receipt
            time.sleep(poll)
        return None

    def wait_until_ready(self, timeout: float, poll: float = 2.0) -> bool:
        """True once the node answers eth_blockNumber. A node still starting up refuses
        connections, which is not a measurement, so callers gate on this before timing
        anything."""
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            try:
                self.block_number()
                return True
            except (urllib.error.URLError, OSError, RpcError, ValueError):
                time.sleep(poll)
        return False

    def __repr__(self) -> str:
        return "RpcClient({0}, {1})".format(self.name, self.url)
