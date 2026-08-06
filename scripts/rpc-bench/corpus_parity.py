#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Replay a private eth_call corpus against a node and compare clients by response bytes.

Privacy contract: request and response contents never appear in output — errors and
reports carry only record indexes, counts, and category names. The baseline state file
(response hex strings) is written to VM-local scratch and must not be artifacted.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import csv
import gzip
import json
import os
import sys
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Sequence

# Guard rail, not a hard limit: the replay holds every record's params in memory, so a large
# capture needs a deliberate raise (and a runner with the RAM for it) rather than discovering the
# cost mid-sweep. Override with RPC_BENCH_MAX_CORPUS_RECORDS.
MAX_CORPUS_RECORDS = int(os.environ.get("RPC_BENCH_MAX_CORPUS_RECORDS", "10000"))
MAX_RESPONSE_BYTES = 16 * 1024 * 1024
REQUEST_TIMEOUT_SECONDS = 120

# Fixed numeric report schema; corpus_results.stage validates staged reports against this.
# "matched" = identical result bytes; "both_rpc_errors" = both clients reject the call (also
# agreement — captured corpora legitimately contain calls that fail at the pinned head, e.g.
# explicit gasPrice with an underfunded sender). Everything else is a defect or divergence.
PARITY_COUNTER_FIELDS = (
    "total",
    "matched",
    "both_rpc_errors",
    "baseline_rpc_errors",
    "candidate_rpc_errors",
    "candidate_transport_failures",
    "candidate_invalid_responses",
    "baseline_shorter",
    "candidate_shorter",
    "length_mismatches",
    "content_mismatches",
)
PARITY_LABEL_FIELDS = ("baseline_client", "candidate_client")

# Reports also carry the 1-based corpus indexes of divergent records (capped) so the corpus
# OWNER can look the calls up in their copy — an index is positional metadata, not content.
MAX_DIVERGENCE_INDEXES = 200

# Baseline outcome marker for a call the baseline client rejected with a JSON-RPC error.
# Deliberately not valid hex so it can never collide with a result string; the error
# content itself is never stored.
ERROR_MARKER = "!rpc_error"


class CorpusParityError(Exception):
    """Raised with a content-free message when a replay cannot produce a trustworthy result."""


def _reject_non_json_constant(value: str) -> None:
    raise ValueError(f"invalid JSON constant {value!r}")


def load_corpus(path: str | Path) -> list[list]:
    """Return the params of each corpus record, in file order."""
    path = Path(path)
    load_started = time.perf_counter()
    # The latency cells convert the same file with prepare-eth-call-corpus.py, which requires one
    # of these suffixes. Enforcing it here too keeps both readers agreeing on what a legal corpus
    # is, so a bad corpus_glob fails at validation rather than inside the first cell.
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
                    # Match the converter: NaN/Infinity are not JSON, and accepting them here
                    # would validate a corpus that then fails conversion in the first cell.
                    record = json.loads(line, parse_constant=_reject_non_json_constant)
                # JSONDecodeError is a ValueError; so is the rejection above. Catch both so a
                # malformed corpus reports a line number instead of a traceback.
                except (ValueError, RecursionError):
                    raise CorpusParityError(f"corpus line {number}: invalid JSON") from None
                if not isinstance(record, dict) or record.get("method") != "eth_call" \
                        or not isinstance(record.get("params"), list):
                    raise CorpusParityError(f"corpus line {number}: not an eth_call record")
                params.append(record["params"])
    except OSError as error:
        raise CorpusParityError(f"cannot read corpus: {error.__class__.__name__}") from None
    if not params:
        raise CorpusParityError("corpus contains no records")
    # Decompressing and parsing a large corpus takes minutes; say so rather than looking hung.
    took = time.perf_counter() - load_started
    if took > 5:
        print(f"  loaded {len(params)} records in {took:.0f}s", flush=True)
    return params


def _node_identity(url: str) -> tuple[int, int]:
    """Return (head block number, chain id); raises a content-free error when unreadable."""
    identity = []
    for method in ("eth_blockNumber", "eth_chainId"):
        body = json.dumps({"jsonrpc": "2.0", "id": 1, "method": method, "params": []}).encode()
        request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
                envelope = json.loads(response.read(1 << 20))
            identity.append(int(envelope["result"], 16))
        except (urllib.error.URLError, OSError, ValueError, KeyError, TypeError):
            raise CorpusParityError(f"cannot read {method} from the node") from None
    return identity[0], identity[1]


def _post(url: str, index: int, params: list) -> tuple[str | None, str]:
    """POST one eth_call; return (category, result_hex). category is None on success."""
    body = json.dumps(
        {"jsonrpc": "2.0", "id": index, "method": "eth_call", "params": params},
        separators=(",", ":"),
    ).encode()
    request = urllib.request.Request(url, data=body, headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(request, timeout=REQUEST_TIMEOUT_SECONDS) as response:
            raw = response.read(MAX_RESPONSE_BYTES + 1)
    except urllib.error.HTTPError as error:
        # Some clients/proxies answer JSON-RPC errors with a non-200 status — that is a
        # response, not a transport failure. The body is parsed but never stored.
        try:
            raw = error.read(MAX_RESPONSE_BYTES + 1)
            envelope = json.loads(raw)
        except (OSError, ValueError):
            return "transport_failure", ""
        if isinstance(envelope, dict) and "error" in envelope and envelope.get("id") in (index, None):
            return "rpc_error", ""
        return "transport_failure", ""
    except (urllib.error.URLError, OSError, ValueError):
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
        return "rpc_error", ""
    result = envelope.get("result")
    if not isinstance(result, str) or not result.startswith("0x") or len(result) % 2 != 0:
        return "invalid_response", ""
    try:
        bytes.fromhex(result[2:])
    except ValueError:
        return "invalid_response", ""
    return None, result.lower()


def baseline(corpus: str, rpc_url: str, state_path: str) -> None:
    """Replay the whole corpus and store each outcome: result hex, or an error marker.

    JSON-RPC errors are recorded (not fatal) — a captured corpus legitimately contains
    calls that fail at the pinned head, and both clients rejecting a call is agreement.
    Transport/invalid responses still abort: they indicate node trouble, not call content.
    """
    params_list = load_corpus(corpus)
    head, chain_id = _node_identity(rpc_url)
    results: list[str] = []
    failures: dict[str, int] = {}
    error_count = 0
    started = time.perf_counter()
    for index, params in enumerate(params_list, start=1):
        _progress(index, len(params_list), started, "baseline")
        category, result = _post(rpc_url, index, params)
        if category == "rpc_error":
            error_count += 1
            results.append(ERROR_MARKER)
            continue
        if category is not None:
            failures[category] = failures.get(category, 0) + 1
        results.append(result)
    if failures:
        summary = " ".join(f"{key}={value}" for key, value in sorted(failures.items()))
        raise CorpusParityError(
            f"baseline replay had failures over {len(params_list)} records: {summary}"
        )
    state = Path(state_path)
    state.parent.mkdir(parents=True, exist_ok=True)
    with gzip.open(state, "wt", encoding="utf-8") as output:
        json.dump({"total": len(results), "head": head, "chain_id": chain_id, "results": results}, output)
    print(f"baseline captured: {len(results)} outcomes ({error_count} rpc_error) at head {head}")
    if error_count:
        error_indexes = [str(i) for i, r in enumerate(results, start=1) if r == ERROR_MARKER]
        print(f"baseline rpc_error indexes (first {min(len(error_indexes), 40)}): {' '.join(error_indexes[:40])}")


def compare(corpus: str, rpc_url: str, state_path: str, report_path: str,
            baseline_client: str, candidate_client: str) -> bool:
    """Replay the corpus against a candidate node and diff against the stored baseline."""
    params_list = load_corpus(corpus)
    try:
        with gzip.open(state_path, "rt", encoding="utf-8") as source:
            state = json.load(source)
        baseline_results = state["results"]
        baseline_head, baseline_chain = state["head"], state["chain_id"]
    except (OSError, json.JSONDecodeError, KeyError, TypeError):
        raise CorpusParityError("baseline state is missing or unreadable") from None
    if not isinstance(baseline_results, list) or len(baseline_results) != len(params_list):
        raise CorpusParityError(
            f"baseline state has {len(baseline_results)} results but the corpus has {len(params_list)}"
        )
    # A snapshot at a different head/chain would mismatch on every record — report it as the
    # fixture problem it is, not as client divergence.
    head, chain_id = _node_identity(rpc_url)
    if (head, chain_id) != (baseline_head, baseline_chain):
        raise CorpusParityError(
            f"node identity mismatch: baseline head={baseline_head} chain={baseline_chain} "
            f"vs candidate head={head} chain={chain_id} — align the snapshots before comparing"
        )

    report = {field: 0 for field in PARITY_COUNTER_FIELDS}
    report["total"] = len(params_list)
    divergences: list[dict[str, int | str]] = []

    def diverge(index: int, kind: str) -> None:
        if len(divergences) < MAX_DIVERGENCE_INDEXES:
            divergences.append({"index": index, "kind": kind})

    compare_started = time.perf_counter()
    for index, params in enumerate(params_list, start=1):
        _progress(index, len(params_list), compare_started, "compare")
        expected = baseline_results[index - 1]
        category, actual = _post(rpc_url, index, params)
        if category == "transport_failure":
            report["candidate_transport_failures"] += 1
            diverge(index, "candidate_transport_failure")
        elif category == "invalid_response":
            report["candidate_invalid_responses"] += 1
            diverge(index, "candidate_invalid_response")
        elif category == "rpc_error":
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

    document = {"baseline_client": baseline_client, "candidate_client": candidate_client,
                "divergences": divergences, **report}
    target = Path(report_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open("w", encoding="utf-8") as output:
        json.dump(document, output, sort_keys=True, separators=(",", ":"))
        output.write("\n")
    clean = report["matched"] + report["both_rpc_errors"] == report["total"]
    defects = " ".join(
        f"{key}={value}" for key, value in report.items()
        if value and key not in ("total", "matched", "both_rpc_errors")
    )
    agreement = f"{report['matched']}/{report['total']} matched"
    if report["both_rpc_errors"]:
        agreement += f" + {report['both_rpc_errors']} both-error"
    print(f"parity {candidate_client} vs {baseline_client}: {agreement}"
          + (f" ({defects})" if defects else ""))
    if divergences:
        preview = " ".join(str(d["index"]) for d in divergences[:40])
        print(f"divergent corpus indexes (first {min(len(divergences), 40)}): {preview}")
    return clean


# A 50k-record replay is a long silent stretch; emit progress often enough that an operator can
# tell a slow node from a hung one, but rarely enough not to flood the job log.
PROGRESS_EVERY = 2000


def _progress(done: int, total: int, started: float, what: str) -> None:
    if done % PROGRESS_EVERY and done != total:
        return
    elapsed = time.perf_counter() - started
    rate = done / elapsed if elapsed > 0 else 0.0
    eta = (total - done) / rate if rate > 0 else 0.0
    print(f"  {what} {done}/{total} ({done / total * 100:.0f}%) "
          f"{rate:.0f} req/s, ~{eta / 60:.1f} min left", flush=True)


def _timed_post(url: str, index: int, params: list) -> tuple[float, str]:
    """POST one eth_call and return (elapsed_ms, outcome). Never raises."""
    started = time.perf_counter()
    try:
        category, _ = _post(url, index, params)
    except Exception:  # a replay must never lose the whole matrix to one bad record
        category = "transport_failure"
    return (time.perf_counter() - started) * 1000.0, category or "ok"


def timings(corpus: str, rpc_url: str, out_path: str, passes: int, rps: float, concurrency: int) -> None:
    """Replay every record `passes` times and write a record x pass matrix of latencies.

    Unlike the k6 cells, which sample the corpus uniformly with replacement and tag every request
    identically, this walks the corpus in order so each row is attributable to one record. Output
    carries record indexes and milliseconds only — no request or response content.
    """
    params_list = load_corpus(corpus)
    total_records = len(params_list)
    if passes < 1:
        raise CorpusParityError("passes must be >= 1")
    head, chain_id = _node_identity(rpc_url)

    grid: list[list[float | None]] = [[None] * passes for _ in range(total_records)]
    outcomes: dict[str, int] = {}
    lock = threading.Lock()
    started_at = time.perf_counter()

    def run_one(order: int, record: int, current_pass: int) -> None:
        # Pace by submission order so the achieved rate matches --rps regardless of latency.
        if rps > 0:
            due = started_at + order / rps
            delay = due - time.perf_counter()
            if delay > 0:
                time.sleep(delay)
        elapsed, outcome = _timed_post(rpc_url, record + 1, params_list[record])
        with lock:
            grid[record][current_pass] = elapsed
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
        writer.writerow(["record_index"] + [f"pass_{p + 1}_ms" for p in range(passes)])
        for record in range(total_records):
            writer.writerow([record + 1] + [
                "" if v is None else f"{v:.3f}" for v in grid[record]
            ])

    achieved = issued / wall if wall > 0 else 0.0
    summary = ", ".join(f"{k}={v}" for k, v in sorted(outcomes.items()))
    print(f"timings: {total_records} records x {passes} passes = {issued} requests "
          f"at head {head} chain {chain_id}")
    print(f"  wall {wall:.1f}s, achieved {achieved:.1f} rps"
          + (f" (target {rps:g})" if rps > 0 else " (unpaced)"))
    print(f"  outcomes: {summary}")


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

    timings_parser = subparsers.add_parser(
        "timings", help="replay the corpus N times and write a record x pass latency matrix")
    timings_parser.add_argument("--corpus", required=True)
    timings_parser.add_argument("--rpc-url", required=True)
    timings_parser.add_argument("--out", required=True, help="CSV destination (indexes + ms only)")
    timings_parser.add_argument("--passes", type=int, default=1, help="times to replay the whole corpus")
    timings_parser.add_argument("--rps", type=float, default=0.0, help="target request rate; 0 = unpaced")
    timings_parser.add_argument("--concurrency", type=int, default=16, help="in-flight requests")

    arguments = parser.parse_args(argv)
    try:
        if arguments.command == "validate":
            print(f"corpus OK: {len(load_corpus(arguments.corpus))} records")
            return 0
        if arguments.command == "timings":
            timings(arguments.corpus, arguments.rpc_url, arguments.out,
                    arguments.passes, arguments.rps, arguments.concurrency)
            return 0
        if arguments.command == "baseline":
            baseline(arguments.corpus, arguments.rpc_url, arguments.state)
            return 0
        clean = compare(
            arguments.corpus, arguments.rpc_url, arguments.state, arguments.report,
            arguments.baseline_client, arguments.candidate_client,
        )
        return 0 if clean else 1
    except CorpusParityError as error:
        print(f"error: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
