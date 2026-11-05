#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Export consecutive blocks with their access lists and receipts for BalCatchUpBenchmark.

usage: bal-catchup-fixture.py --rpc URL --from N --to M --out DIR

Writes DIR/segment.jsonl, one block per line as {"number", "block", "bal", "receipts"} with the raw encodings of
debug_getRawBlock, debug_getRawBlockAccessList and debug_getRawReceipts, and DIR/meta.json with the range and its
gas. The node must keep the receipts and access lists of the range (History.BalRetentionEpochs).

The pre-state is a datadir copy of a node stopped at or before block N: on start the benchmark resumes from its
persisted state and skips the blocks at or below it, so export from below that block (a stopped flat node persists
up to about 150 blocks behind its head; its log reports "Start block loaded from reorg boundary").
"""

import argparse
import json
import os
import sys
import urllib.request


def call(url, method, params):
    return batch(url, [(method, params)])[0]


def batch(url, calls):
    """Sends the calls as one JSON-RPC batch and returns their results in order, failing on any error or null."""
    body = json.dumps([{"jsonrpc": "2.0", "id": i, "method": m, "params": p} for i, (m, p) in enumerate(calls)]).encode()
    request = urllib.request.Request(url, body, {"Content-Type": "application/json"})
    with urllib.request.urlopen(request, timeout=60) as response:
        reply = json.load(response)
    if isinstance(reply, dict):
        # A node that rejects the batch as a whole answers with a single error.
        raise RuntimeError(f"batch of {[m for m, _ in calls]}: {reply.get('error', reply)}")
    replies = {r["id"]: r for r in reply}
    results = []
    for i, (method, params) in enumerate(calls):
        reply = replies.get(i, {"error": "no reply"})
        if "error" in reply:
            raise RuntimeError(f"{method}{params}: {reply['error']}")
        if reply.get("result") is None:
            raise RuntimeError(f"{method}{params} returned nothing: the node may have pruned it (History.BalRetentionEpochs)")
        results.append(reply["result"])
    return results


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--rpc", required=True, help="JSON-RPC endpoint with the debug module enabled")
    parser.add_argument("--from", dest="first", type=int, required=True, help="first block")
    parser.add_argument("--to", dest="last", type=int, required=True, help="last block, inclusive")
    parser.add_argument("--out", required=True, help="output directory")
    args = parser.parse_args()
    if args.last < args.first:
        parser.error("--to is below --from")

    os.makedirs(args.out, exist_ok=True)
    gas = []
    with open(os.path.join(args.out, "segment.jsonl"), "w") as segment:
        for number in range(args.first, args.last + 1):
            tag = hex(number)
            header, block, bal, receipts = batch(args.rpc, [
                ("eth_getBlockByNumber", [tag, False]),
                ("debug_getRawBlock", [tag]),
                ("debug_getRawBlockAccessList", [tag]),
                ("debug_getRawReceipts", [tag]),
            ])
            line = {"number": number, "block": block, "bal": bal, "receipts": receipts}
            segment.write(json.dumps(line) + "\n")
            gas.append(int(header["gasUsed"], 16))
            if number % 100 == 0:
                print(f"exported {number}", file=sys.stderr)

    meta = {
        "rpc": args.rpc,
        "chainId": int(call(args.rpc, "eth_chainId", []), 16),
        "from": args.first,
        "to": args.last,
        "blocks": len(gas),
        "gasPerBlock": sum(gas) // len(gas),
        "maxGas": max(gas),
    }
    with open(os.path.join(args.out, "meta.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print(json.dumps(meta))


if __name__ == "__main__":
    main()
