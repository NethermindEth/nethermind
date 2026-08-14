# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Shared utilities for EVM opcode benchmark comparison.

Used by the detect-noisy and compare steps in evm-opcode-benchmark-diff.yml.
"""

import glob
import os
import re
import statistics

ANSI_RE = re.compile(r"\x1B\[[0-9;]*[A-Za-z]")
VALUE_RE = re.compile(r"^\s*([0-9][0-9,]*(?:\.[0-9]+)?)\s*([a-zA-Zµμ]+)\s*$")
UNIT_TO_NS = {
    "ns": 1.0,
    "us": 1_000.0,
    "µs": 1_000.0,
    "μs": 1_000.0,
    "ms": 1_000_000.0,
    "s": 1_000_000_000.0,
}


def read_env_config():
    """Read benchmark comparison thresholds from environment variables."""
    return {
        "default_threshold": float(os.environ.get("THRESHOLD_PERCENT", "5")),
        "noise_multiplier": float(os.environ.get("NOISE_MULTIPLIER", "2.0")),
        "error_multiplier": float(os.environ.get("ERROR_MULTIPLIER", "1.0")),
        "abs_delta_ns_floor": float(os.environ.get("ABS_DELTA_NS_FLOOR", "2.0")),
        "delta_margin_percent": float(os.environ.get("DELTA_MARGIN_PERCENT", "2.0")),
    }


def collect_logs(base_pattern="evm-opcodes-base*.log", pr_pattern="evm-opcodes-pr*.log"):
    """Collect and sort benchmark log files, with fallback defaults."""
    base_logs = sorted(glob.glob(base_pattern))
    pr_logs = sorted(glob.glob(pr_pattern))
    if not base_logs:
        base_logs = ["evm-opcodes-base.log"]
    if not pr_logs:
        pr_logs = ["evm-opcodes-pr.log"]
    return base_logs, pr_logs


def normalize_text(text):
    """Strip ANSI escape codes and non-breaking spaces."""
    text = text.replace("\xa0", " ")
    return ANSI_RE.sub("", text)


def parse_ns(value):
    """Parse a BenchmarkDotNet timing value (e.g. '12.34 ns') to nanoseconds."""
    m = VALUE_RE.match(value.strip())
    if not m:
        return None
    number = float(m.group(1).replace(",", ""))
    unit = m.group(2)
    scale = UNIT_TO_NS.get(unit)
    if scale is None:
        return None
    return number * scale


def cv_percent(mean, stddev):
    """Compute coefficient of variation as a percentage."""
    if mean is None or stddev is None or mean <= 0:
        return None
    return (stddev / mean) * 100.0


def fmt_cv(mean, stddev):
    """Format coefficient of variation for display."""
    if mean is None or stddev is None or mean == 0:
        return "N/A"
    cv = (stddev / mean) * 100
    return f"{cv:.1f}%"


def uncertainty_floor_percent(base_val, base_error, pr_error, error_multiplier):
    """Compute uncertainty floor from BDN Error columns as a percentage."""
    if base_val is None or base_val <= 0:
        return None
    if base_error is None and pr_error is None:
        return None
    be = base_error or 0.0
    pe = pr_error or 0.0
    return ((be + pe) / base_val) * 100.0 * error_multiplier


def find_col(headers, name):
    """Find column index by name, or None if missing."""
    return headers.index(name) if name in headers else None


def pick_median(values):
    """Return the median of non-None values, or None if empty."""
    values = [v for v in values if v is not None]
    if not values:
        return None
    return statistics.median(values)


def extract_opcode_data(path):
    """Extract opcode stats (median, mean, error, stddev, threshold) from a BDN log file."""
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        text = normalize_text(f.read())

    lines = text.splitlines()
    header_idx = -1
    for i, line in enumerate(lines):
        if line.strip().startswith("|") and "Opcode" in line and "Mean" in line:
            header_idx = i

    if header_idx < 0:
        return {}

    headers = [c.strip() for c in lines[header_idx].strip().strip("|").split("|")]
    opcode_col = find_col(headers, "Opcode")
    median_col = find_col(headers, "Median")
    mean_col = find_col(headers, "Mean")
    error_col = find_col(headers, "Error")
    stddev_col = find_col(headers, "StdDev")
    threshold_col = find_col(headers, "Threshold")

    if opcode_col is None or mean_col is None:
        return {}

    data = {}
    i = header_idx + 2
    while i < len(lines):
        line = lines[i].strip()
        if not line.startswith("|"):
            break

        cells = [c.strip() for c in line.strip("|").split("|")]
        if len(cells) <= max(opcode_col, mean_col):
            i += 1
            continue

        opcode = cells[opcode_col]
        mean = parse_ns(cells[mean_col])
        if opcode and mean is not None:
            median = parse_ns(cells[median_col]) if median_col is not None and len(cells) > median_col else None
            error = parse_ns(cells[error_col]) if error_col is not None and len(cells) > error_col else None
            stddev = parse_ns(cells[stddev_col]) if stddev_col is not None and len(cells) > stddev_col else None
            threshold = None
            if threshold_col is not None and len(cells) > threshold_col:
                try:
                    threshold = float(cells[threshold_col])
                except (ValueError, IndexError):
                    pass
            data[opcode] = {"median": median, "mean": mean, "error": error, "stddev": stddev, "threshold": threshold}
        i += 1

    return data


def aggregate(log_paths):
    """Aggregate opcode data across multiple benchmark log files using median."""
    runs = [extract_opcode_data(path) for path in log_paths]
    all_opcodes = sorted(set().union(*(r.keys() for r in runs)))
    result = {}
    for opcode in all_opcodes:
        rows = [r[opcode] for r in runs if opcode in r]
        result[opcode] = {
            "median": pick_median([x.get("median") for x in rows]),
            "mean": pick_median([x.get("mean") for x in rows]),
            "error": pick_median([x.get("error") for x in rows]),
            "stddev": pick_median([x.get("stddev") for x in rows]),
            "threshold": pick_median([x.get("threshold") for x in rows]),
        }
    return result


def compare_opcodes(base_data, pr_data, config):
    """Compare base vs PR opcode data and return per-opcode comparison results.

    Returns a list of (opcode, info) tuples for every opcode. Each info dict contains:
      base_val, pr_val, delta_pct, delta_abs_ns,
      base_mean, pr_mean, base_error, pr_error, base_stddev, pr_stddev,
      threshold, noise_floor, uncertainty_floor, effective_threshold,
      is_flagged, is_noisy
    """
    results = []
    for opcode in sorted(set(base_data.keys()) | set(pr_data.keys())):
        b = base_data.get(opcode)
        p = pr_data.get(opcode)
        base_val = (b.get("median") or b.get("mean")) if b else None
        pr_val = (p.get("median") or p.get("mean")) if p else None
        base_mean = b["mean"] if b else None
        pr_mean = p["mean"] if p else None
        base_error = b.get("error") if b else None
        pr_error = p.get("error") if p else None
        base_stddev = b["stddev"] if b else None
        pr_stddev = p["stddev"] if p else None
        threshold = (b or p or {}).get("threshold") or config["default_threshold"]

        base_cv_pct = cv_percent(base_mean, base_stddev)
        pr_cv_pct = cv_percent(pr_mean, pr_stddev)
        cv_values = [v for v in (base_cv_pct, pr_cv_pct) if v is not None]
        noise_floor = (max(cv_values) * config["noise_multiplier"]) if cv_values else 0.0
        uf = uncertainty_floor_percent(base_val, base_error, pr_error, config["error_multiplier"]) or 0.0
        effective_threshold = max(threshold, noise_floor, uf)

        delta_pct = None
        delta_abs_ns = None
        is_flagged = False
        is_noisy = noise_floor > threshold or uf > threshold

        if base_val is None or pr_val is None:
            # New or removed opcode
            is_flagged = True
        elif base_val == 0:
            is_flagged = pr_val != 0
        else:
            delta_pct = ((pr_val - base_val) / base_val) * 100.0
            delta_abs_ns = abs(pr_val - base_val)
            is_flagged = (
                abs(delta_pct) >= (effective_threshold + config["delta_margin_percent"])
                and delta_abs_ns >= config["abs_delta_ns_floor"]
            )

        results.append((opcode, {
            "base_val": base_val,
            "pr_val": pr_val,
            "delta_pct": delta_pct,
            "delta_abs_ns": delta_abs_ns,
            "base_mean": base_mean,
            "pr_mean": pr_mean,
            "base_error": base_error,
            "pr_error": pr_error,
            "base_stddev": base_stddev,
            "pr_stddev": pr_stddev,
            "threshold": threshold,
            "noise_floor": noise_floor,
            "uncertainty_floor": uf,
            "effective_threshold": effective_threshold,
            "is_flagged": is_flagged,
            "is_noisy": is_noisy,
        }))
    return results
