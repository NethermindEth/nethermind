#!/usr/bin/env python3

"""
Builds a deterministic benchmark shard plan from BenchmarkDotNet --list flat output.
"""

from __future__ import annotations

import argparse
from pathlib import Path


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Create benchmark shard filter files.")
    parser.add_argument("--input", required=True, help="Path to flat benchmark list output.")
    parser.add_argument("--output-dir", required=True, help="Directory for shard-<n>.txt files.")
    parser.add_argument("--shard-count", required=True, type=int, help="Number of shards.")
    parser.add_argument(
        "--namespace-prefix",
        default="Nethermind.",
        help="Prefix used to detect benchmark lines in the flat list.",
    )
    return parser.parse_args()


def read_lines_with_fallback(path: Path) -> list[str]:
    raw = path.read_bytes()
    if raw.startswith(b"\xff\xfe") or raw.startswith(b"\xfe\xff"):
        return raw.decode("utf-16").splitlines()
    if raw.startswith(b"\xef\xbb\xbf"):
        return raw.decode("utf-8-sig").splitlines()

    for encoding in ("utf-8", "utf-16"):
        try:
            return raw.decode(encoding).splitlines()
        except UnicodeDecodeError:
            continue

    raise SystemExit(f"Could not decode benchmark list '{path}' as UTF-8 or UTF-16.")


def extract_benchmark_classes(lines: list[str], namespace_prefix: str) -> list[str]:
    classes: set[str] = set()
    for line in lines:
        text = line.strip()
        if not text.startswith(namespace_prefix):
            continue

        benchmark_name = text.split("(", 1)[0]
        if "." not in benchmark_name:
            continue

        classes.add(benchmark_name.rsplit(".", 1)[0])

    return sorted(classes)


def write_shards(classes: list[str], output_dir: Path, shard_count: int) -> None:
    shards: list[list[str]] = [[] for _ in range(shard_count)]
    for idx, class_name in enumerate(classes):
        shards[idx % shard_count].append(f"*{class_name}*")

    output_dir.mkdir(parents=True, exist_ok=True)
    for shard_index, shard_filters in enumerate(shards, start=1):
        target = output_dir / f"shard-{shard_index}.txt"
        content = "\n".join(shard_filters)
        if content:
            content += "\n"
        target.write_text(content, encoding="utf-8")

    print(f"Discovered {len(classes)} benchmark classes.")
    for shard_index, shard_filters in enumerate(shards, start=1):
        print(f"Shard {shard_index}: {len(shard_filters)} class filters")


def main() -> int:
    args = parse_args()
    if args.shard_count < 1:
        raise SystemExit("--shard-count must be >= 1.")

    input_path = Path(args.input)
    if not input_path.exists():
        raise SystemExit(f"Flat benchmark list '{input_path}' does not exist.")

    lines = read_lines_with_fallback(input_path)
    classes = extract_benchmark_classes(lines, args.namespace_prefix)
    if not classes:
        raise SystemExit("No benchmarks discovered for shard planning.")

    write_shards(classes, Path(args.output_dir), args.shard_count)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
