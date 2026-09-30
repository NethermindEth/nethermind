#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Coverage for the instruction-count report of the EXPB instructions mode.

The report is what a PR author reads to tell a code change from run-to-run noise, so the parse of the client's
EXPB-COUNT lines and the block pairing between the two runs are pinned here.
"""

import importlib.util
import io
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCRIPT = ROOT / "scripts" / "expb" / "instruction_counts.py"

_spec = importlib.util.spec_from_file_location("instruction_counts", SCRIPT)
counts = importlib.util.module_from_spec(_spec)
sys.modules["instruction_counts"] = counts
_spec.loader.exec_module(counts)


def count_line(block, instr, *, txs=10, gas=1_000_000, cycles=None, alloc=4_000_000, gc="0/0/0", mux=0):
    """One EXPB-COUNT line as the client logs it, with the phases splitting the whole block 50/30/20."""
    cycles = cycles if cycles is not None else instr // 2
    exec_, roots = instr // 2, instr * 3 // 10
    commit = instr - exec_ - roots
    return (
        f"30 Sep 06:51:30 | EXPB-COUNT block={block} txs={txs} gas={gas} instr={instr} cycles={cycles} "
        f"alloc={alloc} gc={gc} mux={mux} jit=0 exec={exec_} roots={roots} commit={commit} "
        f"cyc={cycles // 2}/{cycles // 4}/{cycles - cycles // 2 - cycles // 4}"
    )


def report(*args):
    output = io.StringIO()
    with redirect_stdout(output):
        exit_code = counts.main(["report", *map(str, args)])
    assert exit_code == 0
    return output.getvalue()


class ParseTests(unittest.TestCase):
    def test_reads_the_block_lines_through_colors_and_other_output(self):
        text = "\n".join([
            "EXPB-COUNT armed: user-space instructions and cycles per block",
            "\x1b[97m" + count_line(101, 1_000_000) + "\x1b[0m",
            "some other client output",
            count_line(100, 2_000_000),
        ])
        rows = counts.parse_log(text)
        self.assertEqual([100, 101], [row["block"] for row in rows])
        first = rows[0]
        self.assertEqual(2_000_000, first["instr"])
        self.assertEqual((1_000_000, 600_000, 400_000), (first["exec"], first["roots"], first["commit"]))
        self.assertEqual(1_000_000, first["cycles"])
        self.assertEqual((500_000, 250_000, 250_000), (first["exec_cycles"], first["roots_cycles"], first["commit_cycles"]))
        self.assertEqual("0/0/0", first["gc"])

    def test_a_block_processed_twice_keeps_its_last_count(self):
        rows = counts.parse_log("\n".join([count_line(100, 1_000_000), count_line(100, 1_500_000)]))
        self.assertEqual([1_500_000], [row["instr"] for row in rows])

    def test_the_csv_holds_what_the_log_held(self):
        rows = counts.parse_log("\n".join(count_line(100 + index, 1_000_000 + index) for index in range(3)))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "counts.csv"
            counts.write_csv(rows, path)
            self.assertEqual(rows, counts.load(path))

    def test_measured_keeps_the_last_blocks_by_number(self):
        rows = counts.parse_log("\n".join(count_line(block, 1_000_000) for block in (103, 100, 102, 101)))
        self.assertEqual([102, 103], [row["block"] for row in counts.measured(rows, 2)])
        self.assertEqual(4, len(counts.measured(rows, None)))


class ReportTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.root = Path(directory.name)

    def write(self, name, lines):
        path = self.root / name
        path.write_text("\n".join(lines) + "\n", encoding="utf-8")
        return path

    def test_compares_the_same_blocks_and_names_the_baseline(self):
        # Master's block 99 is a warm-up block of its own run and block 103 is past the PR run's end: neither
        # can move the means, which cover the blocks both runs counted.
        master = self.write("master.log", [count_line(block, 1_000_000) for block in (99, 100, 101, 102, 103)])
        pr = self.write("pr.log", [count_line(100, 1_000_000), count_line(101, 1_000_000), count_line(102, 1_030_000)])
        text = report("--pr", pr, "--master", master, "--master-sha", "a" * 40, "--base-sha", "a" * 40)
        self.assertIn("| per block, mean of 3 blocks | master | PR | Δ |", text)
        self.assertIn("| **Instructions, whole block** | 1.00M | 1.01M | +1.00% |", text)
        # Cycles follow the same phase split; count_line gives each block cycles of half its instructions.
        self.assertIn("| **Cycles, whole block** | 0.50M | 0.51M | +1.00% (noise ±2%) |", text)
        self.assertIn("| &nbsp;&nbsp;transaction execution | 0.25M | 0.25M | +1.00% |", text)
        self.assertIn("Single blocks: median change 0.00%, 9 in 10 within 3.00%.", text)
        self.assertIn("| 102 | 10 | 1,000,000 | 1.00M | 1.03M | +3.00% |", text)
        self.assertIn("Baseline: master `aaaaaaaaaaaa`, the PR's base.", text)

    def test_says_when_master_is_not_the_prs_base(self):
        master = self.write("master.log", [count_line(100, 1_000_000)])
        pr = self.write("pr.log", [count_line(100, 1_000_000)])
        text = report("--pr", pr, "--master", master, "--master-sha", "b" * 40, "--base-sha", "a" * 40)
        self.assertIn("Baseline: master `bbbbbbbbbbbb`. The PR's base is `aaaaaaaaaaaa`", text)
        # Identical runs list no changed blocks.
        self.assertNotIn("<details>", text)

    def test_a_missing_baseline_shows_the_pr_run_alone(self):
        pr = self.write("pr.log", [count_line(100, 1_000_000)])
        text = report("--pr", pr, "--master", self.root / "absent.log")
        self.assertIn("| per block, mean of 1 blocks | PR |", text)
        self.assertIn("No master baseline with instruction counts was cached yet", text)
        # Without --master nothing was asked to compare, so nothing is said about a baseline.
        self.assertNotIn("No master baseline", report("--pr", pr))

    def test_labels_name_the_two_runs(self):
        first = self.write("run1.log", [count_line(100, 1_000_000)])
        second = self.write("run2.log", [count_line(100, 1_000_000)])
        text = report("--pr", second, "--master", first, "--labels", "run 1,run 2", "--title", "Run 2 against run 1")
        self.assertTrue(text.startswith("#### Run 2 against run 1\n"))
        self.assertIn("| per block, mean of 1 blocks | run 1 | run 2 | Δ |", text)
        self.assertNotIn("Baseline", text)
        missing = report("--pr", second, "--master", self.root / "absent.log", "--labels", "run 1,run 2")
        self.assertIn("run 1 has no counts to compare with, so this shows run 2 alone.", missing)
        self.assertNotIn("master", missing)

    def test_warns_about_windows_with_a_collection_or_multiplexed_counters(self):
        pr = self.write("pr.log", [count_line(100, 1_000_000, gc="1/0/0"), count_line(101, 1_000_000, mux=1)])
        self.assertIn("⚠️ 2 block windows", report("--pr", pr))

    def test_a_run_without_counts_says_so(self):
        self.assertIn("No instruction counts were produced", report("--pr", self.root / "absent.log"))
        empty = self.write("empty.log", ["EXPB-COUNT unavailable: perf_event_open failed"])
        self.assertIn("No instruction counts were produced", report("--pr", empty))


if __name__ == "__main__":  # pragma: no cover
    unittest.main()
