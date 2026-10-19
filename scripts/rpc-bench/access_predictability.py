#!/usr/bin/env python3
"""Measure how predictable the state an eth_call reads is, before the call runs.

Each corpus record is replayed as debug_traceCall with the prestateTracer, which lists every account and storage
slot the call touched. A predictor then runs over the records in corpus order, as a node would see them, and learns
per (contract, selector):

- static items: accounts and slots that earlier calls with the same key read;
- derived slots: mapping slots computed from the call data, keccak(word || base) and keccak(base || word) with a small
  base slot, a nested keccak(word2 || keccak(word1 || base)), and up to three slots past each.

For every record it counts the reads the predictor names before the call (recall) and the predicted reads the call
did not make (waste). Only these counts leave the runner: no address, slot, selector or call data is written.
"""

from __future__ import annotations

import argparse
import http.client
import json
import os
import sys
import time
import urllib.parse
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

BASE_SLOTS = 64
MAX_OFFSET = 3
MAX_WORDS = 16
MAX_DERIVATION_SLOTS = 64
TIMEOUT_SECONDS = 120

# ---------------------------------------------------------------- keccak-256


def _load_keccak():
    try:
        from Crypto.Hash import keccak as crypto_keccak  # type: ignore

        return lambda data: crypto_keccak.new(digest_bits=256, data=data).digest(), "pycryptodome"
    except ImportError:
        pass
    try:
        import sha3  # type: ignore

        return lambda data: sha3.keccak_256(data).digest(), "pysha3"
    except ImportError:
        pass
    return _keccak256, "python"


_ROUND_CONSTANTS = [
    0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B,
    0x0000000080000001, 0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088,
    0x0000000080008009, 0x000000008000000A, 0x000000008000808B, 0x800000000000008B, 0x8000000000008089,
    0x8000000000008003, 0x8000000000008002, 0x8000000000000080, 0x000000000000800A, 0x800000008000000A,
    0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
]
_ROTATIONS = [0, 1, 62, 28, 27, 36, 44, 6, 55, 20, 3, 10, 43, 25, 39, 41, 45, 15, 21, 8, 18, 2, 61, 56, 14]
_MASK = (1 << 64) - 1


def _keccak_f(state: list[int]) -> None:
    for constant in _ROUND_CONSTANTS:
        c = [state[x] ^ state[x + 5] ^ state[x + 10] ^ state[x + 15] ^ state[x + 20] for x in range(5)]
        d = [c[(x - 1) % 5] ^ (((c[(x + 1) % 5] << 1) | (c[(x + 1) % 5] >> 63)) & _MASK) for x in range(5)]
        for i in range(25):
            state[i] ^= d[i % 5]
        b = [0] * 25
        for x in range(5):
            for y in range(5):
                i = x + 5 * y
                r = _ROTATIONS[i]
                value = state[i]
                b[y + 5 * ((2 * x + 3 * y) % 5)] = ((value << r) | (value >> (64 - r))) & _MASK if r else value
        for y in range(0, 25, 5):
            row = b[y:y + 5]
            for x in range(5):
                state[y + x] = row[x] ^ ((~row[(x + 1) % 5]) & row[(x + 2) % 5])
        state[0] ^= constant


def _keccak256(data: bytes) -> bytes:
    rate = 136
    padded = bytearray(data)
    padded.append(0x01)
    padded.extend(b"\x00" * ((-len(padded)) % rate))
    padded[-1] |= 0x80
    state = [0] * 25
    for offset in range(0, len(padded), rate):
        block = padded[offset:offset + rate]
        for i in range(rate // 8):
            state[i] ^= int.from_bytes(block[8 * i:8 * i + 8], "little")
        _keccak_f(state)
    return b"".join(state[i].to_bytes(8, "little") for i in range(4))


KECCAK, KECCAK_SOURCE = _load_keccak()

# ---------------------------------------------------------------- predictor


def _word(value: int) -> bytes:
    return value.to_bytes(32, "big")


_BASES = [_word(base) for base in range(BASE_SLOTS)]


def call_words(tx: dict) -> list[bytes]:
    """The 32-byte values a mapping key can come from: the call data arguments, the sender and the target."""
    words: list[bytes] = []
    data = tx.get("data") or tx.get("input") or "0x"
    raw = bytes.fromhex(data[2:] if data.startswith("0x") else data) if len(data) > 2 else b""
    arguments = raw[4:]
    for offset in range(0, min(len(arguments), 32 * MAX_WORDS), 32):
        chunk = arguments[offset:offset + 32]
        if len(chunk) == 32:
            words.append(chunk)
    for field in ("from", "to"):
        address = tx.get(field)
        if isinstance(address, str) and len(address) == 42:
            words.append(b"\x00" * 12 + bytes.fromhex(address[2:]))
    seen: set[bytes] = set()
    return [w for w in words if not (w in seen or seen.add(w))]


def _is_address_word(word: bytes) -> bool:
    return word[:12] == b"\x00" * 12 and word[12:] != b"\x00" * 20


class HashCache:
    """keccak results by preimage, kept across records: senders and tokens repeat."""

    def __init__(self, limit: int = 2_000_000) -> None:
        self._values: dict[bytes, int] = {}
        self._limit = limit
        self.computed = 0

    def __call__(self, preimage: bytes) -> int:
        value = self._values.get(preimage)
        if value is None:
            value = int.from_bytes(KECCAK(preimage), "big")
            self.computed += 1
            if len(self._values) >= self._limit:
                self._values.clear()
            self._values[preimage] = value
        return value


# A pattern names a slot by how it is computed from the call words: (kind, word index, base, second index, offset).
SINGLE, SINGLE_REVERSED, NESTED = 0, 1, 2


def evaluate(pattern: tuple, words: list[bytes], keccak: HashCache) -> int | None:
    kind, first, base, second, offset = pattern
    if first >= len(words) or (kind == NESTED and second >= len(words)):
        return None
    if kind == SINGLE:
        value = keccak(words[first] + _BASES[base])
    elif kind == SINGLE_REVERSED:
        value = keccak(_BASES[base] + words[first])
    else:
        value = keccak(words[second] + _word(keccak(words[first] + _BASES[base])))
    return (value + offset) & ((1 << 256) - 1)


# Without a native keccak the full search costs seconds per record; the reduced one keeps address keys only.
REDUCED_SEARCH = KECCAK_SOURCE == "python"
REDUCED_BASE_SLOTS = 16


def derive(slots: set[int], words: list[bytes], keccak: HashCache) -> dict[int, tuple]:
    """Patterns that compute the given slots from the call words."""
    found: dict[int, tuple] = {}
    if not slots or not words:
        return found
    wanted = {slot - offset: (slot, offset) for slot in slots for offset in range(MAX_OFFSET + 1)}
    single: list[tuple[int, int, int]] = []
    bases = REDUCED_BASE_SLOTS if REDUCED_SEARCH else BASE_SLOTS
    for index, word in enumerate(words):
        if REDUCED_SEARCH and not _is_address_word(word):
            continue
        for base in range(bases):
            for kind, preimage in ((SINGLE, word + _BASES[base]), (SINGLE_REVERSED, _BASES[base] + word)):
                value = keccak(preimage)
                if kind == SINGLE:
                    single.append((index, base, value))
                hit = wanted.get(value)
                if hit and hit[0] not in found:
                    found[hit[0]] = (kind, index, base, 0, hit[1])
    remaining = slots - found.keys()
    if remaining:
        wanted = {slot - offset: (slot, offset) for slot in remaining for offset in range(MAX_OFFSET + 1)}
        address_indexes = [i for i, w in enumerate(words) if _is_address_word(w)]
        for first, base, inner in single:
            if first not in address_indexes:
                continue
            inner_word = _word(inner)
            for second in address_indexes:
                hit = wanted.get(keccak(words[second] + inner_word))
                if hit and hit[0] not in found:
                    found[hit[0]] = (NESTED, first, base, second, hit[1])
    return found


class Model:
    def __init__(self) -> None:
        self.calls: dict[tuple, int] = defaultdict(int)
        self.seen: dict[tuple, dict[tuple, int]] = defaultdict(lambda: defaultdict(int))
        self.patterns: dict[tuple, set[tuple]] = defaultdict(set)


def _hex_int(value: str) -> int:
    return int(value, 16)


def accessed(prestate: dict) -> tuple[set[str], set[tuple[str, int]], int]:
    accounts: set[str] = set()
    slots: set[tuple[str, int]] = set()
    code = 0
    for address, account in prestate.items():
        address = address.lower()
        accounts.add(address)
        if isinstance(account, dict):
            if account.get("code") not in (None, "", "0x"):
                code += 1
            for slot in (account.get("storage") or {}):
                slots.add((address, _hex_int(slot)))
    return accounts, slots, code


class Totals:
    def __init__(self) -> None:
        self.records = 0
        self.warm_records = 0
        self.accounts = 0
        self.slots = 0
        self.code_accounts = 0
        self.warm = {name: 0 for name in (
            "accounts", "slots", "accounts_trivial", "accounts_static", "slots_static", "slots_derived",
            "slots_static_majority", "accounts_static_majority", "predicted_accounts", "predicted_slots",
            "predicted_unused_accounts", "predicted_unused_slots", "slots_unexplained")}
        self.recall_per_record: list[float] = []
        self.patterns_learned = 0


def score(model: Model, totals: Totals, tx: dict, prestate: dict, keccak: HashCache) -> None:
    to = (tx.get("to") or "").lower()
    data = tx.get("data") or tx.get("input") or "0x"
    key = (to, data[:10])
    sender = (tx.get("from") or "").lower()
    words = call_words(tx)
    accounts, slots, code = accessed(prestate)

    totals.records += 1
    totals.accounts += len(accounts)
    totals.slots += len(slots)
    totals.code_accounts += code

    calls = model.calls[key]
    seen = model.seen[key]
    if calls:
        totals.warm_records += 1
        w = totals.warm
        static = {item for item in seen}
        majority = {item for item, count in seen.items() if 2 * count >= calls}
        derived: set[tuple] = set()
        for address, pattern in model.patterns[key]:
            slot = evaluate(pattern, words, keccak)
            if slot is not None:
                derived.add(("s", address, slot))
        trivial = {("a", a) for a in (to, sender) if a}
        actual = {("a", a) for a in accounts} | {("s", a, s) for a, s in slots}
        predicted = static | derived | trivial

        w["accounts"] += len(accounts)
        w["slots"] += len(slots)
        w["accounts_trivial"] += sum(1 for a in accounts if ("a", a) in trivial)
        w["accounts_static"] += sum(1 for a in accounts if ("a", a) in static or ("a", a) in trivial)
        w["accounts_static_majority"] += sum(1 for a in accounts if ("a", a) in majority or ("a", a) in trivial)
        w["slots_static"] += sum(1 for a, s in slots if ("s", a, s) in static)
        w["slots_static_majority"] += sum(1 for a, s in slots if ("s", a, s) in majority)
        w["slots_derived"] += sum(1 for a, s in slots if ("s", a, s) in derived and ("s", a, s) not in static)
        predicted_accounts = {p for p in predicted if p[0] == "a"}
        predicted_slots = predicted - predicted_accounts
        w["predicted_accounts"] += len(predicted_accounts)
        w["predicted_slots"] += len(predicted_slots)
        w["predicted_unused_accounts"] += len(predicted_accounts - actual)
        w["predicted_unused_slots"] += len(predicted_slots - actual)
        hits = len(actual & predicted)
        totals.recall_per_record.append(hits / len(actual) if actual else 1.0)
        unexplained = {s for a, s in slots if ("s", a, s) not in static and ("s", a, s) not in derived}
        w["slots_unexplained"] += len(unexplained)

    # Learn after scoring, as the node would: slots no earlier call explained get a derivation search.
    model.calls[key] = calls + 1
    for a in accounts:
        seen[("a", a)] += 1
    for a, s in slots:
        seen[("s", a, s)] += 1
    known = {(address, pattern) for address, pattern in model.patterns[key]}
    candidates: dict[str, set[int]] = defaultdict(set)
    for a, s in slots:
        if seen[("s", a, s)] == 1:
            candidates[a].add(s)
    budget = MAX_DERIVATION_SLOTS
    for address, address_slots in candidates.items():
        if budget <= 0:
            break
        chosen = set(sorted(address_slots)[:budget])
        budget -= len(chosen)
        for pattern in derive(chosen, words, keccak).values():
            if (address, pattern) not in known:
                model.patterns[key].add((address, pattern))
                known.add((address, pattern))
                totals.patterns_learned += 1


def _quantile(values: list[float], q: float) -> float:
    if not values:
        return 0.0
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(q * (len(ordered) - 1) + 0.5))]


def report(totals: Totals, model: Model, failed: int, elapsed: float, keccak: HashCache) -> dict:
    w = totals.warm

    def ratio(numerator: int, denominator: int) -> float:
        return round(numerator / denominator, 6) if denominator else 0.0

    reads = w["accounts"] + w["slots"]
    predicted_reads_hit = w["accounts_static"] + w["slots_static"] + w["slots_derived"]
    predicted = w["predicted_accounts"] + w["predicted_slots"]
    return {
        "schema": 1,
        "records": totals.records,
        "records_failed": failed,
        "keys": len(model.calls),
        "warm_records": totals.warm_records,
        "warm_record_share": ratio(totals.warm_records, totals.records),
        "accounts": totals.accounts,
        "slots": totals.slots,
        "code_accounts": totals.code_accounts,
        "warm": {
            "reads": reads,
            "accounts": w["accounts"],
            "slots": w["slots"],
            "recall_reads": ratio(predicted_reads_hit, reads),
            "recall_accounts": ratio(w["accounts_static"], w["accounts"]),
            "recall_accounts_trivial_only": ratio(w["accounts_trivial"], w["accounts"]),
            "recall_slots_static": ratio(w["slots_static"], w["slots"]),
            "recall_slots_static_and_derived": ratio(w["slots_static"] + w["slots_derived"], w["slots"]),
            "recall_slots_majority": ratio(w["slots_static_majority"], w["slots"]),
            "recall_accounts_majority": ratio(w["accounts_static_majority"], w["accounts"]),
            "slots_unexplained_share": ratio(w["slots_unexplained"], w["slots"]),
            "waste_share": ratio(w["predicted_unused_accounts"] + w["predicted_unused_slots"], predicted),
            "predicted_per_record": ratio(predicted, totals.warm_records),
            "reads_per_record": ratio(reads, totals.warm_records),
            "recall_per_record_p10": round(_quantile(totals.recall_per_record, 0.10), 6),
            "recall_per_record_p50": round(_quantile(totals.recall_per_record, 0.50), 6),
            "recall_per_record_p90": round(_quantile(totals.recall_per_record, 0.90), 6),
        },
        "patterns_learned": totals.patterns_learned,
        "reduced_search": 1 if REDUCED_SEARCH else 0,
        "keccak_computed": keccak.computed,
        "elapsed_seconds": round(elapsed, 1),
    }


# ---------------------------------------------------------------- replay


FAILURE_REASONS = ("transport", "http_status", "rpc_error", "not_an_object")


def _trace(url: str, params: list) -> tuple[dict | None, str, int]:
    """The prestate of one call, or None with the failure reason and its code (an HTTP status or RPC error code)."""
    parsed = urllib.parse.urlsplit(url)
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": "debug_traceCall", "params": params},
                      separators=(",", ":")).encode()
    try:
        connection = http.client.HTTPConnection(parsed.hostname, parsed.port, timeout=TIMEOUT_SECONDS)
        connection.request("POST", parsed.path or "/", body=body, headers={"Content-Type": "application/json"})
        response = connection.getresponse()
        raw = response.read()
        connection.close()
        if response.status != 200:
            return None, "http_status", response.status
        payload = json.loads(raw)
    except (http.client.HTTPException, OSError, ValueError):
        return None, "transport", 0
    if isinstance(payload.get("error"), dict):
        code = payload["error"].get("code")
        return None, "rpc_error", code if isinstance(code, int) else 0
    result = payload.get("result")
    return (result, "", 0) if isinstance(result, dict) else (None, "not_an_object", 0)


def run(corpus: str, rpc_url: str, out: str, concurrency: int, limit: int | None) -> dict:
    os.environ["RPC_BENCH_CORPUS_METHOD"] = "debug_traceCall"
    os.environ["RPC_BENCH_CORPUS_TRACER"] = "prestateTracer"
    import corpus_parity  # noqa: E402  (reads the rewrite settings above)

    records = corpus_parity.load_corpus(corpus)
    if limit:
        records = records[:limit]
    # A node that does not serve debug_traceCall with this tracer fails every record the same way; stop at once.
    probe, reason, code = _trace(rpc_url, records[0])
    if probe is None and all(_trace(rpc_url, r)[0] is None for r in records[1:5]):
        raise SystemExit(f"debug_traceCall with the prestateTracer fails on the first records ({reason} {code}); "
                         "the node must serve the Debug module (node_config.jsonrpc_modules).")
    started = time.perf_counter()
    model, totals, keccak = Model(), Totals(), HashCache()
    failed = 0
    failures = {f"{reason}": 0 for reason in FAILURE_REASONS}
    failure_codes: dict[str, int] = defaultdict(int)
    batch = max(concurrency * 8, 64)
    with ThreadPoolExecutor(max_workers=concurrency) as pool:
        for start in range(0, len(records), batch):
            chunk = records[start:start + batch]
            for params, (prestate, reason, code) in zip(chunk, pool.map(lambda p: _trace(rpc_url, p), chunk)):
                if prestate is None:
                    failed += 1
                    failures[reason] += 1
                    failure_codes[f"{reason}_{code}"] += 1
                    continue
                score(model, totals, params[0], prestate, keccak)
            done = start + len(chunk)
            if done % (batch * 10) < batch:
                print(f"  access predictability: {done}/{len(records)} records", flush=True)
    result = report(totals, model, failed, time.perf_counter() - started, keccak)
    result["failures"] = failures
    # Codes only, the most frequent few: an HTTP status or a JSON-RPC error code carries no call data.
    for name, count in sorted(failure_codes.items(), key=lambda item: -item[1])[:4]:
        print(f"  access predictability: {count} failures with {name}", flush=True)
    Path(out).parent.mkdir(parents=True, exist_ok=True)
    Path(out).write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--corpus", required=True)
    parser.add_argument("--rpc-url", required=True)
    parser.add_argument("--out", required=True)
    parser.add_argument("--concurrency", type=int, default=8)
    parser.add_argument("--limit", type=int, default=None)
    arguments = parser.parse_args(argv)
    result = run(arguments.corpus, arguments.rpc_url, arguments.out, arguments.concurrency, arguments.limit)
    w = result["warm"]
    print(f"  access predictability: records {result['records']} (failed {result['records_failed']}), keys {result['keys']}, "
          f"warm share {result['warm_record_share']:.1%}, recall reads {w['recall_reads']:.1%}, "
          f"slots static {w['recall_slots_static']:.1%}, slots static+derived {w['recall_slots_static_and_derived']:.1%}, "
          f"waste {w['waste_share']:.1%}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
