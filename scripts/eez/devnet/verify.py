#!/usr/bin/env python3
import argparse
import json
from pathlib import Path
import time
import urllib.request

HEADER = ("hash", "parentHash", "sha3Uncles", "miner", "stateRoot", "transactionsRoot", "receiptsRoot", "logsBloom", "difficulty", "number", "gasLimit", "gasUsed", "timestamp", "extraData", "mixHash", "nonce", "baseFeePerGas")
RECEIPT = ("transactionHash", "transactionIndex", "blockHash", "blockNumber", "from", "to", "cumulativeGasUsed", "gasUsed", "contractAddress", "logsBloom", "status", "type", "effectiveGasPrice", "logs")
SYSTEM = "0xeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee0076"


class RpcError(RuntimeError):
    pass


class Rpc:
    def __init__(self, url, deadline):
        self.url = url
        self.deadline = deadline
        self.sequence = 0

    def call(self, method, *params):
        remaining = self.deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("verification deadline exceeded")
        self.sequence += 1
        request = urllib.request.Request(self.url, data=json.dumps({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params}).encode(), headers={"Content-Type": "application/json"})
        with urllib.request.urlopen(request, timeout=min(10, remaining)) as response:
            data = response.read(8 * 1024 * 1024 + 1)
        if len(data) > 8 * 1024 * 1024:
            raise ValueError("RPC response exceeds limit")
        value = json.loads(data)
        if value.get("jsonrpc") != "2.0" or value.get("id") != self.sequence:
            raise ValueError("invalid RPC response identity")
        if "error" in value:
            raise RpcError(method + ": " + json.dumps(value["error"]))
        if "result" not in value:
            raise ValueError("missing RPC result")
        return value["result"]


def normal(value):
    if isinstance(value, str):
        return value.lower() if value.startswith("0x") else value
    if isinstance(value, list):
        return [normal(item) for item in value]
    if isinstance(value, dict):
        return {key: normal(item) for key, item in value.items()}
    return value


def equal(left, right, message):
    if normal(left) != normal(right):
        raise AssertionError(message + ": " + str(left)[:256] + " != " + str(right)[:256])


def registry(l1, address, calldata, snapshot):
    raw = l1.call("eth_call", {"to": address, "data": calldata}, {"blockHash": snapshot["hash"], "requireCanonical": True})
    value = bytes.fromhex(raw.removeprefix("0x"))
    if len(value) != 96 or any(value[:12]) or not any(value[12:32]) or not any(value[32:64]):
        raise ValueError("invalid rollup registration snapshot")
    return "0x" + value[32:64].hex()


def check_receipt(reference, candidate, transaction, block_hash, index):
    left = reference.call("eth_getTransactionReceipt", transaction["hash"])
    right = candidate.call("eth_getTransactionReceipt", transaction["hash"])
    if not isinstance(left, dict) or not isinstance(right, dict):
        raise AssertionError("missing settled receipt")
    equal(left["blockHash"], block_hash, "reference receipt block")
    equal(right["blockHash"], block_hash, "candidate receipt block")
    equal(left["transactionIndex"], hex(index), "receipt transaction position")
    for field in RECEIPT:
        if field not in left or field not in right:
            raise AssertionError("missing receipt field " + field)
        equal(left[field], right[field], "receipt " + transaction["hash"] + " " + field)
    if int(transaction["type"], 16) == 0x76:
        if int(left["status"], 16) != 1 or int(left["gasUsed"], 16) <= 0 or int(left["effectiveGasPrice"], 16) != 0:
            raise AssertionError("invalid successful native receipt accounting")


def verify_prefix(reference, candidate, commitment, genesis, maximum):
    end = reference.call("eth_getBlockByHash", commitment, True)
    if not isinstance(end, dict) or end["hash"].lower() != commitment.lower():
        raise AssertionError("reference lacks registered endpoint")
    height = int(end["number"], 16)
    if not 0 < height <= maximum:
        raise AssertionError("settled range is empty or exceeds verification budget")
    blocks, receipts, native = 0, 0, 0
    current = commitment
    seen = set()
    for number in range(height, -1, -1):
        if current in seen:
            raise AssertionError("cyclic block ancestry")
        seen.add(current)
        left = reference.call("eth_getBlockByHash", current, True)
        right = candidate.call("eth_getBlockByHash", current, True)
        if not isinstance(left, dict) or not isinstance(right, dict):
            raise AssertionError("missing settled ancestor " + current)
        equal(left["hash"], current, "reference block identity")
        equal(right["hash"], current, "candidate block identity")
        equal(left["number"], hex(number), "reference block height")
        equal(right["number"], hex(number), "candidate block height")
        for field in HEADER:
            if field not in left or field not in right:
                raise AssertionError("missing block field " + field)
            equal(left[field], right[field], "block " + str(number) + " " + field)
        equal([tx["hash"] for tx in left["transactions"]], [tx["hash"] for tx in right["transactions"]], "transaction order")
        for index, transaction in enumerate(left["transactions"]):
            raw = reference.call("eth_getRawTransactionByHash", transaction["hash"])
            if not isinstance(raw, str) or not raw.startswith("0x") or len(raw) <= 2:
                raise AssertionError("missing encoded transaction")
            equal(raw, candidate.call("eth_getRawTransactionByHash", transaction["hash"]), "transaction encoding")
            check_receipt(reference, candidate, transaction, current, index)
            receipts += 1
            native += int(transaction["type"], 16) == 0x76
        blocks += 1
        if number == 0:
            equal(current, genesis, "registered genesis")
        current = left["parentHash"]
    state_context = {"blockHash": commitment, "requireCanonical": True}
    for method in ("eth_getBalance", "eth_getTransactionCount"):
        equal(reference.call(method, SYSTEM, state_context), candidate.call(method, SYSTEM, state_context), "native account " + method)
    return {"endpoint": commitment, "height": height, "verified_blocks": blocks, "verified_receipts": receipts, "verified_native_transactions": native}


def check_genesis(reference, candidate, args):
    headers = []
    for node in (reference, candidate):
        equal(node.call("eth_chainId"), hex(args.l2_chain_id), "L2 chain identity")
        header = node.call("eth_getBlockByNumber", "0x0", False)
        if not isinstance(header, dict) or not header.get("stateRoot"):
            raise AssertionError("missing genesis header or state root")
        equal(header.get("hash"), args.genesis, "L2 genesis")
        headers.append(header)
    equal(headers[0]["stateRoot"], headers[1]["stateRoot"], "genesis state root")
    state = {"hash": args.genesis, "state_root": headers[0]["stateRoot"], "chain_id": args.l2_chain_id,
             "system_account": SYSTEM, "balance": "0x0", "nonce": "0x0"}
    if args.genesis_evidence is None:
        equal(args.phase, "verify-eezl2-deployment", "genesis state requires initial deployment phase")
        for node in (reference, candidate):
            equal(node.call("eth_getBalance", SYSTEM, "0x0"), "0x0", "no native genesis funding")
            equal(node.call("eth_getTransactionCount", SYSTEM, "0x0"), "0x0", "native genesis nonce")
    else:
        evidence = json.loads(args.genesis_evidence.read_text())
        equal(evidence.get("result"), "pass", "genesis evidence must be a passed observation")
        equal(evidence.get("phase"), "verify-eezl2-deployment", "genesis evidence must come from initial deployment")
        equal(evidence.get("genesis_state"), state, "genesis evidence must match both current nodes")
    return state


def main():
    parser = argparse.ArgumentParser()
    for field in ("l1", "reference", "candidate", "registry", "registry-call", "genesis", "phase"):
        parser.add_argument("--" + field, required=True)
    parser.add_argument("--l1-chain-id", type=int, required=True)
    parser.add_argument("--l2-chain-id", type=int, required=True)
    parser.add_argument("--minimum-native", type=int, default=1)
    parser.add_argument("--minimum-height", type=int, default=1)
    parser.add_argument("--max-blocks", type=int, default=8192)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--genesis-evidence", type=Path)
    parser.add_argument("--genesis-only", action="store_true")
    parser.add_argument("--profile", choices=("rollup0", "rollupx"), default="rollup0")
    args = parser.parse_args()
    deadline = time.monotonic() + 600
    l1, reference, candidate = [Rpc(url, deadline) for url in (args.l1, args.reference, args.candidate)]
    equal(l1.call("eth_chainId"), hex(args.l1_chain_id), "L1 chain identity")
    if args.genesis_only and args.genesis_evidence is not None:
        raise ValueError("initial genesis capture cannot reuse prior evidence")
    genesis_state = check_genesis(reference, candidate, args)
    if args.genesis_only:
        args.output.write_text(json.dumps({"result": "pass", "scope": "genesis-only", "phase": args.phase,
                                          "genesis_state": genesis_state}, indent=2) + "\n")
        return
    attempts = 0
    while time.monotonic() < deadline:
        attempts += 1
        snapshot = l1.call("eth_getBlockByNumber", "latest", False)
        commitment = registry(l1, args.registry, args.registry_call, snapshot)
        endpoint = reference.call("eth_getBlockByHash", commitment, False)
        own = candidate.call("eth_getBlockByHash", commitment, False)
        if endpoint is None or own is None or int(endpoint["number"], 16) < args.minimum_height:
            time.sleep(2)
            continue
        result = verify_prefix(reference, candidate, commitment, args.genesis, args.max_blocks)
        equal(l1.call("eth_getBlockByNumber", snapshot["number"], False)["hash"], snapshot["hash"], "canonical L1 recheck")
        source_now = reference.call("eth_getBlockByNumber", hex(result["height"]), False)
        if source_now is None or source_now["hash"].lower() != commitment.lower():
            continue
        equal(candidate.call("eth_getBlockByNumber", hex(result["height"]), False)["hash"], commitment, "candidate canonical endpoint")
        if result["verified_native_transactions"] < args.minimum_native:
            raise AssertionError("required native scenarios produced no transactions")
        result.update({"genesis_state": genesis_state, "phase": args.phase, "canonical_l1_hash": snapshot["hash"], "canonical_l1_number": snapshot["number"], "snapshot_attempts": attempts, "result": "pass", "profile": "rollupx-native" if args.profile == "rollupx" else "legacy-rollup0-0x76"})
        args.output.write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result))
        return
    raise TimeoutError("Nethermind did not derive the required registered endpoint")


if __name__ == "__main__":
    main()
