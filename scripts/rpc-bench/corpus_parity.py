#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Replay a private eth_call corpus against a node and compare clients by response bytes.

Privacy contract: request and response contents never appear in output — errors and reports carry
only record indexes, counts, and category names. The baseline state file (response hex strings) is
written to VM-local scratch and must not be artifacted.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import gzip
import http.client
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import zlib
from pathlib import Path
from typing import Sequence


def _env_int(name: str, default: int) -> int:
    try:
        value = int(os.environ.get(name, ""))
    except ValueError:
        return default
    return value if value > 0 else default


MAX_CORPUS_RECORDS = _env_int("RPC_BENCH_MAX_CORPUS_RECORDS", 10000)
MAX_RESPONSE_BYTES = 16 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 120
MAX_DIVERGENCE_INDEXES = _env_int("RPC_BENCH_MAX_DIVERGENCE_INDEXES", 200)
REPLAY_CONCURRENCY = _env_int("RPC_BENCH_PARITY_CONCURRENCY", 16)
RETRYABLE_CATEGORIES = frozenset({"transport_failure", "invalid_response"})
MAX_DIFF_WORDS = 8
PROGRESS_EVERY = 2000
ERROR_MARKER = "!rpc_error"  # not valid hex, so it can never collide with a result

# "matched" = identical result bytes; "both_rpc_errors" = both clients reject the call (also agreement).
PARITY_COUNTER_FIELDS = (
    "total", "matched", "both_rpc_errors", "baseline_rpc_errors", "candidate_rpc_errors",
    "candidate_transport_failures", "candidate_invalid_responses", "baseline_shorter",
    "candidate_shorter", "length_mismatches", "content_mismatches",
)
PARITY_LABEL_FIELDS = ("baseline_client", "candidate_client")


class CorpusParityError(Exception):
    """Content-free failure of a replay."""


def _reject_non_json_constant(value: str) -> None:
    raise ValueError(f"invalid JSON constant {value!r}")


def load_corpus(path: str | Path) -> list[list]:
    """Return the params of each corpus record, in file order."""
    path = Path(path)
    load_started = time.perf_counter()
    if not (path.name.endswith(".jsonl") or path.name.endswith(".jsonl.gz")):
        raise CorpusParityError("corpus must have a .jsonl or .jsonl.gz extension")
    opener = gzip.open if path.name.endswith(".gz") else open
    params: list[list] = []
    try:
        with opener(path, "rt", encoding="utf-8") as source:
            for number, line in enumerate(source, start=1):
                if not line.strip():
                    continue
                if len(params) >= MAX_CORPUS_RECORDS:
                    raise CorpusParityError(f"corpus exceeds {MAX_CORPUS_RECORDS} records")
                try:
                    record = json.loads(line, parse_constant=_reject_non_json_constant)
                except (ValueError, RecursionError):
                    raise CorpusParityError(f"corpus line {number}: invalid JSON") from None
                if not isinstance(record, dict) or record.get("method") != "eth_call" \
                        or not isinstance(record.get("params"), list):
                    raise CorpusParityError(f"corpus line {number}: not an eth_call record")
                params.append(record["params"])
    except (OSError, EOFError, UnicodeError, zlib.error) as error:
        raise CorpusParityError(f"cannot read corpus: {error.__class__.__name__}") from None
    if not params:
        raise CorpusParityError("corpus contains no records")
    took = time.perf_counter() - load_started
    if took > 5:
        print(f"  loaded {len(params)} records in {took:.0f}s", flush=True)
    return params


def _rpc(url: str, method: str, params: list):
    body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": params}).encode()
    request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            return json.loads(response.read(1 << 20))["result"]
    except (urllib.error.URLError, OSError, ValueError, KeyError, TypeError):
        raise CorpusParityError(f"cannot read {method} from the node") from None


def _node_identity(url: str) -> tuple[int, int, str]:
    """(head number, chain id, head hash) — the hash pins the exact state a replay ran against."""
    chain_id = _rpc(url, "eth_chainId", [])
    header = _rpc(url, "eth_getBlockByNumber", ["latest", False])
    try:
        head = int(header["number"], 16)
        block_hash = str(header["hash"]).lower()
        int(chain_id, 16)
    except (KeyError, TypeError, ValueError, AttributeError):
        raise CorpusParityError("node returned an unusable head header") from None
    return head, int(chain_id, 16), block_hash


_CONNECTIONS = threading.local()  # one keep-alive connection per worker thread


def _release_connection() -> None:
    entry = getattr(_CONNECTIONS, "entry", None)
    if entry is not None:
        try:
            entry[1].close()
        except Exception:  # noqa: BLE001
            pass
        _CONNECTIONS.entry = None


def _fetch(url: str, body: bytes) -> tuple[int, bytes] | None:
    """POST body and return (status, raw), or None on transport failure. Retries once for a closed pooled connection."""
    parsed = urllib.parse.urlsplit(url)
    key = (parsed.scheme, parsed.hostname, parsed.port)
    path = parsed.path or "/"
    for attempt in (0, 1):
        entry = getattr(_CONNECTIONS, "entry", None)
        if entry is None or entry[0] != key:
            _release_connection()
            factory = http.client.HTTPSConnection if parsed.scheme == "https" else http.client.HTTPConnection
            _CONNECTIONS.entry = (key, factory(parsed.hostname, parsed.port, timeout=REQUEST_TIMEOUT_SECONDS))
        conn = _CONNECTIONS.entry[1]
        try:
            conn.request("POST", path, body=body, headers={"Content-Type": "application/json"})
            response = conn.getresponse()
            raw = response.read(MAX_RESPONSE_BYTES + 1)
            if len(raw) > MAX_RESPONSE_BYTES:
                _release_connection()  # unread bytes would corrupt the next request on this connection
            return response.status, raw
        except (http.client.HTTPException, OSError, ValueError):
            _release_connection()
            if attempt:
                return None
    return None


def _post(url: str, index: int, params: list) -> tuple[str | None, str]:
    """POST one eth_call; return (category, result_hex). category is None on success."""
    body = json.dumps({"jsonrpc": "2.0", "id": index, "method": "eth_call", "params": params},
                      separators=(",", ":")).encode()
    fetched = _fetch(url, body)
    if fetched is None:
        return "transport_failure", ""
    status, raw = fetched
    if status >= 400:
        try:
            envelope = json.loads(raw)
        except ValueError:
            return "transport_failure", ""
        if isinstance(envelope, dict) and "error" in envelope and envelope.get("id") in (index, None):
            return "rpc_error", ""
        return "transport_failure", ""
    if len(raw) > MAX_RESPONSE_BYTES:
        return "transport_failure", ""
    try:
        envelope = json.loads(raw)
    except (json.JSONDecodeError, UnicodeDecodeError):
        return "invalid_response", ""
    if not isinstance(envelope, dict) or envelope.get("id") != index:
        return "invalid_response", ""
    if "error" in envelope:
        error = envelope["error"]
        code = error.get("code") if isinstance(error, dict) else None
        return (f"rpc_error:{code}" if isinstance(code, int) else "rpc_error"), ""
    result = envelope.get("result")
    if not isinstance(result, str) or not result.startswith("0x") or len(result) % 2 != 0:
        return "invalid_response", ""
    try:
        bytes.fromhex(result[2:])
    except ValueError:
        return "invalid_response", ""
    return None, result.lower()


def _base_category(category: str | None) -> str | None:
    return category.split(":", 1)[0] if category else category


def _progress(done: int, total: int, started: float, what: str) -> None:
    if done % PROGRESS_EVERY and done != total:
        return
    elapsed = time.perf_counter() - started
    rate = done / elapsed if elapsed > 0 else 0.0
    eta = (total - done) / rate if rate > 0 else 0.0
    print(f"  {what} {done}/{total} ({done / total * 100:.0f}%) {rate:.0f} req/s, ~{eta / 60:.1f} min left", flush=True)


def _replay(rpc_url: str, params_list: list[list], what: str) -> list[tuple[str | None, str]]:
    """Replay every record concurrently; re-run transport/invalid outcomes serially so load artifacts are not counted as defects."""
    outcomes: list[tuple[str | None, str] | None] = [None] * len(params_list)
    started = time.perf_counter()
    done = 0
    lock = threading.Lock()

    def one(position: int) -> None:
        nonlocal done
        outcomes[position] = _post(rpc_url, position + 1, params_list[position])
        with lock:
            done += 1
            _progress(done, len(params_list), started, what)

    workers = max(1, min(REPLAY_CONCURRENCY, len(params_list)))
    with concurrent.futures.ThreadPoolExecutor(max_workers=workers) as pool:
        for _ in pool.map(one, range(len(params_list))):
            pass
    settled = [o if o is not None else ("transport_failure", "") for o in outcomes]
    suspect = [i for i, (category, _) in enumerate(settled) if _base_category(category) in RETRYABLE_CATEGORIES]
    if suspect:
        print(f"  {what}: re-running {len(suspect)} non-clean record(s) serially", flush=True)
        recovered = 0
        for position in suspect:
            retried = _post(rpc_url, position + 1, params_list[position])
            if _base_category(retried[0]) not in RETRYABLE_CATEGORIES:
                recovered += 1
            settled[position] = retried
        if recovered:
            print(f"  {what}: {recovered} recovered on retry (concurrency artifacts, not defects)", flush=True)
    return settled


def baseline(corpus: str, rpc_url: str, state_path: str) -> None:
    """Replay the corpus and store each outcome (result hex or ERROR_MARKER); transport/invalid outcomes abort."""
    params_list = load_corpus(corpus)
    head, chain_id, block_hash = _node_identity(rpc_url)
    results: list[str] = []
    failures: dict[str, int] = {}
    error_count = 0
    for category, result in _replay(rpc_url, params_list, "baseline"):
        if _base_category(category) == "rpc_error":
            error_count += 1
            results.append(ERROR_MARKER)
            continue
        if category is not None:
            failures[category] = failures.get(category, 0) + 1
        results.append(result)
    if failures:
        summary = " ".join(f"{key}={value}" for key, value in sorted(failures.items()))
        raise CorpusParityError(f"baseline replay had failures over {len(params_list)} records: {summary}")
    state = Path(state_path)
    state.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(state, "wt", encoding="utf-8") as output:
        json.dump({"total": len(results), "head": head, "chain_id": chain_id,
                   "block_hash": block_hash, "results": results}, output)
    print(f"baseline captured: {len(results)} outcomes ({error_count} rpc_error) at head {head}")
    if error_count:
        error_indexes = [str(i) for i, r in enumerate(results, start=1) if r == ERROR_MARKER]
        print(f"baseline rpc_error indexes (first {min(len(error_indexes), 40)}): {' '.join(error_indexes[:40])}")


def _describe_divergence(index: int, expected: str, actual: str) -> dict:
    """Word positions and direction only — a magnitude with a zero operand would be the other operand."""
    exp = expected[2:] if expected.startswith("0x") else expected
    act = actual[2:] if actual.startswith("0x") else actual
    entry: dict = {
        "index": index,
        "baseline_bytes": len(exp) // 2,
        "candidate_bytes": len(act) // 2,
        "baseline_all_zero": bool(exp) and set(exp) == {"0"},
        "candidate_all_zero": bool(act) and set(act) == {"0"},
    }
    words = []
    differing = 0
    for word in range(max(len(exp), len(act)) // 64 + 1):
        a, b = exp[word * 64:(word + 1) * 64], act[word * 64:(word + 1) * 64]
        if a == b or (not a and not b):
            continue
        differing += 1
        if len(words) >= MAX_DIFF_WORDS:
            continue
        item: dict = {"word": word}
        try:
            if a and b:
                item["direction"] = "higher" if int(b, 16) > int(a, 16) else "lower"
        except ValueError:
            pass
        words.append(item)
    entry["differing_words"] = words
    entry["total_differing_words"] = differing
    return entry


def compare(corpus: str, rpc_url: str, state_path: str, report_path: str,
            baseline_client: str, candidate_client: str, diffs_path: str | None = None) -> bool:
    """Replay the corpus against a candidate node and diff against the stored baseline."""
    params_list = load_corpus(corpus)
    try:
        with gzip.open(state_path, "rt", encoding="utf-8") as source:
            state = json.load(source)
        baseline_results = state["results"]
        baseline_head, baseline_chain = state["head"], state["chain_id"]
        baseline_hash = state.get("block_hash")
    except (OSError, json.JSONDecodeError, KeyError, TypeError):
        raise CorpusParityError("baseline state is missing or unreadable") from None
    if not isinstance(baseline_results, list) or len(baseline_results) != len(params_list):
        raise CorpusParityError(
            f"baseline state has {len(baseline_results)} results but the corpus has {len(params_list)}")
    head, chain_id, block_hash = _node_identity(rpc_url)
    if (head, chain_id) != (baseline_head, baseline_chain) or (baseline_hash is not None and block_hash != baseline_hash):
        raise CorpusParityError(
            f"node identity mismatch: baseline head={baseline_head} chain={baseline_chain} hash={baseline_hash} "
            f"vs candidate head={head} chain={chain_id} hash={block_hash} — align the snapshots before comparing")

    report = {field: 0 for field in PARITY_COUNTER_FIELDS}
    report["total"] = len(params_list)
    divergences: list[dict[str, int | str]] = []
    diff_records: list[dict] = []

    def diverge(index: int, kind: str) -> None:
        if len(divergences) < MAX_DIVERGENCE_INDEXES:
            divergences.append({"index": index, "kind": kind})

    replayed = _replay(rpc_url, params_list, "compare")
    disputed = [
        i for i, (category, actual) in enumerate(replayed)
        if not (category is None and actual == baseline_results[i])
        and not (_base_category(category) == "rpc_error" and baseline_results[i] == ERROR_MARKER)
    ]
    if disputed:
        print(f"  compare: re-verifying {len(disputed)} disagreement(s) serially", flush=True)
        settled = 0
        for position in disputed:
            retried = _post(rpc_url, position + 1, params_list[position])
            if retried != replayed[position]:
                settled += 1
            replayed[position] = retried
        if settled:
            print(f"  compare: {settled} disagreement(s) changed outcome on retry", flush=True)

    for index, (category, actual) in enumerate(replayed, start=1):
        expected = baseline_results[index - 1]
        base = _base_category(category)
        if base == "transport_failure":
            report["candidate_transport_failures"] += 1
            diverge(index, "candidate_transport_failure")
        elif base == "invalid_response":
            report["candidate_invalid_responses"] += 1
            diverge(index, "candidate_invalid_response")
        elif base == "rpc_error":
            if expected == ERROR_MARKER:
                report["both_rpc_errors"] += 1
            else:
                report["candidate_rpc_errors"] += 1
                diverge(index, "candidate_rpc_error")
        elif expected == ERROR_MARKER:
            report["baseline_rpc_errors"] += 1
            diverge(index, "baseline_rpc_error")
        elif actual == expected:
            report["matched"] += 1
        elif len(actual) != len(expected):
            if len(expected) < len(actual) and actual.startswith(expected):
                report["baseline_shorter"] += 1
                diverge(index, "baseline_shorter")
            elif len(actual) < len(expected) and expected.startswith(actual):
                report["candidate_shorter"] += 1
                diverge(index, "candidate_shorter")
            else:
                report["length_mismatches"] += 1
                diverge(index, "length_mismatch")
        else:
            report["content_mismatches"] += 1
            diverge(index, "content_mismatch")
            if diffs_path:
                diff_records.append(_describe_divergence(index, expected, actual))

    if diffs_path and diff_records:
        diffs_target = Path(diffs_path)
        diffs_target.parent.mkdir(parents=True, exist_ok=True)
        with diffs_target.open("w", encoding="utf-8") as handle:
            json.dump({"baseline_client": baseline_client, "candidate_client": candidate_client,
                       "total_divergences": report["content_mismatches"],
                       "recorded": len(diff_records), "diffs": diff_records}, handle)
        print(f"  wrote {len(diff_records)} divergence characterisations to {diffs_target.name}")

    document = {"baseline_client": baseline_client, "candidate_client": candidate_client,
                "divergences": divergences, **report}
    target = Path(report_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as output:
        json.dump(document, output, sort_keys=True, separators=(",", ":"))
        output.write("\n")
    clean = report["matched"] + report["both_rpc_errors"] == report["total"]
    defects = " ".join(f"{key}={value}" for key, value in report.items()
                       if value and key not in ("total", "matched", "both_rpc_errors"))
    agreement = f"{report['matched']}/{report['total']} matched"
    if report["both_rpc_errors"]:
        agreement += f" + {report['both_rpc_errors']} both-error"
    print(f"parity {candidate_client} vs {baseline_client}: {agreement}" + (f" ({defects})" if defects else ""))
    if divergences:
        preview = " ".join(str(d["index"]) for d in divergences[:40])
        print(f"divergent corpus indexes (first {min(len(divergences), 40)}): {preview}")
    return clean


def _timed_post(url: str, index: int, params: list) -> tuple[float, str]:
    started = time.perf_counter()
    try:
        category, _ = _post(url, index, params)
    except Exception:  # noqa: BLE001 — one bad record must not lose the matrix
        category = "transport_failure"
    return (time.perf_counter() - started) * 1000.0, category or "ok"


def timings(corpus: str, rpc_url: str, out_path: str, passes: int, rps: float, concurrency: int,
            warmup_seconds: int = 0, warmup_rps: float = 0.0) -> None:
    """Replay every record `passes` times in corpus order; write a record x pass matrix of ms + outcome, and a meta sidecar."""
    params_list = load_corpus(corpus)
    total_records = len(params_list)
    if passes < 1:
        raise CorpusParityError("passes must be >= 1")
    head, chain_id, block_hash = _node_identity(rpc_url)

    grid: list[list[float | None]] = [[None] * passes for _ in range(total_records)]
    status: list[list[str]] = [[""] * passes for _ in range(total_records)]
    outcomes: dict[str, int] = {}
    lock = threading.Lock()
    started_at = time.perf_counter()

    def run_one(order: int, record: int, current_pass: int) -> None:
        if rps > 0:
            delay = started_at + order / rps - time.perf_counter()
            if delay > 0:
                time.sleep(delay)
        elapsed, outcome = _timed_post(rpc_url, record + 1, params_list[record])
        with lock:
            grid[record][current_pass] = elapsed
            status[record][current_pass] = outcome
            outcomes[outcome] = outcomes.get(outcome, 0) + 1

    schedule = [(p * total_records + i, i, p) for p in range(passes) for i in range(total_records)]
    with concurrent.futures.ThreadPoolExecutor(max_workers=concurrency) as pool:
        for _ in pool.map(lambda a: run_one(*a), schedule):
            pass

    wall = time.perf_counter() - started_at
    issued = len(schedule)
    target = Path(out_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8", newline="") as handle:
        writer = csv.writer(handle)
        header = ["record_index"]
        for p in range(passes):
            header += [f"pass_{p + 1}_ms", f"pass_{p + 1}_status"]
        writer.writerow(header)
        for record in range(total_records):
            row: list[str] = [str(record + 1)]
            for p in range(passes):
                value = grid[record][p]
                row += ["" if value is None else f"{value:.3f}", status[record][p]]
            writer.writerow(row)

    achieved = issued / wall if wall > 0 else 0.0
    meta = {
        "head": head, "chain_id": chain_id, "block_hash": block_hash,
        "records": total_records, "passes": passes, "requests": issued,
        "target_rps": rps, "achieved_rps": round(achieved, 2), "concurrency": concurrency,
        "warmup_seconds": warmup_seconds, "warmup_rps": warmup_rps,
        "outcomes": {k: v for k, v in sorted(outcomes.items())},
    }
    with target.with_name("timings.meta.json").open("w", encoding="utf-8") as handle:
        json.dump(meta, handle, sort_keys=True, separators=(",", ":"))
    print(f"timings: {total_records} records x {passes} passes = {issued} requests at head {head} chain {chain_id}")
    print(f"  wall {wall:.1f}s, achieved {achieved:.1f} rps" + (f" (target {rps:g})" if rps > 0 else " (unpaced)"))
    print(f"  outcomes: {', '.join(f'{k}={v}' for k, v in sorted(outcomes.items()))}")
    failed = sum(v for k, v in outcomes.items() if k != "ok")
    if failed:
        print(f"  WARNING: {failed}/{issued} ({failed / issued * 100:.1f}%) did not return a result — "
              f"failures return early, so percentiles over this matrix are NOT comparable to a clean run", flush=True)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate_parser = subparsers.add_parser("validate", help="check a corpus is loadable; prints the record count only")
    validate_parser.add_argument("--corpus", required=True)

    baseline_parser = subparsers.add_parser("baseline", help="replay the corpus and store baseline responses")
    baseline_parser.add_argument("--corpus", required=True)
    baseline_parser.add_argument("--rpc-url", required=True)
    baseline_parser.add_argument("--state", required=True, help="VM-local state file (gzip JSON)")

    compare_parser = subparsers.add_parser("compare", help="replay the corpus and diff against the baseline")
    compare_parser.add_argument("--corpus", required=True)
    compare_parser.add_argument("--rpc-url", required=True)
    compare_parser.add_argument("--state", required=True)
    compare_parser.add_argument("--report", required=True, help="counts-only report destination (safe to publish)")
    compare_parser.add_argument("--baseline-client", required=True)
    compare_parser.add_argument("--candidate-client", required=True)
    compare_parser.add_argument("--diffs", default=None, help="optional: characterise each content mismatch word by word")

    timings_parser = subparsers.add_parser("timings", help="replay the corpus N times and write a record x pass latency matrix")
    timings_parser.add_argument("--corpus", required=True)
    timings_parser.add_argument("--rpc-url", required=True)
    timings_parser.add_argument("--out", required=True, help="CSV destination (indexes + ms only)")
    timings_parser.add_argument("--passes", type=int, default=1)
    timings_parser.add_argument("--rps", type=float, default=0.0, help="target request rate; 0 = unpaced")
    timings_parser.add_argument("--concurrency", type=int, default=16)
    timings_parser.add_argument("--warmup-seconds", type=int, default=0, help="discarded warm-up applied before the matrix")
    timings_parser.add_argument("--warmup-rps", type=float, default=0.0, help="rate the warm-up actually delivered")

    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "validate":
            print(f"corpus OK: {len(load_corpus(arguments.corpus))} records")
            return 0
        if arguments.command == "timings":
            timings(arguments.corpus, arguments.rpc_url, arguments.out, arguments.passes, arguments.rps,
                    arguments.concurrency, arguments.warmup_seconds, arguments.warmup_rps)
            return 0
        if arguments.command == "baseline":
            baseline(arguments.corpus, arguments.rpc_url, arguments.state)
            return 0
        clean = compare(arguments.corpus, arguments.rpc_url, arguments.state, arguments.report,
                        arguments.baseline_client, arguments.candidate_client, arguments.diffs)
        return 0 if clean else 1
    except CorpusParityError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
