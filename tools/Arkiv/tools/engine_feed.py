#!/usr/bin/env python3
"""engine_feed.py ENGINE_URL JWT_HEX_FILE PAYLOADS.jsonl [--fcu-every N] [--safe-lag 31] [--finalized-lag 63] [--report FILE]

Feeds engine_newPayloadV4 payloads (from examples/payloads.rs) to an execution client in order, with an
engine_forkchoiceUpdatedV3 to the newest one every N payloads, and reports throughput. Safe and finalized trail the
head as Lighthouse keeps them on Rabdi (31 and 63 blocks, read off the node's forkchoice log); the zero hash stands for
them until the chain is that long. It is how the benchmark drives
Nethermind and arkiv-reth with identical work; a node following the live chain gets the same calls from its
consensus client instead. Stops at the first payload not answered VALID.
"""
import base64, hashlib, hmac, json, sys, time, urllib.request


def jwt(secret: bytes) -> str:
    enc = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=").decode()
    head = enc(json.dumps({"alg": "HS256", "typ": "JWT"}).encode())
    body = enc(json.dumps({"iat": int(time.time())}).encode())
    sig = enc(hmac.new(secret, f"{head}.{body}".encode(), hashlib.sha256).digest())
    return f"{head}.{body}.{sig}"


def forkchoice(recent, safe_lag, finalized_lag, zero):
    lagged = lambda lag: recent[-1 - lag] if len(recent) > lag else zero
    return {"headBlockHash": recent[-1], "safeBlockHash": lagged(safe_lag), "finalizedBlockHash": lagged(finalized_lag)}


def main():
    url, jwt_file, payloads = sys.argv[1:4]
    args = sys.argv[4:]
    fcu_every = int(args[args.index("--fcu-every") + 1]) if "--fcu-every" in args else 1
    report = args[args.index("--report") + 1] if "--report" in args else None
    safe_lag = int(args[args.index("--safe-lag") + 1]) if "--safe-lag" in args else 31
    finalized_lag = int(args[args.index("--finalized-lag") + 1]) if "--finalized-lag" in args else 63
    zero = "0x" + "00" * 32
    recent = []
    secret = bytes.fromhex(open(jwt_file).read().strip().removeprefix("0x"))
    ids = iter(range(1, 1 << 62))

    def call(method, params):
        body = json.dumps({"jsonrpc": "2.0", "id": next(ids), "method": method, "params": params}).encode()
        req = urllib.request.Request(url, data=body, headers={"content-type": "application/json", "authorization": "Bearer " + jwt(secret)})
        with urllib.request.urlopen(req, timeout=600) as r:
            res = json.loads(r.read())
        if "error" in res:
            raise RuntimeError(f"{method}: {res['error']}")
        return res["result"]

    started = time.time()
    n = txs = 0
    latencies = []
    head = None
    with open(payloads) as f:
        for line in f:
            params = json.loads(line)
            payload = params[0]
            t0 = time.perf_counter()
            status = call("engine_newPayloadV4", params)
            latencies.append(time.perf_counter() - t0)
            if status["status"] != "VALID":
                print(f"block {int(payload['blockNumber'], 16)}: {status}", file=sys.stderr)
                sys.exit(1)
            head = payload["blockHash"]
            recent.append(head)
            if len(recent) > finalized_lag + 1:
                recent.pop(0)
            n += 1
            txs += len(payload["transactions"])
            if n % fcu_every == 0:
                call("engine_forkchoiceUpdatedV3", [forkchoice(recent, safe_lag, finalized_lag, zero), None])
            if n % 5000 == 0:
                el = time.time() - started
                print(f"{n} blocks ({int(payload['blockNumber'], 16)}), {txs} txs, {n / el:.1f} blocks/s", file=sys.stderr, flush=True)
    if head:
        call("engine_forkchoiceUpdatedV3", [forkchoice(recent, safe_lag, finalized_lag, zero), None])
    elapsed = time.time() - started
    latencies.sort()
    pct = lambda p: latencies[min(len(latencies) - 1, int(p * len(latencies)))] * 1000
    summary = {"blocks": n, "txs": txs, "seconds": round(elapsed, 2), "blocks_per_s": round(n / elapsed, 2), "safe_lag": safe_lag, "finalized_lag": finalized_lag,
               "newPayload_ms": {"p50": round(pct(0.5), 3), "p90": round(pct(0.9), 3), "p99": round(pct(0.99), 3), "max": round(latencies[-1] * 1000, 3)}}
    print(json.dumps(summary))
    if report:
        json.dump(summary, open(report, "w"), indent=2)


if __name__ == "__main__":
    main()
