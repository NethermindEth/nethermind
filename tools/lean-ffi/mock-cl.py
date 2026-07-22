#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""
Minimal mock consensus client for EIP-8288 live testing.

Modifying a production CL to carry the new `recursive_stark` execution-payload fields is a large,
separate effort; this stands in for it. It drives Nethermind's Engine API through one block cycle and
checks that `recursive_stark` survives the getPayload -> newPayload round-trip (the relay a real CL
performs). Requires a running Nethermind node with EIP-8288 active and at least one dependency-frame
transaction in the pool. Not executed in CI — run against a live devnet node.

Usage:
    python3 mock-cl.py --engine http://localhost:8551 --jwt /path/to/jwt.hex \
        --head <parent_block_hash> --fee-recipient 0x...
"""
import argparse, base64, hashlib, hmac, json, time, urllib.request

def jwt_token(secret_hex: str) -> str:
    secret = bytes.fromhex(secret_hex.strip().removeprefix("0x"))
    b64 = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=")
    header = b64(b'{"alg":"HS256","typ":"JWT"}')
    payload = b64(json.dumps({"iat": int(time.time())}).encode())
    sig = b64(hmac.new(secret, header + b"." + payload, hashlib.sha256).digest())
    return (header + b"." + payload + b"." + sig).decode()

def rpc(engine: str, token: str, method: str, params: list):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    req = urllib.request.Request(engine, data=body, headers={
        "Content-Type": "application/json", "Authorization": f"Bearer {token}"})
    resp = json.load(urllib.request.urlopen(req))
    if "error" in resp:
        raise SystemExit(f"{method} error: {resp['error']}")
    return resp["result"]

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--engine", default="http://localhost:8551")
    ap.add_argument("--jwt", required=True)
    ap.add_argument("--head", required=True, help="parent block hash to build on")
    ap.add_argument("--fee-recipient", default="0x" + "00" * 20)
    args = ap.parse_args()
    token = jwt_token(open(args.jwt).read())

    fcs = {"headBlockHash": args.head, "safeBlockHash": args.head, "finalizedBlockHash": args.head}
    attrs = {"timestamp": hex(int(time.time())), "prevRandao": "0x" + "00" * 32,
             "suggestedFeeRecipient": args.fee_recipient, "withdrawals": [],
             "parentBeaconBlockRoot": "0x" + "00" * 32}
    fcu = rpc(args.engine, token, "engine_forkchoiceUpdatedV3", [fcs, attrs])
    payload_id = fcu["payloadId"]

    payload = rpc(args.engine, token, "engine_getPayloadV6", [payload_id])["executionPayload"]
    if "recursiveStarkProof" not in payload:
        raise SystemExit("FAIL: getPayload returned no recursive_stark (no deps, or EIP-8288 inactive)")
    print(f"getPayload carried recursive_stark: blockDepsHash={payload['recursiveStarkBlockDepsHash']}")

    # Relay the payload straight back, exactly as a CL would to a peer EL.
    status = rpc(args.engine, token, "engine_newPayloadV5", [payload, [], attrs["parentBeaconBlockRoot"]])
    print(f"newPayload status: {status['status']}")
    if status["status"] != "VALID":
        raise SystemExit(f"FAIL: recursive_stark did not round-trip: {status}")
    print("OK: recursive_stark survived getPayload -> newPayload")

if __name__ == "__main__":
    main()
