#!/usr/bin/env python3
"""Merge BenchmarkDotNet JSON results from multiple chunks into a single file."""
import json
import os
import sys


def main():
    if len(sys.argv) != 3:
        print("Usage: merge_bdn_results.py <results_dir> <output_file>", file=sys.stderr)
        sys.exit(1)

    results_dir = sys.argv[1]
    output_file = sys.argv[2]

    all_benchmarks = []
    base_report = None

    for filename in sorted(os.listdir(results_dir)):
        if not filename.endswith("-full.json"):
            continue

        filepath = os.path.join(results_dir, filename)
        with open(filepath) as f:
            try:
                report = json.load(f)
            except json.JSONDecodeError as e:
                print("WARNING: Skipping {} — malformed JSON: {}".format(filename, e), file=sys.stderr)
                continue

        if base_report is None:
            base_report = report

        benchmarks = report.get("Benchmarks", [])
        all_benchmarks.extend(benchmarks)

    if base_report is None:
        print("No BDN result files found in {}".format(results_dir), file=sys.stderr)
        sys.exit(1)

    # Deduplicate by full benchmark name (in case of overlapping chunks)
    seen = {}
    for b in all_benchmarks:
        key = b.get("FullName", b.get("DisplayInfo", ""))
        seen[key] = b

    base_report["Benchmarks"] = list(seen.values())
    base_report["Benchmarks"].sort(key=lambda b: b.get("FullName", ""))

    with open(output_file, "w") as f:
        json.dump(base_report, f, indent=2)

    print("Merged {} benchmarks into {}".format(len(base_report["Benchmarks"]), output_file))


if __name__ == "__main__":
    main()
