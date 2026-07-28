#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
#
# Aggregate per-cell jsonbench-summary.md files from a client x rps sweep into
# two matrices: an OVERALL cell matrix (client x rps) and a PER-SCENARIO matrix
# (scenario x client@rps). Parses both the "Overall" and "Per method" markdown
# tables run-jsonbench.sh emits. Args: <client:rps>=<summary.md> ...
import re
import sys

_OVR = {
    "p50": re.compile(r"\|\s*latency p50 \(ms\)\s*\|\s*([0-9.]+)"),
    "p90": re.compile(r"\|\s*latency p90 \(ms\)\s*\|\s*([0-9.]+)"),
    "p99": re.compile(r"\|\s*latency p99 \(ms\)\s*\|\s*([0-9.]+)"),
    "tput": re.compile(r"\|\s*throughput \(req/s\)\s*\|\s*([0-9.]+)"),
    "checks": re.compile(r"\|\s*checks passed\s*\|\s*([0-9.]+)%"),
    "fail": re.compile(r"\|\s*http fail rate\s*\|\s*([0-9.]+)%"),
}
# per-method row: | name | avg | p50 | p90 | p95 | p99 | max |
_METH = re.compile(
    r"\|\s*([\w/#-]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|\s*([0-9.]+)\s*\|"
)


def parse(path):
    ovr, meth = {}, {}
    txt = open(path, encoding="utf-8", errors="replace").read()
    for k, rx in _OVR.items():
        m = rx.search(txt)
        if m:
            ovr[k] = float(m.group(1))
    in_meth = False
    for line in txt.splitlines():
        if line.strip().startswith("### Per method"):
            in_meth = True
            continue
        if in_meth:
            m = _METH.match(line.strip())
            if m and m.group(1) != "method":
                meth[m.group(1)] = {"p50": float(m.group(3)), "p90": float(m.group(4)),
                                    "p99": float(m.group(6))}
    return ovr, meth


def main():
    cells, clients, rpss, scenarios = {}, [], [], []
    for arg in sys.argv[1:]:
        label, path = arg.split("=", 1)
        client, rps = label.split(":")
        if client not in clients:
            clients.append(client)
        if rps not in rpss:
            rpss.append(rps)
        ovr, meth = parse(path)
        cells[(client, rps)] = (ovr, meth)
        for s in meth:
            if s not in scenarios:
                scenarios.append(s)
    rpss.sort(key=int)

    print("## Overall (client x rps) - throughput r/s / checks% / p90 / p99 (ms)\n")
    print("| client | " + " | ".join(f"rps {r}" for r in rpss) + " |")
    print("|" + "---|" * (len(rpss) + 1))
    for c in clients:
        row = [c]
        for r in rpss:
            o, _ = cells.get((c, r), (None, None))
            row.append(f"{o['tput']:.0f} / {o['checks']:.0f}% / {o['p90']:.0f} / {o['p99']:.0f}"
                       if o else "-")
        print("| " + " | ".join(row) + " |")

    for metric in ("p99", "p90"):
        print(f"\n## Per-scenario {metric} (ms) - rows=scenario, cols=client@rps\n")
        cols = [(c, r) for c in clients for r in rpss]
        print("| scenario | " + " | ".join(f"{c[:4]}@{r}" for c, r in cols) + " |")
        print("|" + "---|" * (len(cols) + 1))
        for s in scenarios:
            row = [s]
            for c, r in cols:
                _, m = cells.get((c, r), (None, None))
                row.append(f"{m[s][metric]:.0f}" if m and s in m else "-")
            print("| " + " | ".join(row) + " |")


if __name__ == "__main__":
    main()
