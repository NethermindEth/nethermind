#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Reject BenchmarkDotNet runs with missing or failed BAL catch-up cases."""

import json
import pathlib
import re
import sys


def check(directory, expected_cases):
    directory = pathlib.Path(directory)
    if re.search(r"(?:^[ \t]*|--->[ \t]*(?:\(Inner Exception #\d+\)[ \t]*)?|Unhandled exception\.[ \t]*)[\w.]*Exception:|Invalid Blocks?",
                 (directory / "run.log").read_text(), re.MULTILINE):
        raise ValueError("Benchmark log contains an exception or invalid block")
    reports = list(directory.glob("results/*-report-full-compressed.json"))
    if not reports:
        raise ValueError("No BenchmarkDotNet JSON report was produced")
    count = 0
    for report in reports:
        cases = json.loads(report.read_text())["Benchmarks"]
        count += len(cases)
        if not cases or any(not case.get("Statistics") for case in cases):
            raise ValueError(f"Missing measurements in {report}")
    if count != expected_cases:
        raise ValueError(f"Expected {expected_cases} benchmark cases, got {count}")


if __name__ == "__main__":
    check(sys.argv[1], int(sys.argv[2]))
