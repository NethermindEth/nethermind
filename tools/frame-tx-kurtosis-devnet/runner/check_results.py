#!/usr/bin/env python3
"""Checks one scenario's collected results against the devnet's acceptance rules.

    runner/check_results.py results/<scenario-id> <attacker-role>

Exits non-zero if any rule fails. Every rule applies to every execution client in the run, so a
multi-client run passes only if each implementation behaves. Standard library only.
"""
from __future__ import annotations

import glob
import json
import os
import re
import sys

ROW = re.compile(r'(\w+)=("[^"]*"|\S+)')
LOG_ALARM = re.compile(r"Exception|[Ii]nvalid [Bb]lock|Unhandled|\bFATAL\b|\bFatal\b|panicked")
# Nethermind replays a synthetic payload at startup to warm its pipeline; the consensus layer
# has not sent a head yet, so it is refused on its timestamp and the node carries on.
BENIGN = re.compile(r"Startup payload pipeline warmup failed; RPC startup will continue")


def rows(result_path: str) -> list[dict]:
    parsed = []
    with open(result_path) as handle:
        for line in handle:
            if line.startswith("RESULT "):
                parsed.append({k: v.strip('"') for k, v in ROW.findall(line[len("RESULT "):])})
    return parsed


def main(argv: list[str]) -> int:
    if len(argv) != 2:
        print(__doc__, file=sys.stderr)
        return 2
    out_dir, role = argv
    sid = os.path.basename(os.path.normpath(out_dir))
    result_path = os.path.join(out_dir, "{0}.result".format(sid))
    if not os.path.isfile(result_path):
        print("  [FAIL] no result file at {0}".format(result_path))
        return 1
    table = rows(result_path)
    failures = []

    def check(name: str, ok: bool, detail: str = "") -> None:
        print("  [{0}] {1}{2}".format("ok  " if ok else "FAIL", name, "" if ok else " -- " + detail))
        if not ok:
            failures.append(name)

    def case(name: str, **match) -> list[dict]:
        return [r for r in table if r.get("case") == name and all(r.get(k) == v for k, v in match.items())]

    nodes = sorted({r["node"] for r in case("preflight")})
    clients = {r["node"]: r.get("client", "?") for r in case("preflight")}
    print("  nodes: {0}".format(", ".join("{0} ({1})".format(n, clients[n]) for n in nodes)))
    check("at least two execution clients", len(nodes) >= 2, str(nodes))

    def every_node(name: str, rows_for, predicate, show) -> None:
        missing, bad = [], []
        for node in nodes:
            found = rows_for(node)
            if not found:
                missing.append(node)
            elif not all(predicate(r) for r in found):
                bad.append("{0}: {1}".format(node, "; ".join(show(r) for r in found)))
        check(name, not missing and not bad, "missing on {0}; {1}".format(missing, bad))

    every_node("every client ready", lambda n: case("preflight", node=n),
               lambda r: r.get("ready") == "yes", lambda r: r.get("ready", "?"))
    every_node("every client produced or imported blocks", lambda n: case("head_progress", node=n),
               lambda r: int(r.get("blocks", "0")) > 1, lambda r: "blocks=" + r.get("blocks", "?"))
    every_node("EIP-8141 active on every client", lambda n: case("fork_gate", node=n),
               lambda r: r.get("active") == "yes", lambda r: r.get("active", "?"))
    every_node("every client refused a prefix just over the ceiling", lambda n: case("ceiling_probe", node=n),
               lambda r: r.get("rejected") == "yes", lambda r: r.get("reason", "?"))
    every_node("every client runs the image built for this ceiling", lambda n: case("image_ceiling", node=n),
               lambda r: r.get("matches") == "yes",
               lambda r: "{0} label={1}".format(r.get("image"), r.get("label_max_verify_gas")))
    for node in nodes:
        log_path = os.path.join(out_dir, "{0}.log".format(node))
        text = open(log_path, errors="replace").read() if os.path.exists(log_path) else None
        if text is None:
            check("{0} log collected".format(node), False, log_path)
            continue
        alarm_lines = [line for line in text.splitlines() if LOG_ALARM.search(line)]
        benign = [line for line in alarm_lines if BENIGN.search(line)]
        real = [line for line in alarm_lines if not BENIGN.search(line)]
        check("{0}: no exception, invalid block, unhandled or fatal line ({1} lines read{2})".format(
            node, text.count("\n"), ", {0} known-benign".format(len(benign)) if benign else ""),
            not real, real[0][:300] if real else "")

    attack = case("admission", role=role, phase="measured")
    every_node("every attack transaction refused on every client",
               lambda n: [r for r in attack if r["node"] == n],
               lambda r: r.get("accepted") == "0" and r.get("errored") == "0"
               and r.get("rejected") == r.get("samples") and int(r.get("samples", "0")) > 0,
               lambda r: "samples={0} accepted={1} rejected={2} errored={3}".format(
                   r.get("samples"), r.get("accepted"), r.get("rejected"), r.get("errored")))
    for r in attack:
        print("         {0}: {1}".format(r["node"], r.get("top_reject_reason", "no reason recorded")))
    # A nonce refusal comes before the validation prefix runs, so it measures nothing.
    events_path = os.path.join(out_dir, "{0}.events.jsonl".format(sid))
    nonce_refusals = 0
    if os.path.exists(events_path):
        with open(events_path) as handle:
            for line in handle:
                event = json.loads(line)
                if event.get("role") == role and "nonce" in (event.get("reason") or "").lower():
                    nonce_refusals += 1
    check("no attack transaction refused on its nonce (the prefix ran)", nonce_refusals == 0,
          "{0} refused on nonce".format(nonce_refusals))

    baseline = case("inclusion", role="baseline")
    check("every baseline transaction included, none outstanding, no slot skipped",
          bool(baseline) and all(r.get("included") == r.get("submitted") and r.get("outstanding") == "0"
                                 and r.get("starved", "0") == "0" for r in baseline),
          "; ".join("{0}: {1}/{2} outstanding={3} starved={4}".format(
              r.get("phase"), r.get("included"), r.get("submitted"), r.get("outstanding"), r.get("starved"))
                    for r in baseline))
    load = case("offered_load", role=role)
    offered = {float(r["offered_rate"]) for r in attack if "offered_rate" in r}
    check("attack delivered at 95% or more of the offered rate",
          bool(load) and bool(offered) and float(load[0].get("achieved_rate", 0)) >= 0.95 * max(offered),
          "achieved {0} of offered {1}".format(load[0].get("achieved_rate") if load else "?", offered))
    capacity = case("generator_capacity")
    check("generator could build every role faster than offered",
          bool(capacity) and all(r.get("sufficient") == "yes" for r in capacity),
          "; ".join("{0}: {1}".format(r.get("role"), r.get("sufficient")) for r in capacity))

    agreement = case("chain_agreement")
    check("all clients agree on the chain",
          len(agreement) == 1 and agreement[0].get("agree") == "yes"
          and agreement[0].get("nodes") == str(len(nodes)),
          str(agreement))
    builders = {r.get("builder"): int(r.get("frame_txs", "0")) for r in case("block_builders")}
    expected = {clients[n] for n in nodes}
    check("every client built blocks carrying frame transactions",
          expected.issubset(builders) and all(builders[c] > 0 for c in expected),
          "builders {0}, clients {1}".format(builders, sorted(expected)))
    check("scenario completed", bool(case("scenario_complete")) and not case("scenario_aborted"),
          str(case("scenario_aborted")))

    metrics = glob.glob(os.path.join(out_dir, "metrics", "*.json"))
    check("metrics snapshotted", bool(metrics))
    check("run provenance written", os.path.getsize(os.path.join(out_dir, "run.json")) > 0
          if os.path.exists(os.path.join(out_dir, "run.json")) else False)
    events = os.path.join(out_dir, "{0}.events.jsonl".format(sid))
    check("per-submission events written", os.path.exists(events) and os.path.getsize(events) > 0)

    if failures:
        print("\n{0} check(s) failed. Raw output: {1}".format(len(failures), out_dir))
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
