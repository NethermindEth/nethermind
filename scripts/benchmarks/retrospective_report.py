#!/usr/bin/env python3
"""Generate a retrospective report from multiple BDN benchmark runs across commits.

Reads BDN JSON results from per-commit directories, computes MGas/s for each
scenario at each commit, detects per-scenario regression inflection points
(the specific commit range where performance changed), and outputs a structured
Markdown + JSON report suitable for both human review and AI agent analysis.

Usage:
    python3 scripts/benchmarks/retrospective_report.py <results_dir> [--json]

The results_dir is expected to contain subdirectories named retro-<commit_sha>,
each containing BDN *-full.json result files.

Output:
    Markdown report to stdout.
    With --json: machine-readable JSON to stdout instead.
"""

import json
import os
import statistics
import sys
from collections import defaultdict

GAS_PER_BENCHMARK = 100_000_000
# Minimum % change between consecutive data points to flag as an inflection
INFLECTION_THRESHOLD = 5.0
# Minimum % change end-to-end to include scenario in the summary
SIGNIFICANT_CHANGE_THRESHOLD = 5.0


def extract_scenario(benchmark):
    """Extract a short scenario key from the benchmark parameters."""
    params = benchmark.get("Parameters", "")
    if params.startswith("Scenario="):
        return params[9:]
    return params or benchmark.get("Method", "unknown")


def mgas_per_second(mean_ns):
    """Convert mean nanoseconds to MGas/s."""
    if mean_ns <= 0:
        return 0.0
    return GAS_PER_BENCHMARK / (mean_ns * 1e-9) / 1e6


def load_commit_results(json_path):
    """Load BDN JSON and return {scenario: {mgas, mean_ns, stddev_ns}}."""
    with open(json_path) as f:
        report = json.load(f)

    results = {}
    for b in report.get("Benchmarks", []):
        scenario = extract_scenario(b)
        stats = b.get("Statistics", {})
        mean_ns = stats.get("Mean", 0)
        results[scenario] = {
            "mgas": mgas_per_second(mean_ns),
            "mean_ns": mean_ns,
            "stddev_ns": stats.get("StandardDeviation", 0),
        }
    return results


def find_inflection_points(timeline):
    """Find commits where a significant performance change occurred.

    timeline: list of (sha, mgas) in chronological order (oldest first).
    Returns: list of {before_sha, after_sha, before_mgas, after_mgas, change_pct, direction}
    """
    inflections = []
    for i in range(1, len(timeline)):
        prev_sha, prev_val = timeline[i - 1]
        curr_sha, curr_val = timeline[i]

        if prev_val <= 0 or curr_val <= 0:
            continue

        change_pct = ((curr_val - prev_val) / prev_val) * 100

        if abs(change_pct) >= INFLECTION_THRESHOLD:
            inflections.append({
                "before_sha": prev_sha,
                "after_sha": curr_sha,
                "before_mgas": prev_val,
                "after_mgas": curr_val,
                "change_pct": change_pct,
                "direction": "regression" if change_pct < 0 else "improvement",
            })

    return inflections


def find_largest_drop(timeline):
    """Find the single largest regression between consecutive commits.

    Returns: dict with before/after info, or None if no regression found.
    """
    worst = None
    for i in range(1, len(timeline)):
        prev_sha, prev_val = timeline[i - 1]
        curr_sha, curr_val = timeline[i]

        if prev_val <= 0 or curr_val <= 0:
            continue

        change_pct = ((curr_val - prev_val) / prev_val) * 100
        if change_pct < 0 and (worst is None or change_pct < worst["change_pct"]):
            worst = {
                "before_sha": prev_sha,
                "after_sha": curr_sha,
                "before_mgas": prev_val,
                "after_mgas": curr_val,
                "change_pct": change_pct,
                "commit_index": i,
            }
    return worst


def discover_commits(results_dir):
    """Discover per-commit results from directory structure."""
    commits = []
    for entry in sorted(os.listdir(results_dir)):
        entry_path = os.path.join(results_dir, entry)
        if not os.path.isdir(entry_path):
            continue
        if not entry.startswith("retro-"):
            continue

        sha = entry[6:]

        json_files = [f for f in os.listdir(entry_path) if f.endswith("-full.json")]
        if not json_files:
            print(f"WARNING: No JSON results in {entry}", file=sys.stderr)
            continue

        merged = {}
        for jf in json_files:
            results = load_commit_results(os.path.join(entry_path, jf))
            merged.update(results)

        if merged:
            commits.append((sha, merged))

    return commits


def build_analysis(commits):
    """Build the full analysis data structure."""
    all_scenarios = sorted(set(s for _, results in commits for s in results))

    oldest_sha = commits[0][0]
    newest_sha = commits[-1][0]

    # Per-scenario analysis
    scenario_analysis = []
    for scenario in all_scenarios:
        # Build timeline: (sha, mgas) for commits that have this scenario
        timeline = []
        for sha, results in commits:
            data = results.get(scenario)
            if data and data["mgas"] > 0:
                timeline.append((sha, data["mgas"]))

        if len(timeline) < 2:
            continue

        first_val = timeline[0][1]
        last_val = timeline[-1][1]
        end_to_end_pct = ((last_val - first_val) / first_val) * 100

        inflections = find_inflection_points(timeline)
        largest_drop = find_largest_drop(timeline)

        values = [v for _, v in timeline]

        entry = {
            "scenario": scenario,
            "first_sha": timeline[0][0],
            "last_sha": timeline[-1][0],
            "first_mgas": first_val,
            "last_mgas": last_val,
            "end_to_end_change_pct": end_to_end_pct,
            "min_mgas": min(values),
            "max_mgas": max(values),
            "median_mgas": statistics.median(values),
            "data_points": len(timeline),
            "inflection_count": len(inflections),
            "inflections": inflections,
            "largest_drop": largest_drop,
            "timeline": [{"sha": sha, "mgas": round(v, 2)} for sha, v in timeline],
        }
        scenario_analysis.append(entry)

    # Compute per-commit aggregate stats
    commit_stats = []
    for sha, results in commits:
        values = [d["mgas"] for d in results.values() if d["mgas"] > 0]
        commit_stats.append({
            "sha": sha,
            "scenario_count": len(values),
            "avg_mgas": sum(values) / len(values) if values else 0,
            "median_mgas": statistics.median(values) if values else 0,
            "min_mgas": min(values) if values else 0,
            "max_mgas": max(values) if values else 0,
        })

    # Separate regressions and improvements (end-to-end)
    regressions = [s for s in scenario_analysis
                   if s["end_to_end_change_pct"] < -SIGNIFICANT_CHANGE_THRESHOLD]
    improvements = [s for s in scenario_analysis
                    if s["end_to_end_change_pct"] > SIGNIFICANT_CHANGE_THRESHOLD]

    return {
        "meta": {
            "oldest_sha": oldest_sha,
            "newest_sha": newest_sha,
            "commit_count": len(commits),
            "scenario_count": len(all_scenarios),
            "inflection_threshold_pct": INFLECTION_THRESHOLD,
            "significant_change_threshold_pct": SIGNIFICANT_CHANGE_THRESHOLD,
        },
        "commit_stats": commit_stats,
        "regressions": sorted(regressions, key=lambda s: s["end_to_end_change_pct"]),
        "improvements": sorted(improvements, key=lambda s: -s["end_to_end_change_pct"]),
        "all_scenarios": scenario_analysis,
    }


def format_markdown(analysis):
    """Format the analysis as a Markdown report."""
    meta = analysis["meta"]
    lines = []

    lines.append("## Gas Benchmark Retrospective Report")
    lines.append("")
    lines.append(f"**Commits analyzed:** {meta['commit_count']} "
                 f"(oldest: `{meta['oldest_sha'][:10]}`, newest: `{meta['newest_sha'][:10]}`)")
    lines.append(f"**Scenarios:** {meta['scenario_count']}  ")
    lines.append(f"**Inflection threshold:** {meta['inflection_threshold_pct']:.0f}% between consecutive commits  ")
    lines.append(f"**Regression threshold:** {meta['significant_change_threshold_pct']:.0f}% end-to-end")
    lines.append("")

    # ── Overall trend ────────────────────────────────────────────────────
    lines.append("### Overall Trend")
    lines.append("")
    lines.append("| # | Commit | Scenarios | Avg MGas/s | Median | Min | Max |")
    lines.append("|--:|--------|----------:|-----------:|-------:|----:|----:|")
    for i, cs in enumerate(analysis["commit_stats"]):
        lines.append(
            f"| {i+1} | `{cs['sha'][:10]}` | {cs['scenario_count']} "
            f"| {cs['avg_mgas']:.1f} | {cs['median_mgas']:.1f} "
            f"| {cs['min_mgas']:.1f} | {cs['max_mgas']:.1f} |")
    lines.append("")

    # ── Regressions with inflection detail ───────────────────────────────
    regressions = analysis["regressions"]
    if regressions:
        lines.append(f"### Regressions ({len(regressions)} scenarios)")
        lines.append("")
        lines.append("Each entry shows the scenario, overall change, and the **specific commit range** "
                     "where the largest drop occurred. Use `git log <before>..<after>` to identify the cause.")
        lines.append("")

        for s in regressions:
            drop = s["largest_drop"]
            lines.append(f"#### `{s['scenario']}`")
            lines.append(f"- **End-to-end:** {s['first_mgas']:.1f} → {s['last_mgas']:.1f} MGas/s "
                         f"(**{s['end_to_end_change_pct']:+.1f}%**)")
            if drop:
                lines.append(f"- **Largest drop:** {drop['before_mgas']:.1f} → {drop['after_mgas']:.1f} MGas/s "
                             f"(**{drop['change_pct']:+.1f}%**)")
                lines.append(f"- **Between commits:** `{drop['before_sha'][:10]}` → `{drop['after_sha'][:10]}`")
                lines.append(f"- **Bisect range:** `git log --oneline {drop['before_sha'][:10]}..{drop['after_sha'][:10]}`")
            if s["inflection_count"] > 1:
                lines.append(f"- **Total inflection points:** {s['inflection_count']} "
                             f"(performance changed {s['inflection_count']} times)")
            lines.append(f"- **Range:** {s['min_mgas']:.1f} – {s['max_mgas']:.1f} MGas/s "
                         f"(median {s['median_mgas']:.1f})")
            lines.append("")

    # ── Improvements ─────────────────────────────────────────────────────
    improvements = analysis["improvements"]
    if improvements:
        lines.append(f"### Improvements ({len(improvements)} scenarios)")
        lines.append("")
        lines.append("| Scenario | First | Last | Change | Largest jump between |")
        lines.append("|----------|------:|-----:|-------:|----------------------|")
        for s in improvements:
            jump_info = ""
            # Find the largest positive jump
            best_jump = None
            for inf in s["inflections"]:
                if inf["direction"] == "improvement":
                    if best_jump is None or inf["change_pct"] > best_jump["change_pct"]:
                        best_jump = inf
            if best_jump:
                jump_info = (f"`{best_jump['before_sha'][:10]}` → `{best_jump['after_sha'][:10]}` "
                             f"({best_jump['change_pct']:+.1f}%)")
            lines.append(
                f"| {s['scenario']} | {s['first_mgas']:.1f} | {s['last_mgas']:.1f} "
                f"| **{s['end_to_end_change_pct']:+.1f}%** | {jump_info} |")
        lines.append("")

    if not regressions and not improvements:
        lines.append(f":white_check_mark: No significant changes detected across "
                     f"{meta['commit_count']} commits.")
        lines.append("")

    # ── Per-scenario timeline (collapsed) ────────────────────────────────
    all_scenarios = analysis["all_scenarios"]
    commit_shas = [cs["sha"] for cs in analysis["commit_stats"]]

    lines.append("<details>")
    lines.append(f"<summary>Full per-scenario timeline "
                 f"({len(all_scenarios)} scenarios × {len(commit_shas)} commits)</summary>")
    lines.append("")

    header = "| Scenario | Change |"
    separator = "|----------|-------:|"
    for sha in commit_shas:
        header += f" `{sha[:7]}` |"
        separator += "--------:|"
    lines.append(header)
    lines.append(separator)

    for s in all_scenarios:
        # Build a lookup from sha to mgas for this scenario
        sha_to_mgas = {t["sha"]: t["mgas"] for t in s["timeline"]}
        row = f"| {s['scenario']} | {s['end_to_end_change_pct']:+.1f}% |"
        for sha in commit_shas:
            val = sha_to_mgas.get(sha)
            row += f" {val:.1f} |" if val else " — |"
        lines.append(row)

    lines.append("")
    lines.append("</details>")

    # ── Agent-friendly summary ───────────────────────────────────────────
    lines.append("")
    lines.append("### Agent Investigation Guide")
    lines.append("")
    if regressions:
        lines.append("To investigate each regression, run these commands:")
        lines.append("")
        lines.append("```bash")
        for s in regressions[:10]:  # Limit to top 10
            drop = s["largest_drop"]
            if drop:
                lines.append(f"# {s['scenario']} ({s['end_to_end_change_pct']:+.1f}%)")
                lines.append(f"git log --oneline {drop['before_sha'][:10]}..{drop['after_sha'][:10]}")
                lines.append("")
        lines.append("```")
        lines.append("")
        lines.append("For each commit range, look for changes to EVM instruction handlers, "
                     "gas cost tables, state access patterns, or precompile implementations.")
    else:
        lines.append("No regressions detected — no investigation needed.")

    return "\n".join(lines)


def format_json(analysis):
    """Format the analysis as machine-readable JSON."""
    # Round floats for cleaner output
    def round_floats(obj):
        if isinstance(obj, float):
            return round(obj, 2)
        if isinstance(obj, dict):
            return {k: round_floats(v) for k, v in obj.items()}
        if isinstance(obj, list):
            return [round_floats(v) for v in obj]
        return obj

    return json.dumps(round_floats(analysis), indent=2)


def main():
    json_mode = "--json" in sys.argv
    args = [a for a in sys.argv[1:] if a != "--json"]

    if len(args) != 1:
        print("Usage: retrospective_report.py <results_dir> [--json]", file=sys.stderr)
        sys.exit(1)

    results_dir = args[0]

    commits = discover_commits(results_dir)
    if not commits:
        print("No retrospective results found.", file=sys.stderr)
        sys.exit(1)

    print(f"Loaded {len(commits)} commits, "
          f"{sum(len(r) for _, r in commits)} total data points", file=sys.stderr)

    analysis = build_analysis(commits)

    if json_mode:
        print(format_json(analysis))
    else:
        print(format_markdown(analysis))


if __name__ == "__main__":
    main()
