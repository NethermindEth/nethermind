#!/usr/bin/env python3

from __future__ import annotations

import argparse
import csv
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Validate benchmark CSV result files.")
    parser.add_argument("--results-dir", required=True, help="Directory containing *-report.csv files.")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    results_dir = Path(args.results_dir)
    if not results_dir.exists():
        raise SystemExit(f"Results directory '{results_dir}' does not exist.")

    csv_files = sorted(results_dir.glob("*-report.csv"))
    if not csv_files:
        raise SystemExit(f"No benchmark report CSV files were produced in '{results_dir}'.")

    failed: list[str] = []
    for csv_path in csv_files:
        with csv_path.open("r", encoding="utf-8-sig", newline="") as file:
            reader = csv.DictReader(file)
            for row in reader:
                mean = (row.get("Mean") or "").strip()
                if mean.upper() == "NA":
                    method = (row.get("Method") or "").strip()
                    failed.append(f"{csv_path.name}:{method}")

    if failed:
        print("Benchmarks with non-numeric mean values detected:")
        for item in failed:
            print(item)
        raise SystemExit("Benchmark execution issues detected (Mean=NA).")

    print(f"Validated {len(csv_files)} benchmark CSV files in '{results_dir}'.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

