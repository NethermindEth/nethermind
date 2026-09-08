#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
import unittest.mock


def load(name: str):
    path = Path(__file__).with_name(name)
    specification = importlib.util.spec_from_file_location(name.replace("-", "_")[:-3], path)
    if specification is None or specification.loader is None:
        raise RuntimeError(f"Unable to load {path}")
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module


PARSER = load("parse-stats.py")
REPORT = load("report.py")

# Verbatim shape of `ziskemu -X`, which is what the parse is pinned to: the output hash first, then
# the REPORT, then the cost distribution with its own separator rows.
ZISKEMU_LOG = """d38ffa0643d60c76a25c4ab4ffeeb40d99f2dbedf240b3854b23a9d50233a8330101000000000000000100

REPORT
----------------------------------------
STEPS                        437561208

COST DISTRIBUTION                   COST       %
------------------------------------------------
MAIN                      29754162144  59.88%
OPCODES                    6287492347  12.65%
PRECOMPILES                8023714093  16.15%
MEMORY                     5334595573  10.74%
                        ------------------------
VARIABLE                  49399964157  99.42%
BASE                        287309824   0.58%
                        ------------------------
TOTAL                     49687273981 100.00%
"""

# Copied out of the measurement job for block 25526356 (actions/runs/34105317341). The pinned image
# groups digits even though the recipe passes `--no-thousands-sep`, which is why both parsers have to
# accept separators; before they did, the very first block failed with "no STEPS line in the log".
# cspell:ignore FROPS
ZISKEMU_LOG_WITH_SEPARATORS = """018cc34e1eb14c42412dc26aeca8e7e1bf540f216753c7431568985b66ecca1f0101000000000000000100

REPORT\x20
----------------------------------------
STEPS                        406,243,606

COST DISTRIBUTION                   COST       %
------------------------------------------------
MAIN                      27,624,565,208  61.34%
OPCODES                    6,223,986,843  13.82%
PRECOMPILES                6,875,247,706  15.27%
MEMORY                     4,027,415,979   8.94%
                        ------------------------
VARIABLE                  44,751,215,736  99.36%
BASE                         287,309,824   0.64%
                        ------------------------
TOTAL                     45,038,525,560 100.00%

FROPS                      4,877,143,744  10.90%
ROM USAGE                        878,275  20.94%

COST BY BASE OPCODE                COUNT       %            COST       %
------------------------------------------------------------------------
OP and                        18,991,411   4.67%   1,139,484,660   2.55%
OP add                        46,947,817  11.56%     710,601,755   1.59%

COST BY PRECOMPILED OPCODE           COUNT       %            COST       %
--------------------------------------------------------------------------
OP keccak                        165,632   0.04%   6,369,212,928  14.23%
"""


def row(name, steps, total, main=1, opcodes=1, precompiles=1, memory=1):
    return {
        "input": name, "steps": steps, "total": total,
        "main": main, "opcodes": opcodes, "precompiles": precompiles, "memory": memory,
    }


class ParseStatsTests(unittest.TestCase):
    def parse(self, log, name="25532382.ssz", into=None):
        with tempfile.TemporaryDirectory() as directory:
            log_path = Path(directory) / "stats.log"
            log_path.write_text(log, encoding="utf-8")
            results = Path(directory) / "results.json"
            if into is not None:
                results.write_text(json.dumps(into), encoding="utf-8")

            argv = [
                "parse-stats.py", "--input", name, "--log", str(log_path), "--into", str(results),
            ]
            with unittest.mock.patch.object(sys, "argv", argv):
                PARSER.main()
            return json.loads(results.read_text(encoding="utf-8"))

    def test_reads_steps_and_every_cost_bucket(self):
        rows = self.parse(ZISKEMU_LOG)

        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0], {
            "input": "25532382.ssz",
            "steps": 437561208,
            "main": 29754162144,
            "opcodes": 6287492347,
            "precompiles": 8023714093,
            "memory": 5334595573,
            "total": 49687273981,
        })

    def test_reads_the_grouped_digits_the_pinned_image_actually_prints(self):
        rows = self.parse(ZISKEMU_LOG_WITH_SEPARATORS, name="25526356.ssz")

        self.assertEqual(rows[0], {
            "input": "25526356.ssz",
            "steps": 406243606,
            "main": 27624565208,
            "opcodes": 6223986843,
            "precompiles": 6875247706,
            "memory": 4027415979,
            "total": 45038525560,
        })

    def test_a_fuller_report_does_not_disturb_the_parse(self):
        # `--mem-stats` adds sections of its own, including further TOTAL rows. Adding that flag one day
        # should not change any figure here. (The cost-distribution TOTAL happens to come first, so this
        # is a regression guard against the sections being reordered or the rows renamed, not proof that
        # the pattern could not match a memory subtotal.)
        log = ZISKEMU_LOG + """
MEM COST BY TYPE                   COUNT       %            COST       %
------------------------------------------------------------------------
TOTAL ALIGNED                129709872  79.22%      2214363794  41.51%
TOTAL UNALIGNED               34029073  20.78%      3120231779  58.49%
TOTAL RAM                    159208872  97.23%      5173133731  96.97%
"""

        rows = self.parse(log)

        self.assertEqual(rows[0]["total"], 49687273981)

    def test_appends_and_keeps_one_row_per_block(self):
        existing = [row("25526356.ssz", 1, 2)]

        rows = self.parse(ZISKEMU_LOG, into=existing)

        self.assertEqual([entry["input"] for entry in rows], ["25526356.ssz", "25532382.ssz"])

    def test_re_measuring_a_block_replaces_its_row(self):
        existing = [row("25532382.ssz", 999, 999)]

        rows = self.parse(ZISKEMU_LOG, into=existing)

        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["steps"], 437561208)

    def test_rejects_a_log_with_no_step_count(self):
        with self.assertRaises(SystemExit):
            self.parse("d38ffa\nnothing useful here\n")

    def test_rejects_a_log_with_no_cost_model(self):
        # A run without -X still prints steps, and its figures would silently be half a measurement.
        with self.assertRaises(SystemExit):
            self.parse("STEPS                        437561208\n")


class ReportTests(unittest.TestCase):
    def test_percentages_are_signed_and_per_block(self):
        current = {"a.ssz": row("a.ssz", 90, 900)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000)}

        body, regressed = REPORT.render(current, baseline, "c0ffee")

        self.assertIn("-10.000%", body)
        self.assertFalse(regressed)

    def test_a_cost_increase_is_flagged_as_a_regression(self):
        current = {"a.ssz": row("a.ssz", 100, 1100)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000)}

        _, regressed = REPORT.render(current, baseline, "c0ffee")

        self.assertTrue(regressed)

    def test_a_rounding_level_change_is_not_flagged(self):
        current = {"a.ssz": row("a.ssz", 100, 1_000_000)}
        baseline = {"a.ssz": row("a.ssz", 100, 999_999)}

        _, regressed = REPORT.render(current, baseline, "c0ffee")

        self.assertFalse(regressed)

    def test_a_step_regression_alone_is_not_flagged(self):
        # Steps can rise while cost falls — widening a field does exactly that — so the gate is on cost.
        current = {"a.ssz": row("a.ssz", 110, 900)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000)}

        _, regressed = REPORT.render(current, baseline, "c0ffee")

        self.assertFalse(regressed)

    def test_totals_cover_only_blocks_present_in_both(self):
        # A block the baseline never measured would otherwise inflate the aggregate as pure growth.
        current = {"a.ssz": row("a.ssz", 100, 1000), "new.ssz": row("new.ssz", 500, 5000)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000)}

        body, _ = REPORT.render(current, baseline, "c0ffee")

        self.assertIn("**all 1 blocks**", body)
        self.assertIn("| new | 500 | new | 5,000 | new |", body)

    def test_a_flagged_regression_is_visible_in_the_body_not_only_the_output(self):
        current = {"a.ssz": row("a.ssz", 100, 1100)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000)}

        body, _ = REPORT.render(current, baseline, "c0ffee")

        self.assertIn("Prover cost is up against the baseline", body)

    def test_the_per_block_threshold_also_covers_the_totals_row(self):
        # The aggregate delta is a cost-weighted mean of the per-block ones, so it cannot exceed the
        # largest of them: a totals row over the threshold always has a block over it too.
        current = {"big.ssz": row("big.ssz", 100, 1_000_000), "small.ssz": row("small.ssz", 100, 1_100)}
        baseline = {"big.ssz": row("big.ssz", 100, 1_000_000), "small.ssz": row("small.ssz", 100, 1_000)}

        body, regressed = REPORT.render(current, baseline, "c0ffee")

        self.assertTrue(regressed)
        self.assertIn("**+0.010%**", body)  # the totals row alone would have stayed quiet

    def test_a_block_dropped_from_the_benchmark_is_called_out(self):
        # Silence here would shrink the benchmark without shrinking the confidence in it.
        current = {"a.ssz": row("a.ssz", 100, 1000)}
        baseline = {"a.ssz": row("a.ssz", 100, 1000), "gone.ssz": row("gone.ssz", 100, 1000)}

        body, _ = REPORT.render(current, baseline, "c0ffee")

        self.assertIn("this run did not: gone", body)

    def test_the_baseline_commit_is_named_so_the_delta_can_be_attributed(self):
        rows = {"a.ssz": row("a.ssz", 100, 1000)}

        body, _ = REPORT.render(rows, rows, "c0ffee", "ba5e1111aaaa", "ba5e1111aaaa")

        self.assertIn("Compared against `ba5e1111aaaa`.", body)

    def test_a_baseline_from_another_commit_says_so(self):
        # `restore-keys` serves the newest matching cache, which need not be the pull request's base.
        rows = {"a.ssz": row("a.ssz", 100, 1000)}

        body, _ = REPORT.render(rows, rows, "c0ffee", "0lde5t000000", "ba5e1111aaaa")

        self.assertIn("not this pull request's base (`ba5e1111aaaa`)", body)

    def test_an_unrecorded_baseline_is_not_passed_off_as_a_known_commit(self):
        rows = {"a.ssz": row("a.ssz", 100, 1000)}

        body, _ = REPORT.render(rows, rows, "c0ffee")

        self.assertIn("Compared against an unrecorded master commit.", body)

    def test_without_a_baseline_it_reports_absolutes_only(self):
        current = {"a.ssz": row("a.ssz", 100, 1000)}

        body, regressed = REPORT.render(current, None, "c0ffee")

        self.assertIn("No baseline was restored", body)
        self.assertNotIn("Δ", body)
        self.assertFalse(regressed)

    def test_body_carries_the_marker_so_the_comment_is_updated_not_duplicated(self):
        body, _ = REPORT.render({"a.ssz": row("a.ssz", 1, 1)}, None, "c0ffee")

        self.assertTrue(body.startswith(REPORT.MARKER))

    def test_block_number_is_shown_without_the_extension(self):
        body, _ = REPORT.render({"25532382.ssz": row("25532382.ssz", 1, 1)}, None, "c0ffee")

        self.assertIn("| 25532382 |", body)
        self.assertNotIn("25532382.ssz", body)


class BaselineFileTests(unittest.TestCase):
    """A measured file is the bare array `parse-stats.py` writes; a staged baseline wraps it."""

    def load(self, document):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "rows.json"
            path.write_text(json.dumps(document), encoding="utf-8")
            return REPORT.load(path)

    def test_reads_a_freshly_measured_array(self):
        rows, commit = self.load([row("a.ssz", 100, 1000)])

        self.assertEqual(list(rows), ["a.ssz"])
        self.assertEqual(commit, "")

    def test_reads_a_staged_baseline_and_its_commit(self):
        rows, commit = self.load({"commit": "ba5e1111aaaa", "rows": [row("a.ssz", 100, 1000)]})

        self.assertEqual(list(rows), ["a.ssz"])
        self.assertEqual(commit, "ba5e1111aaaa")


class InputListTests(unittest.TestCase):
    """`inputs.json` and the correctness workflow's matrix must name the same blocks.

    They are two lists because converting the merge-gating matrix to read the file is a change to a
    workflow that cannot be exercised locally. Keeping them in step is therefore enforced here: a
    benchmark reporting on a different set of blocks than the tests verify would be quietly misleading.
    """

    REPOSITORY = Path(__file__).resolve().parents[2]

    def test_matches_the_stateless_test_matrix(self):
        import re

        workflow = (self.REPOSITORY / ".github/workflows/stateless-tests.yml").read_text(encoding="utf-8")
        start = workflow.index("        include:")
        matrix = re.findall(
            r"- input: (\S+)\s+hash: (\S+)\s+output: (\S+)",
            workflow[start:workflow.index("    steps:", start)],
        )

        inputs = json.loads(
            (self.REPOSITORY / "src/Nethermind/Nethermind.Stateless.ZiskGuest/inputs.json")
            .read_text(encoding="utf-8")
        )

        self.assertEqual(
            matrix,
            [(entry["input"], entry["hash"], entry["output"]) for entry in inputs],
            "inputs.json has drifted from the stateless-tests.yml matrix",
        )


if __name__ == "__main__":
    unittest.main()
