#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Validate EXPB_NETHERMIND_PRIORITY_MODE records in an EXPB log."""

from __future__ import annotations

import argparse
import re
import sys
from dataclasses import dataclass
from pathlib import Path

MARKER = "EXPB_PRIORITY"
REQUIRED_FIELDS = {
    "mode",
    "tid",
    "policy_before",
    "nice_before",
    "managed_during",
    "policy_during",
    "nice_during",
    "policy_after",
    "nice_after",
    "success",
}
TOKEN = re.compile(r"(?P<key>[A-Za-z_][A-Za-z0-9_]*)=(?P<value>[^\s]+)")
ANSI_ESCAPE = re.compile(r"\x1b\[[0-?]*[ -/]*[@-~]")
KNOWN_POLICIES = {
    "SCHED_OTHER",
    "SCHED_FIFO",
    "SCHED_RR",
    "SCHED_BATCH",
    "SCHED_IDLE",
    "SCHED_DEADLINE",
}


@dataclass(frozen=True)
class PriorityRecord:
    values: dict[str, str]
    line_number: int

    def __getitem__(self, key: str) -> str:
        return self.values[key]


def parse_records(lines: list[str]) -> tuple[list[PriorityRecord], list[str]]:
    records: list[PriorityRecord] = []
    errors: list[str] = []
    for line_number, line in enumerate(lines, start=1):
        line = ANSI_ESCAPE.sub("", line)
        marker_index = line.find(MARKER)
        if marker_index == -1:
            continue
        values = {match["key"]: match["value"] for match in TOKEN.finditer(line[marker_index:])}
        missing = sorted(REQUIRED_FIELDS - values.keys())
        if missing:
            errors.append(f"line {line_number}: missing fields: {', '.join(missing)}")
            continue
        records.append(PriorityRecord(values, line_number))
    return records, errors


def validate_records(
    records: list[PriorityRecord],
    parse_errors: list[str] | None = None,
    expected_mode: str | None = None,
) -> list[str]:
    errors = list(parse_errors or [])
    if not records:
        errors.append("no valid EXPB_PRIORITY records found")
        return errors

    modes = {record["mode"] for record in records}
    if expected_mode is not None and modes != {expected_mode}:
        errors.append(
            f"expected mode {expected_mode}, found {', '.join(sorted(modes))}"
        )
    elif len(modes) != 1:
        errors.append(f"mixed priority modes in one log: {', '.join(sorted(modes))}")

    for record in records:
        prefix = f"line {record.line_number}"
        if record["mode"] not in {"observe", "nice"}:
            errors.append(f"{prefix}: unsupported mode {record['mode']}")
        if record["success"].lower() != "true":
            errors.append(f"{prefix}: priority record reports success={record['success']}")
        try:
            tid = int(record["tid"])
            nice_before = int(record["nice_before"])
            nice_during = int(record["nice_during"])
            nice_after = int(record["nice_after"])
        except ValueError as error:
            errors.append(f"{prefix}: invalid numeric field: {error}")
            continue
        if tid <= 0:
            errors.append(f"{prefix}: tid must be positive")
        for field, value in (
            ("nice_before", nice_before),
            ("nice_during", nice_during),
            ("nice_after", nice_after),
        ):
            if not -20 <= value <= 19:
                errors.append(f"{prefix}: {field} is outside Linux nice range [-20, 19]")
        if record["managed_during"] != "Highest":
            errors.append(
                f"{prefix}: managed_during must be Highest, got {record['managed_during']}"
            )

        policies = (
            record["policy_before"],
            record["policy_during"],
            record["policy_after"],
        )
        unknown_policies = sorted(set(policies) - KNOWN_POLICIES)
        if unknown_policies:
            errors.append(
                f"{prefix}: unknown scheduling policy: {', '.join(unknown_policies)}"
            )
        if len(set(policies)) != 1:
            errors.append(
                f"{prefix}: scheduling policy was not restored ({' -> '.join(policies)})"
            )
        if nice_after != nice_before:
            errors.append(
                f"{prefix}: nice value was not restored ({nice_before} -> {nice_after})"
            )

        if record["mode"] == "observe":
            if nice_during != nice_before:
                errors.append(
                    f"{prefix}: observe arm changed nice from {nice_before} to {nice_during}"
                )
        elif nice_during != -5:
            errors.append(f"{prefix}: nice arm did not set nice=-5 (got {nice_during})")
        elif nice_before <= -5:
            errors.append(
                f"{prefix}: nice arm baseline nice={nice_before} cannot demonstrate a raise to -5"
            )
        if record["policy_before"] != "SCHED_OTHER":
            errors.append(
                f"{prefix}: expected baseline policy SCHED_OTHER, got {record['policy_before']}"
            )
    return errors


def verify_log(path: Path, expected_mode: str | None = None) -> list[str]:
    records, parse_errors = parse_records(path.read_text(errors="replace").splitlines())
    return validate_records(records, parse_errors, expected_mode)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("log", type=Path, help="EXPB raw run log")
    parser.add_argument(
        "--mode",
        choices=("observe", "nice"),
        help="Require records for this arm; otherwise infer one mode from the log",
    )
    args = parser.parse_args(argv)
    errors = verify_log(args.log, args.mode)
    if errors:
        print("priority verification failed:", file=sys.stderr)
        for error in errors:
            print(f"  {error}", file=sys.stderr)
        return 1
    print(f"priority verification passed: {args.log}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
