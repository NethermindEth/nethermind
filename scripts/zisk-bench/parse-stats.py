#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Pull one block's guest cost out of a `ziskemu -X` log and append it to a results file.

The emulator prints a REPORT with the step count, then a COST DISTRIBUTION whose buckets are what
step count alone does not capture: PRECOMPILES is roughly a sixth of the bill while contributing
almost no steps, so a change that removes hashing shows up here and nowhere else.
"""

import argparse
import json
import pathlib
import re
import sys

# The pinned image groups digits (`STEPS 406,243,606`) even when passed `--no-thousands-sep`, so both
# patterns accept separators and `number()` strips them. The flag stays on the command line for the
# day the image honours it; nothing here depends on it either way.
NUMBER = r"(\d[\d,]*)"
STEPS = re.compile(rf"^STEPS\s+{NUMBER}\s*$", re.MULTILINE)
BUCKETS = ("MAIN", "OPCODES", "PRECOMPILES", "MEMORY", "TOTAL")


def number(text: str) -> int:
    return int(text.replace(",", ""))


def bucket(log: str, name: str) -> int:
    match = re.search(rf"^{name}\s+{NUMBER}\s", log, re.MULTILINE)
    if match is None:
        raise SystemExit(f"cost bucket {name!r} not found — did ziskemu run with -X?")
    return number(match.group(1))


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--input", required=True, help="Block input file name, used as the row key.")
    parser.add_argument("--log", required=True, type=pathlib.Path, help="Captured ziskemu output.")
    parser.add_argument("--into", required=True, type=pathlib.Path, help="JSON array to append to.")
    args = parser.parse_args()
    if re.fullmatch(r"[0-9]+\.ssz", args.input) is None:
        raise SystemExit("input must be a numeric block filename ending in .ssz")

    log = args.log.read_text(encoding="utf-8", errors="replace")

    steps = STEPS.search(log)
    if steps is None:
        raise SystemExit("no STEPS line in the log — the run did not complete")

    row = {"input": args.input, "steps": number(steps.group(1))}
    row.update({name.lower(): bucket(log, name) for name in BUCKETS})

    rows = json.loads(args.into.read_text(encoding="utf-8")) if args.into.exists() else []
    rows = [existing for existing in rows if existing["input"] != args.input]
    rows.append(row)
    rows.sort(key=lambda entry: entry["input"])
    args.into.write_text(json.dumps(rows, indent=2) + "\n", encoding="utf-8")

    print(f"{args.input}: {row['steps']} steps, {row['total']} cost")
    return 0


if __name__ == "__main__":
    sys.exit(main())
