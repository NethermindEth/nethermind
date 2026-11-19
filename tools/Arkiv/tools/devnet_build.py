#!/usr/bin/env python3
"""devnet_build.py STEPS [--engine] — build STEPS blocks on Nethermind with testing_buildBlockV1 (transactions from its pool), and import
each into arkiv-reth and Nethermind with engine_newPayloadV4 + forkchoiceUpdatedV3. --engine builds through the
Engine API instead (forkchoiceUpdatedV3 with payload attributes, then getPayloadV4). Endpoints from NM, NM_ENGINE, RETH,
RETH_ENGINE and JWT (scripts/run-devnet.sh's defaults); EXTRA_ENGINES ("name=url,...") are fed each block too, as a
consensus client would feed a node that is syncing (it answers SYNCING until it has caught up)."""
import json, os, sys, time, hmac, hashlib, base64, urllib.request

env = lambda name, default: os.environ.get(name, default)
NM, NM_ENGINE = env("NM", "http://127.0.0.1:38545"), env("NM_ENGINE", "http://127.0.0.1:38551")
RETH, RETH_ENGINE = env("RETH", "http://127.0.0.1:18645"), env("RETH_ENGINE", "http://127.0.0.1:18651")
SECRET = bytes.fromhex(open(env("JWT", "jwt.hex")).read().strip())
EXTRA = [tuple(e.split("=", 1)) for e in env("EXTRA_ENGINES", "").split(",") if e]

def jwt():
    b = lambda d: base64.urlsafe_b64encode(d).rstrip(b"=")
    h, p = b(b'{"alg":"HS256","typ":"JWT"}'), b(json.dumps({"iat": int(time.time())}).encode())
    return (h + b"." + p + b"." + b(hmac.new(SECRET, h + b"." + p, hashlib.sha256).digest())).decode()

def rpc(url, method, *params, auth=False):
    req = urllib.request.Request(url, json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": list(params)}).encode(),
                                 {"content-type": "application/json", **({"authorization": "Bearer " + jwt()} if auth else {})})
    r = json.load(urllib.request.urlopen(req, timeout=30))
    if "error" in r: raise RuntimeError(f"{method}: {r['error']}")
    return r["result"]

def build_one(engine=False):
    head = rpc(NM, "eth_getBlockByNumber", "latest", False)
    attrs = {"timestamp": hex(int(head["timestamp"], 16) + 2), "prevRandao": "0x" + "11" * 32,
             "suggestedFeeRecipient": "0x" + "c0" * 20, "withdrawals": [], "parentBeaconBlockRoot": "0x" + "22" * 32}
    if engine:
        state = {"headBlockHash": head["hash"], "safeBlockHash": head["hash"], "finalizedBlockHash": head["hash"]}
        payload_id = rpc(NM_ENGINE, "engine_forkchoiceUpdatedV3", state, attrs, auth=True)["payloadId"]
        time.sleep(1)
        built = rpc(NM_ENGINE, "engine_getPayloadV4", payload_id, auth=True)
    else:
        # No transaction list: Nethermind takes them from its pool.
        built = rpc(NM, "testing_buildBlockV1", head["hash"], attrs, None)
    p = built["executionPayload"]
    requests = built.get("executionRequests", [])
    print(f"built #{int(p['blockNumber'],16)} {p['blockHash']} txs={len(p['transactions'])} gasUsed={int(p['gasUsed'],16)} stateRoot={p['stateRoot']}")
    for name, url in [("arkiv-reth", RETH_ENGINE), ("nethermind", NM_ENGINE)] + EXTRA:
        s = rpc(url, "engine_newPayloadV4", p, [], attrs["parentBeaconBlockRoot"], requests, auth=True)
        f = rpc(url, "engine_forkchoiceUpdatedV3", {"headBlockHash": p["blockHash"], "safeBlockHash": p["blockHash"], "finalizedBlockHash": head["hash"]}, None, auth=True)
        print(f"  {name}: newPayload {s['status']}{' ' + str(s.get('validationError')) if s.get('validationError') else ''}, forkchoice {f['payloadStatus']['status']}")
    return p

if __name__ == "__main__":
    for _ in range(int(sys.argv[1])):
        build_one("--engine" in sys.argv)
