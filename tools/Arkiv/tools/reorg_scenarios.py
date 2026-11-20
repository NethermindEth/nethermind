#!/usr/bin/env python3
"""reorg_scenarios.py BUILDER NODE... [--scenarios late,flip,burst,deep] [--timeout 1800] [--report FILE]

A consensus-layer mock that puts execution clients through the forks arkiv#287 describes, from their common head:

  late   a block, then a competing one at its height that wins (a late block replaced); the chain goes on from it
  flip   the same two blocks, but forkchoice goes back to the first, and the chain goes on from that one
  burst  six blocks at one height (a signing stall releasing them at once), forkchoice through each, then a child
  deep   two blocks, then a competing two-block branch from their parent that wins (a two-block reorg)

Each NODE is name=rpc_url,engine_url; BUILDER names the node that builds the payloads (forkchoiceUpdatedV3 with
attributes, then getPayloadV4) and must be one of them. Every node gets the same engine_newPayloadV4 and
engine_forkchoiceUpdatedV3 calls in the same order. A step is done for a node when its head is the block forkchoice
named; the time that took is reported, and a node that is not there within --timeout is reported stuck. JWT is the
shared secret's file (env JWT, default jwt.hex).
"""
import base64, hashlib, hmac, json, os, sys, time, urllib.request

SECRET = bytes.fromhex(open(os.environ.get("JWT", "jwt.hex")).read().strip().removeprefix("0x"))


def jwt():
    enc = lambda b: base64.urlsafe_b64encode(b).rstrip(b"=")
    h, p = enc(b'{"alg":"HS256","typ":"JWT"}'), enc(json.dumps({"iat": int(time.time())}).encode())
    return (h + b"." + p + b"." + enc(hmac.new(SECRET, h + b"." + p, hashlib.sha256).digest())).decode()


def rpc(url, method, *params, auth=False, timeout=60):
    req = urllib.request.Request(url, json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": list(params)}).encode(),
                                 {"content-type": "application/json", **({"authorization": "Bearer " + jwt()} if auth else {})})
    r = json.load(urllib.request.urlopen(req, timeout=timeout))
    if "error" in r: raise RuntimeError(f"{method}: {r['error']}")
    return r["result"]


class Node:
    def __init__(self, spec):
        self.name, urls = spec.split("=", 1)
        self.rpc, self.engine = urls.split(",")
        self.stuck = None

    def head(self):
        return rpc(self.rpc, "eth_getBlockByNumber", "latest", False)


ROOT = "0x" + "22" * 32
randao = iter(range(1, 1 << 30))


def build(builder, parent, offset=2):
    """A payload on `parent` from the builder; a fresh prevRandao each time, so every build is a distinct block."""
    attrs = {"timestamp": hex(int(parent["timestamp"], 16) + offset), "prevRandao": "0x" + f"{next(randao):064x}",
             "suggestedFeeRecipient": "0x" + "c0" * 20, "withdrawals": [], "parentBeaconBlockRoot": ROOT}
    state = {"headBlockHash": parent["hash"], "safeBlockHash": parent["hash"], "finalizedBlockHash": parent["hash"]}
    payload_id = rpc(builder.engine, "engine_forkchoiceUpdatedV3", state, attrs, auth=True, timeout=1800)["payloadId"]
    time.sleep(0.5)
    built = rpc(builder.engine, "engine_getPayloadV4", payload_id, auth=True)
    p = built["executionPayload"]
    p["_requests"] = built.get("executionRequests", [])
    p["hash"] = p["blockHash"]
    return p


def new_payload(node, p):
    body = {k: v for k, v in p.items() if not k.startswith("_") and k != "hash"}
    t = time.time()
    try:
        s = rpc(node.engine, "engine_newPayloadV4", body, [], ROOT, p["_requests"], auth=True, timeout=1800)
        status = s["status"] + (f" ({s['validationError']})" if s.get("validationError") else "")
    except Exception as e:
        status = f"error: {e}"
    return status, time.time() - t


def forkchoice(node, head_hash, finalized, timeout, slot=2.0):
    """Forkchoice to `head_hash`, sent again every slot as a consensus client does, until the node's head is that block.
    Nethermind answers newPayload SYNCING when processing takes longer than its timeout, and a forkchoice that arrives
    before the block is processed does not wait for it."""
    t = time.time()
    status, sent = None, 0.0
    while time.time() - t < timeout:
        if time.time() - sent >= slot:
            sent = time.time()
            try:
                f = rpc(node.engine, "engine_forkchoiceUpdatedV3",
                        {"headBlockHash": head_hash, "safeBlockHash": finalized, "finalizedBlockHash": finalized}, None, auth=True, timeout=timeout)
                status = status or f["payloadStatus"]["status"]
            except Exception as e:
                status = status or f"error: {e}"
        try:
            if node.head()["hash"] == head_hash: return status, time.time() - t, True
        except Exception:
            pass
        time.sleep(0.2)
    return status, time.time() - t, False


def step(nodes, label, payloads, head_hash, finalized, timeout, log):
    """Every node gets `payloads` (newPayload each) and then forkchoice to `head_hash`."""
    for node in nodes:
        if node.stuck: continue
        parts = []
        for p in payloads:
            status, took = new_payload(node, p)
            parts.append(f"newPayload #{int(p['blockNumber'], 16)} {p['hash'][:10]} {status} {took:.2f}s")
        status, took, there = forkchoice(node, head_hash, finalized, timeout)
        parts.append(f"forkchoice {head_hash[:10]} {status}, head there after {took:.2f}s" if there else f"forkchoice {head_hash[:10]} {status}, head NOT there after {took:.0f}s")
        if not there: node.stuck = label
        line = f"  {node.name:12s} {label}: " + "; ".join(parts)
        print(line, flush=True)
        log.append({"node": node.name, "step": label, "detail": parts, "reached": there, "seconds": took})


def scenario(name, builder, nodes, timeout, log):
    base = builder.head()
    final = base["hash"]
    print(f"== {name} from #{int(base['number'], 16)} {base['hash'][:10]}", flush=True)
    if name in ("late", "flip"):
        a, b = build(builder, base), build(builder, base)
        step(nodes, "A", [a], a["hash"], final, timeout, log)
        step(nodes, "B (competes with A)", [b], b["hash"], final, timeout, log)
        winner = b if name == "late" else a
        if name == "flip": step(nodes, "back to A", [], a["hash"], final, timeout, log)
        child = build(builder, winner)
        step(nodes, f"child of {'B' if winner is b else 'A'}", [child], child["hash"], final, timeout, log)
    elif name == "burst":
        siblings = [build(builder, base) for _ in range(6)]
        for i, s in enumerate(siblings):
            step(nodes, f"sibling {i + 1}/6", [s], s["hash"], final, timeout, log)
        child = build(builder, siblings[-1])
        step(nodes, "child of sibling 6", [child], child["hash"], final, timeout, log)
    elif name == "deep":
        # Both branches' first blocks are built over the base before either is imported. B2 is built once B1 is the
        # builder's head, as a proposer on that branch would; that is a reorg on the builder too.
        a1, b1 = build(builder, base), build(builder, base, offset=4)
        step(nodes, "A1", [a1], a1["hash"], final, timeout, log)
        a2 = build(builder, a1)
        step(nodes, "A2", [a2], a2["hash"], final, timeout, log)
        step(nodes, "B1 (competes with A1)", [b1], b1["hash"], final, timeout, log)
        b2 = build(builder, b1)
        step(nodes, "B2 (B branch wins, 2-block reorg)", [b2], b2["hash"], final, timeout, log)


def main():
    args = sys.argv[1:]
    opt = lambda flag, default: args[args.index(flag) + 1] if flag in args else default
    for flag in ("--scenarios", "--timeout", "--report"):
        if flag in args: del args[args.index(flag):args.index(flag) + 2]
    builder_name, specs = args[0], args[1:]
    nodes = [Node(s) for s in specs]
    builder = next(n for n in nodes if n.name == builder_name)
    timeout = float(opt("--timeout", "1800"))
    log = []
    heads = {n.name: n.head()["hash"] for n in nodes}
    if len(set(heads.values())) != 1: sys.exit(f"the nodes do not share a head: {heads}")
    for name in opt("--scenarios", "late,flip,burst,deep").split(","):
        scenario(name, builder, nodes, timeout, log)
    print("== result", flush=True)
    for node in nodes:
        head = node.head()
        print(f"  {node.name:12s} head #{int(head['number'], 16)} {head['hash'][:10]} root {head['stateRoot'][:10]}"
              + (f" STUCK since '{node.stuck}'" if node.stuck else ""), flush=True)
    report = opt("--report", None)
    if report:
        with open(report, "w") as f: json.dump(log, f, indent=1)


if __name__ == "__main__":
    main()
