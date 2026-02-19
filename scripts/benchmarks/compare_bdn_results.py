#!/usr/bin/env python3
"""Compare BDN gas benchmark results between master (baseline) and PR branch.

Reads two merged BDN JSON files, computes MGas/s from mean execution time,
and outputs a Markdown comparison table suitable for GitHub PR comments.
"""
import json
import sys

GAS_PER_BENCHMARK = 100_000_000  # 100M gas per scenario
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


def load_results(filepath):
    """Load BDN JSON and return {scenario: {mgas, mean_ns, ...}}."""
    with open(filepath) as f:
        report = json.load(f)

    results = {}
    for b in report.get("Benchmarks", []):
        scenario = extract_scenario(b)
        stats = b.get("Statistics", {})
        mean_ns = stats.get("Mean", 0)
        results[scenario] = {
            "mean_ns": mean_ns,
            "mgas": mgas_per_second(mean_ns),
            "stddev_ns": stats.get("StandardDeviation", 0),
        }
    return results


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

    # Determine regressions and improvements
    regressions = []
    improvements = []
    neutral = []

    for scenario in all_scenarios:
        b = baseline.get(scenario)
        p = pr.get(scenario)

        if b is None or p is None:
            neutral.append((scenario, b, p))
            continue

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

    has_regressions = len(regressions) > 0
    has_improvements = len(improvements) > 0

    if has_regressions:
        lines.append("### :warning: Regressions (>{:.0f}% slower)".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")
        lines.append("| Scenario | Master (MGas/s) | PR (MGas/s) | Change |")
        lines.append("|----------|----------------:|------------:|-------:|")
        for scenario, b, p, change_pct in sorted(regressions, key=lambda x: x[3]):
            lines.append("| {} | {:.1f} | {:.1f} | **{:+.1f}%** |".format(
                scenario, b["mgas"], p["mgas"], change_pct))
        lines.append("")

    if has_improvements:
        lines.append("### :rocket: Improvements (>{:.0f}% faster)".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")
        lines.append("| Scenario | Master (MGas/s) | PR (MGas/s) | Change |")
        lines.append("|----------|----------------:|------------:|-------:|")
        for scenario, b, p, change_pct in sorted(improvements, key=lambda x: -x[3]):
            lines.append("| {} | {:.1f} | {:.1f} | **{:+.1f}%** |".format(
                scenario, b["mgas"], p["mgas"], change_pct))
        lines.append("")

    if not has_regressions and not has_improvements:
        lines.append(":white_check_mark: No significant changes detected (all within +/-{:.0f}%).".format(SIGNIFICANT_CHANGE_THRESHOLD))
        lines.append("")

    # Summary table (collapsed for large sets)
    if len(all_scenarios) > 20:
        lines.append("<details>")
        lines.append("<summary>Full results ({} scenarios)</summary>".format(len(all_scenarios)))
        lines.append("")

    lines.append("| Scenario | Master (MGas/s) | PR (MGas/s) | Change |")
    lines.append("|----------|----------------:|------------:|-------:|")

    for scenario in all_scenarios:
        b = baseline.get(scenario)
        p = pr.get(scenario)

        master_str = "{:.1f}".format(b["mgas"]) if b else "N/A"
        pr_str = "{:.1f}".format(p["mgas"]) if p else "N/A"

        if b and p and b["mgas"] > 0:
            change_pct = ((p["mgas"] - b["mgas"]) / b["mgas"]) * 100
            change_str = "{:+.1f}%".format(change_pct)
        elif b is None:
            change_str = "NEW"
        elif p is None:
            change_str = "REMOVED"
        else:
            change_str = "N/A"

        lines.append("| {} | {} | {} | {} |".format(scenario, master_str, pr_str, change_str))

    if len(all_scenarios) > 20:
        lines.append("")
        lines.append("</details>")

    print("\n".join(lines))


if __name__ == "__main__":
    main()
