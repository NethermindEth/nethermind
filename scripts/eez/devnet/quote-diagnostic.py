#!/usr/bin/env python3
import concurrent.futures
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import time
import urllib.request
import uuid


def rpc(url, method, params):
    started = time.time_ns()
    request = urllib.request.Request(
        url,
        data=json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode(),
        headers={"Content-Type": "application/json"},
    )
    try:
        with urllib.request.urlopen(request, timeout=2) as response:
            result = json.load(response)
    except Exception as error:
        result = {"diagnostic_error": str(error)}
    return {"method": method, "params": params, "started_ns": started,
            "finished_ns": time.time_ns(), "response": result}


def probes(url, sender):
    calls = [("eth_getBlockByNumber", ["latest", False]),
             ("eth_getTransactionCount", [sender, "latest"]),
             ("eth_getTransactionCount", [sender, "pending"])]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        results = list(pool.map(lambda call: rpc(url, *call), calls))
    head = results[0]["response"].get("result")
    if head:
        results.append(rpc(url, "eth_getTransactionCount", [sender, {"blockHash": head["hash"], "requireCanonical": True}]))
        results.append(rpc(url, "eth_getBlockByNumber", ["latest", False]))
    return results


def broadcast_manifest(text):
    match = re.search(r"(?m)^Transactions saved to: (.+)$", text)
    if not match:
        return None
    path = Path(match[1].strip()).resolve(strict=True)
    path.relative_to((Path.cwd() / "broadcast").resolve(strict=True))
    if path.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("broadcast manifest exceeds diagnostic limit")
    data = json.loads(path.read_text())
    receipt_fields = ("transactionHash", "blockHash", "blockNumber", "transactionIndex",
                      "status", "from", "to", "contractAddress")
    transaction_fields = ("from", "to", "nonce", "chainId", "type")
    return {
        "path": str(path.relative_to(Path.cwd())),
        "pending": data.get("pending", []),
        "transactions": [{"hash": tx.get("hash"), "contractAddress": tx.get("contractAddress"),
                          "transaction": {key: tx.get("transaction", {}).get(key) for key in transaction_fields}}
                         for tx in data.get("transactions", [])],
        "receipts": [{key: receipt.get(key) for key in receipt_fields} for receipt in data.get("receipts", [])],
    }


def main():
    tool = sys.argv[1]
    args = sys.argv[2:]
    real = os.environ["EEZ_REAL_" + tool.upper()]
    nonce = tool == "cast" and args[:1] == ["nonce"]
    quote = tool == "cast" and args[:1] == ["rpc"] and "eez_quoteFee" in args
    broadcast = tool == "forge" and args[:1] == ["script"] and "--broadcast" in args
    if not (nonce or quote or broadcast):
        os.execv(real, [real, *args])
    event = {"kind": "nonce" if nonce else "quote" if quote else "broadcast",
             "started_ns": time.time_ns(), "pid": os.getpid()}
    output = subprocess.run([real, *args], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    event.update({"finished_ns": time.time_ns(), "returncode": output.returncode,
                  "stdout": output.stdout.decode(errors="replace"),
                  "stderr": output.stderr.decode(errors="replace")})
    try:
        url = args[args.index("--rpc-url") + 1]
        event["url"] = url
        collect = os.environ["EEZ_DIAGNOSTIC_PROBE"] == "1"
        if nonce:
            event["sender"] = args[1]
            if collect:
                event["after_original_result"] = probes(url, args[1])
        elif quote:
            event["intent"] = json.loads(args[args.index("eez_quoteFee") + 1])
            sender = event["intent"].get("from")
            if sender and collect:
                event["after_original_result"] = probes(url, sender)
        else:
            event["script"] = args[1]
            text = event["stdout"] + event["stderr"]
            hashes = list(dict.fromkeys(re.findall(r"(?i)\b(?:transaction\s+hash|hash):\s*(0x[0-9a-f]{64})", text)))
            event["broadcast_manifest"] = broadcast_manifest(text)
            if event["broadcast_manifest"]:
                hashes = list(dict.fromkeys([*hashes, *[tx["hash"] for tx in event["broadcast_manifest"]["transactions"] if tx["hash"]]]))
            if collect:
                event["after_original_result"] = [rpc(url, "eth_getTransactionReceipt", [tx]) for tx in hashes]
    except Exception as error:
        event["diagnostic_error"] = str(error)
    finally:
        try:
            destination = Path(os.environ["EEZ_DIAGNOSTIC_DIR"])
            destination.mkdir(parents=True, exist_ok=True)
            (destination / (str(event["started_ns"]) + "-" + uuid.uuid4().hex + ".json")).write_text(json.dumps(event) + "\n")
        except Exception as error:
            print("diagnostic recording failed: " + str(error), file=sys.stderr)
        sys.stdout.buffer.write(output.stdout)
        sys.stderr.buffer.write(output.stderr)
    return output.returncode if output.returncode >= 0 else 128 - output.returncode


if __name__ == "__main__":
    sys.exit(main())
