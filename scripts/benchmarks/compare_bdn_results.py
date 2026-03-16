#!/usr/bin/env python3
"""Compare BDN gas benchmark results between master (baseline) and PR branch.

Reads two merged BDN JSON files, computes MGas/s from mean execution time,
and outputs a Markdown comparison table suitable for GitHub PR comments.
"""
import json
import sys

# All gas-benchmark payloads use exactly 100M gas by design (filenames contain "gas-value_100M").
# Must stay in sync with the constant in GasBenchmarkColumnProvider.cs.
GAS_PER_BENCHMARK = 100_000_000
SIGNIFICANT_CHANGE_THRESHOLD = 3.0  # % change to flag as regression/improvement


def extract_scenario(benchmark):
    """Extract a short scenario key from the benchmark parameters."""
    params = benchmark.get("Parameters", "")
    # Format: "Scenario=name[params]"
    if params.startswith("Scenario="):
        return params[9:]
    return params or benchmark.get("Method", "unknown")


def mgas_per_second(mean_ns):
    """Convert mean nanoseconds to MGas/s."""
    if mean_ns <= 0:
        return 0.0
    return GAS_PER_BENCHMARK / (mean_ns * 1e-9) / 1e6


def ns_to_ms(ns):
    """Convert nanoseconds to milliseconds."""
    return ns / 1e6


def load_results(filepath):
    """Load BDN JSON and return {scenario: {mgas, mean_ns, ...}}."""
    with open(filepath) as f:
        report = json.load(f)

    results = {}
    for b in report.get("Benchmarks", []):
        scenario = extract_scenario(b)
        stats = b.get("Statistics", {})
        mean_ns = stats.get("Mean", 0)
        stddev_ns = stats.get("StandardDeviation", 0)
        ci99_lower = stats.get("ConfidenceInterval", {}).get("Lower", mean_ns)
        ci99_upper = stats.get("ConfidenceInterval", {}).get("Upper", mean_ns)
        results[scenario] = {
            "mean_ns": mean_ns,
            "mean_ms": ns_to_ms(mean_ns),
            "mgas": mgas_per_second(mean_ns),
            "stddev_ns": stddev_ns,
            "stddev_ms": ns_to_ms(stddev_ns),
            "ci_lower_ms": ns_to_ms(ci99_lower),
            "ci_upper_ms": ns_to_ms(ci99_upper),
        }
    return results


def format_ms(ms):
    """Format milliseconds with appropriate precision."""
    if ms >= 100:
        return "{:.0f}".format(ms)
    if ms >= 10:
        return "{:.1f}".format(ms)
    return "{:.2f}".format(ms)


def main():
    if len(sys.argv) != 3:
        print("Usage: compare_bdn_results.py <baseline.json> <pr.json>", file=sys.stderr)
        sys.exit(1)

    baseline = load_results(sys.argv[1])
    pr = load_results(sys.argv[2])

    all_scenarios = sorted(set(baseline.keys()) | set(pr.keys()))

    if not all_scenarios:
        print("No benchmarks to compare.", file=sys.stderr)
        sys.exit(1)

    matched = 0
    new_count = 0
    removed_count = 0

    # Determine regressions and improvements
    regressions = []
    improvements = []
    neutral = []

    for scenario in all_scenarios:
        b = baseline.get(scenario)
        p = pr.get(scenario)

        if b is None:
            new_count += 1
            neutral.append((scenario, b, p, 0.0))
            continue
        if p is None:
            removed_count += 1
            neutral.append((scenario, b, p, 0.0))
            continue

        matched += 1

        if b["mgas"] > 0:
            change_pct = ((p["mgas"] - b["mgas"]) / b["mgas"]) * 100
        else:
            change_pct = 0.0

        entry = (scenario, b, p, change_pct)
        if change_pct < -SIGNIFICANT_CHANGE_THRESHOLD:
            regressions.append(entry)
        elif change_pct > SIGNIFICANT_CHANGE_THRESHOLD:
            improvements.append(entry)
        else:
            neutral.append(entry)

    # Output Markdown
    lines = []
    lines.append("## Gas Benchmark Results (BDN)")
    lines.append("")

    # Summary line
    parts = ["{} scenarios compared".format(matched)]
    if new_count:
        parts.append("{} new".format(new_count))
    if removed_count:
        parts.append("{} removed".format(removed_count))
    lines.append("{} | {} regressions | {} improvements | threshold: +/-{:.0f}%".format(
        " | ".join(parts), len(regressions), len(improvements), SIGNIFICANT_CHANGE_THRESHOLD))
    lines.append("")

    has_regressions = len(regressions) > 0
    has_improvements = len(improvements) > 0

    if has_regressions:
        lines.append("### :warning: Regressions (>{:.0f}% slower)".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")
        lines.append("| Scenario | Master (ms) | PR (ms) | Delta (ms) | Change | Master (MGas/s) | PR (MGas/s) |")
        lines.append("|----------|------------:|--------:|-----------:|-------:|----------------:|------------:|")
        for scenario, b, p, change_pct in sorted(regressions, key=lambda x: x[3]):
            delta_ms = p["mean_ms"] - b["mean_ms"]
            lines.append("| {} | {} | {} | {:+.1f} | **{:+.1f}%** | {:.1f} | {:.1f} |".format(
                scenario, format_ms(b["mean_ms"]), format_ms(p["mean_ms"]),
                delta_ms, change_pct, b["mgas"], p["mgas"]))
        lines.append("")

    if has_improvements:
        lines.append("### :rocket: Improvements (>{:.0f}% faster)".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")
        lines.append("| Scenario | Master (ms) | PR (ms) | Delta (ms) | Change | Master (MGas/s) | PR (MGas/s) |")
        lines.append("|----------|------------:|--------:|-----------:|-------:|----------------:|------------:|")
        for scenario, b, p, change_pct in sorted(improvements, key=lambda x: -x[3]):
            delta_ms = p["mean_ms"] - b["mean_ms"]
            lines.append("| {} | {} | {} | {:+.1f} | **{:+.1f}%** | {:.1f} | {:.1f} |".format(
                scenario, format_ms(b["mean_ms"]), format_ms(p["mean_ms"]),
                delta_ms, change_pct, b["mgas"], p["mgas"]))
        lines.append("")

    if not has_regressions and not has_improvements:
        lines.append(":white_check_mark: No significant changes detected (all within +/-{:.0f}%).".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")

    # Full results table (collapsed for large sets)
    if len(all_scenarios) > 20:
        lines.append("<details>")
        lines.append("<summary>Full results ({} scenarios)</summary>".format(len(all_scenarios)))
        lines.append("")

    lines.append("| Scenario | Master (ms) | PR (ms) | Delta (ms) | Change | Master (MGas/s) | PR (MGas/s) |")
    lines.append("|----------|------------:|--------:|-----------:|-------:|----------------:|------------:|")

    for scenario in all_scenarios:
        b = baseline.get(scenario)
        p = pr.get(scenario)

        master_ms = format_ms(b["mean_ms"]) if b else "N/A"
        pr_ms = format_ms(p["mean_ms"]) if p else "N/A"
        master_mgas = "{:.1f}".format(b["mgas"]) if b else "N/A"
        pr_mgas = "{:.1f}".format(p["mgas"]) if p else "N/A"

        if b and p and b["mgas"] > 0:
            change_pct = ((p["mgas"] - b["mgas"]) / b["mgas"]) * 100
            delta_ms = p["mean_ms"] - b["mean_ms"]
            change_str = "{:+.1f}%".format(change_pct)
            delta_str = "{:+.1f}".format(delta_ms)
        elif b is None:
            change_str = "NEW"
            delta_str = ""
        elif p is None:
            change_str = "REMOVED"
            delta_str = ""
        else:
            change_str = "N/A"
            delta_str = ""

        lines.append("| {} | {} | {} | {} | {} | {} | {} |".format(
            scenario, master_ms, pr_ms, delta_str, change_str, master_mgas, pr_mgas))

    if len(all_scenarios) > 20:
        lines.append("")
        lines.append("</details>")

    print("\n".join(lines))


if __name__ == "__main__":
    main()
