#!/usr/bin/env python3

"""
Builds deterministic benchmark shard plans from BenchmarkDotNet --list flat output.
Can optionally use historical benchmark means to balance shards better.
"""

from __future__ import annotations

import argparse
import json
import statistics
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path


@dataclass(frozen=True)
class BenchmarkMethod:
    full_name: str
    class_name: str


@dataclass
class ClassPlanItem:
    class_name: str
    method_count: int
    estimated_weight_ns: float


@dataclass
class ShardPlan:
    index: int
    items: list[ClassPlanItem] = field(default_factory=list)
    total_weight_ns: float = 0.0
    total_methods: int = 0

    def add(self, item: ClassPlanItem) -> None:
        self.items.append(item)
        self.total_weight_ns += item.estimated_weight_ns
        self.total_methods += item.method_count


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
    parser.add_argument(
        "--history-summary",
        default="",
        help="Optional benchmark summary JSON used to estimate class weights.",
    )
    parser.add_argument(
        "--plan-summary",
        default="",
        help="Optional markdown file path that describes planned shard balancing.",
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


def extract_benchmark_methods(lines: list[str], namespace_prefix: str) -> list[BenchmarkMethod]:
    methods: dict[str, BenchmarkMethod] = {}
    for line in lines:
        text = line.strip()
        if not text.startswith(namespace_prefix):
            continue

        benchmark_name = text.split("(", 1)[0]
        if "." not in benchmark_name:
            continue

        class_name = benchmark_name.rsplit(".", 1)[0]
        methods[benchmark_name] = BenchmarkMethod(full_name=benchmark_name, class_name=class_name)

    return [methods[key] for key in sorted(methods.keys())]


def read_history_weights(path: Path | None) -> dict[str, float]:
    if path is None or not path.exists():
        return {}

    payload = json.loads(path.read_text(encoding="utf-8"))
    rows = payload.get("benchmarks", [])
    weights: dict[str, float] = defaultdict(float)
    for row in rows:
        benchmark = row.get("benchmark")
        mean_ns = row.get("meanNs")
        if not benchmark or mean_ns is None:
            continue

        try:
            weights[str(benchmark)] += float(mean_ns)
        except (TypeError, ValueError):
            continue

    return dict(weights)


def build_class_items(
    methods: list[BenchmarkMethod],
    history_method_weights: dict[str, float],
) -> list[ClassPlanItem]:
    methods_by_class: dict[str, list[str]] = defaultdict(list)
    for method in methods:
        methods_by_class[method.class_name].append(method.full_name)

    known_method_weights = [weight for weight in history_method_weights.values() if weight > 0]
    if known_method_weights:
        default_method_weight = statistics.median(known_method_weights)
    else:
        default_method_weight = 1.0

    items: list[ClassPlanItem] = []
    for class_name in sorted(methods_by_class.keys()):
        class_methods = methods_by_class[class_name]
        weight = 0.0
        for method_name in class_methods:
            weight += history_method_weights.get(method_name, default_method_weight)

        items.append(
            ClassPlanItem(
                class_name=class_name,
                method_count=len(class_methods),
                estimated_weight_ns=weight,
            )
        )

    return items


def balance_shards(items: list[ClassPlanItem], shard_count: int) -> list[ShardPlan]:
    shards = [ShardPlan(index=i + 1) for i in range(shard_count)]
    sorted_items = sorted(
        items,
        key=lambda item: (item.estimated_weight_ns, item.method_count, item.class_name),
        reverse=True,
    )

    for item in sorted_items:
        target = min(shards, key=lambda shard: (shard.total_weight_ns, len(shard.items), shard.index))
        target.add(item)

    return shards


def write_shards(shards: list[ShardPlan], output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    for shard in shards:
        target = output_dir / f"shard-{shard.index}.txt"
        filters = [f"*{item.class_name}*" for item in sorted(shard.items, key=lambda item: item.class_name)]
        content = "\n".join(filters)
        if content:
            content += "\n"
        target.write_text(content, encoding="utf-8")


def write_plan_summary(
    shards: list[ShardPlan],
    output_path: Path,
    using_history: bool,
    total_classes: int,
    total_methods: int,
) -> None:
    output_path.parent.mkdir(parents=True, exist_ok=True)

    lines: list[str] = []
    lines.append("# Benchmark shard plan")
    lines.append("")
    lines.append(f"- Total classes: **{total_classes}**")
    lines.append(f"- Total methods: **{total_methods}**")
    lines.append(f"- Uses history weights: **{'yes' if using_history else 'no'}**")
    lines.append("")
    lines.append("| Shard | Classes | Methods | Estimated weight (ns) |")
    lines.append("|---:|---:|---:|---:|")
    for shard in shards:
        lines.append(
            f"| {shard.index} | {len(shard.items)} | {shard.total_methods} | {int(shard.total_weight_ns):,} |"
        )

    lines.append("")
    lines.append("## Shard contents")
    lines.append("")
    for shard in shards:
        lines.append(f"### Shard {shard.index}")
        lines.append("")
        lines.append("| Class | Methods | Estimated weight (ns) |")
        lines.append("|---|---:|---:|")
        for item in sorted(shard.items, key=lambda value: value.class_name):
            lines.append(f"| `{item.class_name}` | {item.method_count} | {int(item.estimated_weight_ns):,} |")
        lines.append("")

    output_path.write_text("\n".join(lines), encoding="utf-8")


def main() -> int:
    args = parse_args()
    if args.shard_count < 1:
        raise SystemExit("--shard-count must be >= 1.")

    input_path = Path(args.input)
    if not input_path.exists():
        raise SystemExit(f"Flat benchmark list '{input_path}' does not exist.")

    lines = read_lines_with_fallback(input_path)
    methods = extract_benchmark_methods(lines, args.namespace_prefix)
    if not methods:
        raise SystemExit("No benchmarks discovered for shard planning.")

    history_path = Path(args.history_summary) if args.history_summary else None
    history_weights = read_history_weights(history_path)

    class_items = build_class_items(methods, history_weights)
    shards = balance_shards(class_items, args.shard_count)
    write_shards(shards, Path(args.output_dir))

    summary_path = Path(args.plan_summary) if args.plan_summary else Path(args.output_dir) / "plan-summary.md"
    write_plan_summary(
        shards=shards,
        output_path=summary_path,
        using_history=len(history_weights) > 0,
        total_classes=len(class_items),
        total_methods=len(methods),
    )

    print(f"Discovered {len(class_items)} benchmark classes and {len(methods)} methods.")
    print(f"Using history weights: {'yes' if history_weights else 'no'}")
    for shard in shards:
        print(
            f"Shard {shard.index}: classes={len(shard.items)}, "
            f"methods={shard.total_methods}, estimated_weight_ns={int(shard.total_weight_ns)}"
        )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
