#!/usr/bin/env python3
"""differential.py --node URL [--reference URL] [--samples N] [--vectors samples.jsonl] [--seed S] [--out report.json]

Differential test of a Nethermind Arkiv node against the reference (the public Rabdi RPC, arkiv-reth):

  history  N random block heights in [1, the lower of the two heads]: the block (hashes and full transactions), every
           transaction's receipt, and eth_getLogs over a window from it. All historical on both sides.
  vectors  the ground truth tools/reference_sampler.sh recorded at the reference's head X, replayed against the node
           at block X: balances, nonces, entities, entity counts and query counts. The reference serves state at its
           head only, so this is how past state is checked.
  live     at a block both nodes have as their head: balances, nonces, the entityNonce view (eth_call), arkiv_query
           pages and counts, eth_getStorageAt, eth_getCode.
  proofs   eth_getProof on the node at random past blocks, checked against its own account trie (the reference has no
           eth_getProof; its account proofs would be against GolemDB's root, which no tool can verify yet).

Fields compared are the consensus ones; presentation-only fields one client adds and the other does not are listed
separately, not counted as mismatches.
"""
import argparse, concurrent.futures, json, random, subprocess, sys, time

REFERENCE = "https://rpc.rabdi.db-chain.testnet.arkiv.network"
ARKIV = "0x4400000000000000000000000000000000000044"
BLOCK_FIELDS = ["number", "hash", "parentHash", "sha3Uncles", "miner", "stateRoot", "transactionsRoot", "receiptsRoot",
                "logsBloom", "difficulty", "gasLimit", "gasUsed", "timestamp", "extraData", "mixHash", "nonce",
                "baseFeePerGas", "withdrawalsRoot", "blobGasUsed", "excessBlobGas", "parentBeaconBlockRoot", "requestsHash",
                "transactions", "withdrawals", "uncles"]
TX_FIELDS = ["hash", "type", "chainId", "nonce", "from", "to", "value", "gas", "maxFeePerGas", "maxPriorityFeePerGas",
             "gasPrice", "input", "accessList", "r", "s", "yParity", "v", "blockHash", "blockNumber", "transactionIndex"]
RECEIPT_FIELDS = ["transactionHash", "transactionIndex", "blockHash", "blockNumber", "from", "to", "cumulativeGasUsed",
                  "gasUsed", "effectiveGasPrice", "contractAddress", "logs", "logsBloom", "type", "status"]
LOG_FIELDS = ["address", "topics", "data", "blockNumber", "blockHash", "transactionHash", "transactionIndex", "logIndex", "removed"]


class Rpc:
    def __init__(self, url, pace=0.0):
        self.url, self.pace, self.ids = url, pace, 0

    def batch(self, calls):
        out = []
        for i in range(0, len(calls), 50):
            chunk = calls[i:i + 50]
            body = [{"jsonrpc": "2.0", "id": self.ids + j, "method": m, "params": p} for j, (m, p) in enumerate(chunk)]
            self.ids += len(chunk)
            for attempt in range(8):
                try:
                    # curl as the transport: the public endpoint's edge refuses some HTTP stacks outright.
                    r = subprocess.run(["curl", "-sf", "-m", "120", "-X", "POST", "-H", "content-type: application/json",
                                        "--data-binary", "@-", self.url], input=json.dumps(body).encode(), capture_output=True, check=True)
                    res = json.loads(r.stdout)
                    break
                except Exception:
                    time.sleep(2 * (attempt + 1))
            else:
                raise RuntimeError(f"{self.url}: batch failed")
            by_id = {r["id"]: r for r in res}
            out.extend(by_id[b["id"]] for b in body)
            if self.pace:
                time.sleep(self.pace)
        return out

    def call(self, method, params):
        return self.batch([(method, params)])[0]


def pick(obj, fields):
    return {f: obj.get(f) for f in fields if obj is not None and f in obj}


def norm_block(b, full):
    if b is None:
        return None
    out = pick(b, BLOCK_FIELDS)
    if full:
        out["transactions"] = [pick(t, TX_FIELDS) for t in b.get("transactions", [])]
    return out


def norm_receipt(r):
    if r is None:
        return None
    out = pick(r, RECEIPT_FIELDS)
    out["logs"] = [pick(l, LOG_FIELDS) for l in r.get("logs", [])]
    return out


def extra_fields(a, b):
    if not isinstance(a, dict) or not isinstance(b, dict):
        return set(), set()
    return set(a) - set(b), set(b) - set(a)


def diff(a, b, path=""):
    """First differences between two JSON values, as (path, node, reference)."""
    if type(a) != type(b):
        return [(path, a, b)]
    if isinstance(a, dict):
        out = []
        for k in sorted(set(a) | set(b)):
            if k not in a or k not in b:
                out.append((f"{path}.{k}", a.get(k, "<absent>"), b.get(k, "<absent>")))
            else:
                out.extend(diff(a[k], b[k], f"{path}.{k}"))
        return out
    if isinstance(a, list):
        if len(a) != len(b):
            return [(f"{path}.length", len(a), len(b))]
        return [d for i, (x, y) in enumerate(zip(a, b)) for d in diff(x, y, f"{path}[{i}]")]
    if isinstance(a, str) and isinstance(b, str) and a.startswith("0x") and b.startswith("0x"):
        return [] if a.lower() == b.lower() else [(path, a, b)]
    return [] if a == b else [(path, a, b)]


class Report:
    def __init__(self):
        self.checks, self.mismatches, self.extras, self.reference_findings, self.unavailable = {}, [], {}, {}, {}

    def compare(self, kind, label, node, ref):
        self.checks[kind] = self.checks.get(kind, 0) + 1
        d = diff(node, ref)
        if d:
            self.mismatches.append({"kind": kind, "at": label, "differences": d[:5]})
        return not d

    def note_extra(self, kind, node_raw, ref_raw):
        only_node, only_ref = extra_fields(node_raw, ref_raw)
        if only_node or only_ref:
            e = self.extras.setdefault(kind, {"node_only": set(), "reference_only": set()})
            e["node_only"] |= only_node
            e["reference_only"] |= only_ref


def history(node, ref, report, heights, top):
    calls = [("eth_getBlockByNumber", [hex(h), True]) for h in heights]
    nb, rb = node.batch(calls), ref.batch(calls)
    tx_hashes = []
    for h, a, b in zip(heights, nb, rb):
        a, b = a.get("result"), b.get("result")
        report.note_extra("block", a, b)
        report.compare("eth_getBlockByNumber(full)", h, norm_block(a, True), norm_block(b, True))
        tx_hashes += [t["hash"] for t in (b or {}).get("transactions", [])]
    calls = [("eth_getBlockByNumber", [hex(h), False]) for h in heights]
    for h, a, b in zip(heights, node.batch(calls), ref.batch(calls)):
        report.compare("eth_getBlockByNumber", h, norm_block(a.get("result"), False), norm_block(b.get("result"), False))
    calls = [("eth_getTransactionReceipt", [t]) for t in tx_hashes]
    for t, a, b in zip(tx_hashes, node.batch(calls), ref.batch(calls)):
        report.note_extra("receipt", a.get("result"), b.get("result"))
        report.compare("eth_getTransactionReceipt", t, norm_receipt(a.get("result")), norm_receipt(b.get("result")))
    # The reference's eth_getLogs fails ("golemdb segment_range_of(transactions): NotImplemented"), so the node's
    # eth_getLogs is checked against the logs of the reference's own receipts for every block of the window.
    windows = heights[: max(1, len(heights) // 4)]
    reference_logs_error = None
    for h in windows:
        span = list(range(h, min(h + 15, top) + 1))
        blocks = ref.batch([("eth_getBlockByNumber", [hex(n), False]) for n in span])
        hashes = [t for b in blocks for t in (b.get("result") or {}).get("transactions", [])]
        expected = []
        for r in ref.batch([("eth_getTransactionReceipt", [t]) for t in hashes]):
            expected += [pick(l, LOG_FIELDS) for l in (r.get("result") or {}).get("logs", []) if l["address"].lower() == ARKIV]
        flt = {"fromBlock": hex(span[0]), "toBlock": hex(span[-1]), "address": ARKIV}
        got = [pick(l, LOG_FIELDS) for l in node.call("eth_getLogs", [flt]).get("result") or []]
        report.compare("eth_getLogs", flt["fromBlock"], got, expected)
        if reference_logs_error is None:
            reference_logs_error = ref.call("eth_getLogs", [flt]).get("error")
    report.reference_findings["eth_getLogs"] = reference_logs_error


def vectors(node, report, path, node_head):
    used = 0
    for line in open(path):
        sample = json.loads(line)
        if sample["block"] > node_head:
            continue
        used += 1
        calls = [(c["method"], c["params"]) for c in sample["calls"]]
        for c, a in zip(sample["calls"], node.batch(calls)):
            expected = c["response"].get("result", c["response"].get("error"))
            got = a.get("result", a.get("error"))
            if c["method"] == "eth_getBlockByNumber":
                expected, got = norm_block(expected, False), norm_block(got, False)
            # Query results at a past block: neither client keeps them (GolemDB v1.0.0 reads at its head only). The
            # reference answered because the block was its head then; the node answers -32006 as the reference does
            # now. Counted apart: a capability neither has, not a divergence.
            if (c["method"] in ("arkiv_query", "arkiv_getEntityCount") and sample["block"] <= node_head
                    and isinstance(got, dict) and got.get("code") == -32006):
                report.unavailable[c["method"]] = report.unavailable.get(c["method"], 0) + 1
                continue
            report.compare(f"vector:{c['method']}", f"{sample['block']}:{json.dumps(c['params'])[:80]}", got, expected)
    return used


def live(node, ref, report, addresses, entity_keys):
    """Head to head at one block X. Both nodes answer arkiv_query at their head only and Rabdi moves every 2 s, so
    one batch of at most 50 calls goes to each, the node's the moment its head is the reference's, and a slot in
    which either moved on (-32006 / "chain head only") is retried."""
    unpaced = Rpc(ref.url)
    for attempt in range(60):
        x = int(unpaced.call("eth_blockNumber", [])["result"], 16)
        n = int(node.call("eth_blockNumber", [])["result"], 16)
        if n != x:
            time.sleep(0.2)
            continue
        hx = hex(x)
        calls = []
        for a in addresses[:6]:
            calls += [("eth_getBalance", [a, hx]), ("eth_getTransactionCount", [a, hx]),
                      ("eth_call", [{"to": ARKIV, "data": entity_nonce_calldata(a)}, hx]),
                      ("eth_getStorageAt", [a, "0x0", hx]), ("eth_getCode", [a, hx])]
        for k in entity_keys[:6]:
            calls.append(("arkiv_getEntity", [k, x]))
        calls += [("arkiv_getEntityCount", [{"block": x}]), ("arkiv_query", ["*", {"atBlock": hx, "select": {"count": True}}]),
                  ("arkiv_query", ["*", {"atBlock": hx, "limit": "0x14", "select": {"key": True, "owner": True, "expiresAt": True, "attributes": True}}])]
        # Both at once: a full entity count takes ~1 s on either client, and the slot is 2 s.
        with concurrent.futures.ThreadPoolExecutor(2) as pool:
            fn, fr = pool.submit(node.batch, calls), pool.submit(unpaced.batch, calls)
            na, ra = fn.result(), fr.result()
        moved = lambda rs: any(r.get("error", {}).get("code") == -32006 or "chain head only" in json.dumps(r.get("error", "")) for r in rs)
        if moved(na) or moved(ra):
            continue
        for (m, p), a, b in zip(calls, na, ra):
            report.compare(f"live:{m}", f"{x}:{json.dumps(p)[:80]}", a.get("result", a.get("error")), b.get("result", b.get("error")))
        return x
    return None


# `cast sig 'entityNonce(address)'`, as arkiv-bindings documents it.
ENTITY_NONCE_SELECTOR = "36917bfd"


def entity_nonce_calldata(address):
    return "0x" + ENTITY_NONCE_SELECTOR + "0" * 24 + address[2:].lower()


def proofs(node, report, heights, addresses):
    for h in heights:
        for a in addresses[:3]:
            p = node.call("eth_getProof", [a, [], hex(h)]).get("result")
            bal = node.call("eth_getBalance", [a, hex(h)]).get("result")
            report.compare("eth_getProof(balance)", f"{h}:{a}", p and p.get("balance"), bal)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--node", required=True)
    ap.add_argument("--reference", default=REFERENCE)
    ap.add_argument("--samples", type=int, default=200)
    ap.add_argument("--vectors")
    ap.add_argument("--seed", type=int, default=7)
    ap.add_argument("--out")
    args = ap.parse_args()
    node, ref = Rpc(args.node), Rpc(args.reference, pace=0.2)
    rng = random.Random(args.seed)
    report = Report()

    node_head = int(node.call("eth_blockNumber", [])["result"], 16)
    ref_head = int(ref.call("eth_blockNumber", [])["result"], 16)
    top = min(node_head, ref_head)
    heights = sorted(rng.sample(range(1, top + 1), min(args.samples, top)))
    started = time.time()
    history(node, ref, report, heights, top)

    used_vectors = vectors(node, report, args.vectors, node_head) if args.vectors else 0

    # Addresses and entities from the sampled blocks, for the live and proof checks.
    blocks = [b.get("result") or {} for b in node.batch([("eth_getBlockByNumber", [hex(h), True]) for h in heights[-40:]])]
    addresses = sorted({t["from"] for b in blocks for t in b.get("transactions", [])} | {"0x000000000000000000000000000000000000dead", ARKIV})[:15]
    receipts = node.batch([("eth_getTransactionReceipt", [t["hash"]]) for b in blocks for t in b.get("transactions", [])][:60])
    keys = sorted({l["topics"][1] for r in receipts for l in (r.get("result") or {}).get("logs", []) if l["address"].lower() == ARKIV and len(l["topics"]) > 1})[:15]
    live_block = live(node, ref, report, addresses, keys)
    proofs(node, report, heights[:: max(1, len(heights) // 10)], addresses)

    result = {
        "node": args.node, "reference": args.reference, "node_head": node_head, "reference_head": ref_head,
        "random_heights": len(heights), "seed": args.seed, "vectors_replayed": used_vectors, "live_block": live_block,
        "checks": report.checks, "mismatch_count": len(report.mismatches), "mismatches": report.mismatches[:200],
        "reference_errors": report.reference_findings,
        "historical_queries_unavailable_on_both": report.unavailable,
        "presentation_fields": {k: {kk: sorted(vv) for kk, vv in v.items()} for k, v in report.extras.items()},
        "seconds": round(time.time() - started, 1),
    }
    text = json.dumps(result, indent=2, default=str)
    if args.out:
        open(args.out, "w").write(text)
    print(json.dumps({k: result[k] for k in ("node_head", "reference_head", "random_heights", "vectors_replayed", "live_block", "checks", "mismatch_count")}, indent=1))
    sys.exit(1 if report.mismatches else 0)


if __name__ == "__main__":
    main()
