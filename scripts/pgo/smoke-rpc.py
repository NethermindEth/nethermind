#!/usr/bin/env python3
"""Check deterministic eth_call results with state overrides against a local node."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import http.client
from urllib.parse import urlsplit


def check(connection, path, index):
    address = "0x1111111111111111111111111111111111111111"
    value = index % 251 + 1
    # RETURN a full word, with either arithmetic, SLOAD, or CALLDATALOAD as the producer.
    code, data, expected = (
        (f"60{value:02x}60110160005260206000f3", "0x", value + 17),
        ("60005460005260206000f3", "0x", value),
        ("60003560005260206000f3", "0x" + f"{value:064x}", value),
    )[index % 3]
    request = {"jsonrpc": "2.0", "id": index, "method": "eth_call", "params": [
        {"to": address, "gas": "0x100000", "data": data}, "latest",
        {address: {"code": "0x" + code, "state": {"0x" + "0" * 64: "0x" + f"{value:064x}"}}}]}
    body = json.dumps(request).encode()
    connection.request("POST", path, body, {"Content-Type": "application/json"})
    with connection.getresponse() as response:
        result = json.load(response)
        if response.status != 200:
            raise ValueError(f"HTTP {response.status}: {result}")
    if result.get("result") != "0x" + f"{expected:064x}":
        raise ValueError(f"eth_call {index} failed: {result}")


def worker(url, indexes):
    endpoint = urlsplit(url)
    connection_type = http.client.HTTPSConnection if endpoint.scheme == "https" else http.client.HTTPConnection
    connection = connection_type(endpoint.hostname, endpoint.port, timeout=30)
    try:
        for index in indexes:
            check(connection, endpoint.path or "/", index)
    finally:
        connection.close()


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--url", default="http://127.0.0.1:18545")
    parser.add_argument("--requests", type=int, default=10000)
    args = parser.parse_args()
    if args.requests <= 0:
        parser.error("--requests must be positive")
    with ThreadPoolExecutor(max_workers=8) as pool:
        futures = [pool.submit(worker, args.url, range(i, args.requests, 8)) for i in range(8)]
        for future in futures:
            future.result()
    print(f"PASS: {args.requests} eth_call results (arithmetic, storage and calldata overrides)")
