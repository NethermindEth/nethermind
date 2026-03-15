#!/usr/bin/env python3
"""Generate quick-subset.txt for gas benchmarks.

Analyzes all test scenarios, groups by test function name, identifies
opcode/variant axes within parameters, and picks representative subsets
that maintain full opcode coverage with ~20% of the total scenarios.

Usage:
    python3 scripts/benchmarks/generate_quick_subset.py > scripts/benchmarks/quick-subset.txt
"""

import os
import re
import sys
from collections import defaultdict

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.normpath(os.path.join(SCRIPT_DIR, '..', '..'))
TESTING_DIR = os.path.join(REPO_ROOT, 'tools', 'gas-benchmarks', 'eest_tests', 'testing')

# Known opcode axes: (function_name, regex to extract opcode from params)
# These are parameter dimensions that represent distinct opcodes/operations
# and must each be represented in the quick subset.
OPCODE_AXES = {
    'log':                      re.compile(r'(log\d)$'),
    'log_benchmark':            re.compile(r'(log\d)$'),
    'memory_access':            re.compile(r'opcode_(MLOAD|MSTORE8|MSTORE)'),
    'push':                     re.compile(r'(push\d+)'),
    'swap':                     re.compile(r'(swap\d+)'),
    'dup':                      re.compile(r'(dup\d+)'),
    'ext_account_query_warm':   re.compile(r'opcode_(\w+)'),
    'ext_account_query_cold':   re.compile(r'opcode_(\w+)'),
    'mcopy':                    re.compile(r'(overlap_\w+)'),
    'storage_access_cold':      re.compile(r'(SSLOAD|SSTORE \w+)'),
    'storage_access_warm':      re.compile(r'(SSLOAD|SSTORE \w+)'),
    'return_revert':            re.compile(r'opcode_(RETURN|REVERT)'),
    'calldatacopy_from_origin': re.compile(r'mem_size_(\d+)'),
    'calldatacopy_from_call':   re.compile(r'mem_size_(\d+)'),
    'returndatacopy':           re.compile(r'mem_size_(\d+)'),
    'create':                   re.compile(r'opcode_(CREATE2|CREATE)\b'),
}

# For modexp: group by the "type" of test (gas_balanced, gas_base_heavy, gas_exp_heavy, mod_even, mod_odd, fixed sizes)
MODEXP_PATTERN = re.compile(r'gas_(balanced|base_heavy|exp_heavy)|mod_(even|odd)|mod_(\d+)_exp_(\d+)')


def parse_filename(filename):
    """Extract test function name and parameters from a test filename."""
    m = re.match(
        r'.*__test_(\w+)\[fork_Prague-benchmark-blockchain_test_engine_x-(.+?)\]-gas-value_100M\.txt$',
        filename)
    if m:
        return m.group(1), m.group(2)

    m = re.match(
        r'.*__test_(\w+)\[fork_Prague-benchmark-blockchain_test_engine_x\]-gas-value_100M\.txt$',
        filename)
    if m:
        return m.group(1), ""

    # Fallback: just extract function name
    m = re.match(r'.*__test_(\w+)', filename)
    if m:
        return m.group(1), ""

    return None, None


def pick_spaced(items, count):
    """Pick evenly-spaced items from a list."""
    n = len(items)
    if n <= count:
        return list(items)
    if count == 1:
        return [items[n // 2]]
    indices = sorted(set(int(i * (n - 1) / (count - 1)) for i in range(count)))
    return [items[i] for i in indices]


def classify_modexp(params):
    """Classify a modexp scenario into a category for representative picking."""
    if 'gas_balanced' in params:
        return 'gas_balanced'
    if 'gas_base_heavy' in params:
        return 'gas_base_heavy'
    if 'gas_exp_heavy' in params:
        return 'gas_exp_heavy'
    if 'mod_even' in params:
        return 'mod_even'
    if 'mod_odd' in params:
        return 'mod_odd'
    return 'fixed_size'


def select_for_group(func_name, items):
    """Select representative scenarios for a test function group.

    items: list of (dir_name, filename, params)
    Returns: list of (dir_name, params) to include.
    """
    if len(items) <= 3:
        return [(d, p) for d, _, p in items]

    # Special case: modexp has many variants, group by category
    if func_name == 'modexp':
        categories = defaultdict(list)
        for dir_name, _, params in items:
            cat = classify_modexp(params)
            categories[cat].append((dir_name, params))

        selected = []
        for cat in sorted(categories):
            cat_items = categories[cat]
            # Pick 1-2 per category
            picks = pick_spaced(cat_items, 2 if len(cat_items) >= 4 else 1)
            selected.extend(picks)
        return selected

    # Check if this function has a known opcode axis
    opcode_re = OPCODE_AXES.get(func_name)
    if opcode_re:
        opcode_groups = defaultdict(list)
        ungrouped = []
        for dir_name, _, params in items:
            m = opcode_re.search(params)
            if m:
                opcode_groups[m.group(1)].append((dir_name, params))
            else:
                ungrouped.append((dir_name, params))

        if opcode_groups:
            selected = []
            # Pick 1-2 representatives per opcode variant
            reps = 2 if len(opcode_groups) <= 8 else 1
            for key in sorted(opcode_groups):
                picks = pick_spaced(opcode_groups[key], reps)
                selected.extend(picks)
            if ungrouped:
                selected.extend(pick_spaced(ungrouped, 2))
            return selected

    # Default: pick evenly spaced
    max_picks = 3 if len(items) <= 15 else 5
    return [(d, p) for d, _, p in pick_spaced(items, max_picks)]


def main():
    if not os.path.isdir(TESTING_DIR):
        print(f"ERROR: testing directory not found: {TESTING_DIR}", file=sys.stderr)
        sys.exit(1)

    # Collect all scenarios
    scenarios = []
    for dir_name in sorted(os.listdir(TESTING_DIR)):
        dir_path = os.path.join(TESTING_DIR, dir_name)
        if not os.path.isdir(dir_path):
            continue
        for f in sorted(os.listdir(dir_path)):
            if 'setup' in f.lower():
                continue
            func_name, params = parse_filename(f)
            if func_name:
                scenarios.append((dir_name, f, func_name, params))

    if not scenarios:
        print("ERROR: no scenarios found", file=sys.stderr)
        sys.exit(1)

    # Group by function name
    groups = defaultdict(list)
    for dir_name, filename, func_name, params in scenarios:
        groups[func_name].append((dir_name, filename, params))

    # Select representatives for each group
    selected = []  # (dir_name, func_name, params)
    stats = []

    for func_name in sorted(groups):
        items = groups[func_name]
        picks = select_for_group(func_name, items)
        for dir_name, params in picks:
            selected.append((dir_name, func_name, params))
        stats.append((func_name, len(items), len(picks)))

    selected.sort(key=lambda x: x[0])

    # Print to stdout
    total = len(scenarios)
    subset_size = len(selected)
    pct = 100 * subset_size / total if total > 0 else 0

    print(f"# Gas benchmarks quick subset")
    print(f"# Generated by: python3 scripts/benchmarks/generate_quick_subset.py")
    print(f"# {subset_size} of {total} scenarios ({pct:.0f}%)")
    print(f"# Maintains full opcode/function coverage with fewer parameter variations.")
    print(f"#")
    print(f"# Format: one directory name per line. Lines starting with # are comments.")
    print(f"# Inline comments after directory name are allowed (separated by whitespace + #).")

    current_func = None
    for dir_name, func_name, params in selected:
        if func_name != current_func:
            print()
            print(f"# --- {func_name} ---")
            current_func = func_name
        suffix = f"  # {params}" if params else ""
        print(f"{dir_name}{suffix}")

    # Print stats to stderr
    print(f"\n=== Quick subset: {subset_size} of {total} scenarios ({pct:.0f}%) ===", file=sys.stderr)
    print(f"\n{'Function':<35} {'Total':>5} {'Quick':>5} {'Pct':>5}", file=sys.stderr)
    print("-" * 55, file=sys.stderr)
    for func_name, total_count, quick_count in sorted(stats, key=lambda x: -x[1]):
        p = 100 * quick_count / total_count if total_count > 0 else 0
        print(f"{func_name:<35} {total_count:>5} {quick_count:>5} {p:>4.0f}%", file=sys.stderr)


if __name__ == '__main__':
    main()
