"""Prints a Markdown comparison of two joined BenchmarkDotNet runs.

Usage: compare.py <base artifacts directory> <head artifacts directory>
Reads BASE_SHA, HEAD_SHA, BASE_OUTCOME, HEAD_OUTCOME and RUNNER_LABEL from the environment.
"""

import glob
import json
import os
import sys


def load(artifacts_dir):
    files = glob.glob(os.path.join(artifacts_dir, "results", "*-report-full-compressed.json"))
    results = {}
    for path in files:
        with open(path) as f:
            for bm in json.load(f)["Benchmarks"]:
                stats = bm.get("Statistics")
                if not stats:
                    continue
                name = f'{bm["Type"]}.{bm["Method"]}'
                if bm.get("Parameters"):
                    name += f'({bm["Parameters"]})'
                results[name] = {
                    "median": stats["Median"],
                    "cv": stats["StandardDeviation"] / stats["Mean"] * 100 if stats["Mean"] else 0,
                    "allocated": bm.get("Memory", {}).get("BytesAllocatedPerOperation", 0),
                }
    return results


def fmt_time(ns):
    if ns is None:
        return "—"
    for unit, scale in (("s", 1e9), ("ms", 1e6), ("μs", 1e3)):
        if ns >= scale:
            return f"{ns / scale:.2f} {unit}"
    return f"{ns:.1f} ns"


def fmt_bytes(b):
    if b is None:
        return "—"
    for unit, scale in (("MB", 1 << 20), ("KB", 1 << 10)):
        if b >= scale:
            return f"{b / scale:.2f} {unit}"
    return f"{b} B"


def fmt_delta(head, base):
    if head is None or base is None:
        return "—"
    if base == 0:
        return "0%" if head == 0 else "new"
    return f"{(head - base) / base * 100:+.1f}%"


def main():
    base = load(sys.argv[1])
    head = load(sys.argv[2])
    env = os.environ

    print("## Network benchmarks")
    print()
    print(f'Runner `{env.get("RUNNER_LABEL", "")}`, base `{env.get("BASE_SHA", "")[:10]}` '
          f'({env.get("BASE_OUTCOME", "")}), head `{env.get("HEAD_SHA", "")[:10]}` ({env.get("HEAD_OUTCOME", "")}).')
    print()
    print("Medians are compared; a delta within the CV of either side is noise. Allocations are per operation "
          "and deterministic.")
    print()
    if not head:
        print("_No head results; see the run log._")
        return

    print("| Benchmark | Base | Head | Δ | CV base | CV head | Alloc base | Alloc head | Δ alloc |")
    print("|---|---:|---:|---:|---:|---:|---:|---:|---:|")
    for name in sorted(head):
        h = head[name]
        b = base.get(name)
        cv_base = f"{b['cv']:.1f}%" if b else "—"
        print(f'| {name} '
              f'| {fmt_time(b and b["median"])} | {fmt_time(h["median"])} '
              f'| {fmt_delta(h["median"], b and b["median"])} '
              f'| {cv_base} | {h["cv"]:.1f}% '
              f'| {fmt_bytes(b and b["allocated"])} | {fmt_bytes(h["allocated"])} '
              f'| {fmt_delta(h["allocated"], b and b["allocated"])} |')


if __name__ == "__main__":
    main()
