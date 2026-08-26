#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import contextlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_parity  # noqa: E402
import corpus_results  # noqa: E402

SENTINEL = "SENTINEL_PRIVATE_DATA"
DURATION = {"avg": 1.0, "med": 1.0, "p(90)": 2.0, "p(95)": 3.0, "p(99)": 4.0, "max": 5.0}


def raw_summary(**overrides):
    metrics = {
        "http_req_duration": {"values": dict(DURATION)},
        "http_reqs": {"values": {"count": 90, "rate": 3.0}},
        "http_req_failed": {"values": {"rate": 0.0}},
        "checks": {"values": {"passes": 180, "fails": 0}},
        "dropped_iterations": {"values": {"count": 0}},
        f"http_req_duration{{url:{SENTINEL}}}": {"values": {"avg": 9.0}},
    }
    metrics.update(overrides)
    return {"metrics": metrics, "root_group": {"name": SENTINEL}}


def valid_parity_report():
    report = {field: 0 for field in corpus_parity.PARITY_COUNTER_FIELDS}
    report.update(total=90, matched=89, content_mismatches=1,
                  divergences=[{"index": 7, "kind": "content_mismatch"}],
                  baseline_client="nethermind", candidate_client="reth")
    return report


class CorpusResultsTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.dir = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def write_json(self, path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data), encoding="utf-8")
        return path

    def test_sanitize_keeps_only_the_fixed_schema(self):
        raw = self.write_json(self.dir / "raw.json", raw_summary())
        out = self.dir / "safe.json"
        corpus_results.sanitize(str(raw), str(out))
        data = json.loads(out.read_text(encoding="utf-8"))
        self.assertEqual(set(data), {"metrics"})
        self.assertEqual(set(data["metrics"]), set(corpus_results.METRIC_FIELDS))
        self.assertNotIn(SENTINEL, out.read_text(encoding="utf-8"))

    def test_sanitize_keeps_selector_class_submetrics_only(self):
        raw = raw_summary(**{
            "http_req_duration{req_name:class_1,scenario:nm}": {"values": dict(DURATION)},
            "http_reqs{req_name:class_1,scenario:nm}": {"values": {"count": 30, "rate": 1.0}},
            "http_req_duration{req_name:'class_2'}": {"values": dict(DURATION)},
            f"http_req_duration{{req_name:{SENTINEL}}}": {"values": dict(DURATION)},
        })
        data = corpus_results.sanitize_data(raw)
        self.assertEqual(list(data["metrics"]["classes"]), ["class_1", "class_2"])
        self.assertEqual(data["metrics"]["classes"]["class_1"]["values"]["count"], 30)
        self.assertEqual(data["metrics"]["classes"]["class_2"]["values"]["count"], 0)
        self.assertNotIn(SENTINEL, json.dumps(data))
        corpus_results._validate_summary(self.write_json(self.dir / "classes" / "summary.json", data))
        data["metrics"]["classes"][SENTINEL] = data["metrics"]["classes"].pop("class_2")
        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results._validate_summary(self.write_json(self.dir / "bad" / "summary.json", data))

    def test_sanitize_accepts_k6_rate_layout_variants(self):
        for metric in ({"rate": 0.25}, {"value": 0.25}, {"values": {"rate": 0.25}}, {"values": {"value": 0.25}}):
            with self.subTest(metric=metric):
                data = corpus_results.sanitize_data(raw_summary(http_req_failed=metric))
                self.assertEqual(data["metrics"]["http_req_failed"]["values"]["rate"], 0.25)

    def test_sanitize_defaults_missing_optional_metrics_to_zero(self):
        raw = raw_summary()
        for metric in ("dropped_iterations", "checks", "http_req_failed"):
            del raw["metrics"][metric]
        data = corpus_results.sanitize_data(raw)
        self.assertEqual(data["metrics"]["dropped_iterations"]["values"], {"count": 0})
        self.assertEqual(data["metrics"]["checks"]["values"], {"passes": 0, "fails": 0})
        self.assertEqual(data["metrics"]["http_req_failed"]["values"], {"rate": 0})

    def test_sanitize_rejects_broken_summaries(self):
        for name, raw in (
            ("no metrics", {"foo": 1}),
            ("missing metric", {"metrics": {"http_reqs": {"values": {"count": 1, "rate": 1}}}}),
            ("non-finite", raw_summary(http_reqs={"values": {"count": float("inf"), "rate": 1}})),
            ("negative", raw_summary(http_reqs={"values": {"count": -1, "rate": 1}})),
            ("boolean", raw_summary(http_reqs={"values": {"count": True, "rate": 1}})),
            ("zero requests", raw_summary(http_reqs={"values": {"count": 0, "rate": 0}})),
        ):
            with self.subTest(name=name):
                with self.assertRaises(corpus_results.CorpusResultsError):
                    corpus_results.sanitize_data(raw)

    def test_sanitize_cli_reports_content_free_error_for_missing_raw(self):
        result = subprocess.run(
            [sys.executable, str(Path(__file__).with_name("corpus_results.py")),
             "sanitize", str(self.dir / f"{SENTINEL}.json"), str(self.dir / "out.json")],
            check=False, text=True, capture_output=True)
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(SENTINEL, result.stderr)

    def test_stage_copies_only_validated_allowlisted_files(self):
        out_root = self.dir / "out"
        sanitized = corpus_results.sanitize_data(raw_summary())
        self.write_json(out_root / "corpus" / "a" / "nm" / "10" / "summary.json", sanitized)
        self.write_json(out_root / "corpus" / "a" / "reth" / "parity.json", valid_parity_report())
        (out_root / "corpus" / "a" / "nm" / "10" / "jsonbench-summary.md").write_text("## ok\n", encoding="utf-8")
        (out_root / "raw-responses.jsonl").write_text(SENTINEL, encoding="utf-8")
        (out_root / "jsonbench.log").write_text(SENTINEL, encoding="utf-8")
        self.write_json(out_root / "results.json", {"request": SENTINEL})
        stage_root = self.dir / "stage"
        corpus_results.stage(str(out_root), str(stage_root))
        staged = sorted(p.relative_to(stage_root).as_posix() for p in stage_root.rglob("*") if p.is_file())
        self.assertEqual(staged, ["corpus/a/nm/10/jsonbench-summary.md", "corpus/a/nm/10/summary.json", "corpus/a/reth/parity.json"])
        blob = "\n".join(p.read_text(encoding="utf-8") for p in stage_root.rglob("*") if p.is_file())
        self.assertNotIn(SENTINEL, blob)

    def test_stage_excludes_warmup_cells(self):
        out_root = self.dir / "out"
        sanitized = corpus_results.sanitize_data(raw_summary())
        self.write_json(out_root / "corpus" / "a" / "nm" / "100" / "summary.json", sanitized)
        self.write_json(out_root / "corpus" / "a" / "nm" / "warmup" / "summary.json", sanitized)
        self.write_json(out_root / "warmup-cell" / "a" / "nm" / "summary.json", sanitized)
        self.write_json(out_root / "corpus" / "warmup-heavy" / "nm" / "100" / "summary.json", sanitized)
        stage_root = self.dir / "stage-warm"
        corpus_results.stage(str(out_root), str(stage_root))
        staged = sorted(p.relative_to(stage_root).as_posix() for p in stage_root.rglob("*") if p.is_file())
        self.assertEqual(staged, ["corpus/a/nm/100/summary.json", "corpus/warmup-heavy/nm/100/summary.json"])

    def test_timings_meta_schema_requires_warmup_fields(self):
        meta = {"head": 100, "chain_id": 1, "block_hash": "0x" + "ab" * 32, "records": 3, "passes": 2, "requests": 6,
                "target_rps": 50.0, "achieved_rps": 49.9, "concurrency": 4, "warmup_seconds": 60, "warmup_rps": 383.5,
                "outcomes": {"ok": 6}}
        corpus_results._validate_timings_meta(self.write_json(self.dir / "timings.meta.json", meta))
        for missing in ("warmup_seconds", "warmup_rps"):
            legacy = {k: v for k, v in meta.items() if k != missing}
            with self.assertRaises(corpus_results.CorpusResultsError):
                corpus_results._validate_timings_meta(self.write_json(self.dir / missing / "timings.meta.json", legacy))
        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results._validate_timings_meta(self.write_json(self.dir / "negative" / "timings.meta.json", dict(meta, warmup_rps=-1.0)))

    def test_manifest_is_validated_and_relativized(self):
        out_root = self.dir / "out"
        sanitized = corpus_results.sanitize_data(raw_summary())
        self.write_json(out_root / "corpus" / "a" / "nm" / "100" / "summary.json", sanitized)
        cell = out_root / "corpus" / "a" / "nm" / "100"
        (out_root / "summaries.manifest").write_text(f"iso|a|nm|100={cell / 'jsonbench-summary.md'}\n", encoding="utf-8")
        stage_root = self.dir / "stage-manifest"
        corpus_results.stage(str(out_root), str(stage_root))
        staged = (stage_root / "summaries.manifest").read_text(encoding="utf-8")
        self.assertEqual(staged, "iso|a|nm|100=corpus/a/nm/100/jsonbench-summary.md\n")
        self.assertNotIn(str(out_root), staged)
        for tag, bad in (("escape", "iso|a|nm|100=/etc/passwd"), ("shapeless", "not a manifest line"),
                         ("arity", "iso|a|b|c|d|e=x/jsonbench-summary.md")):
            (out_root / "summaries.manifest").write_text(bad + "\n", encoding="utf-8")
            stage2 = self.dir / f"stage-manifest-{tag}"
            corpus_results.stage(str(out_root), str(stage2))
            staged2 = sorted(q.relative_to(stage2).as_posix() for q in stage2.rglob("*") if q.is_file())
            self.assertNotIn("summaries.manifest", staged2, bad)
            self.assertIn("corpus/a/nm/100/summary.json", staged2, bad)

    def test_stage_rejects_unsanitized_summary_and_bad_parity(self):
        for name, filename, payload in (
            ("raw k6 summary", "summary.json", raw_summary()),
            ("parity extra key", "parity.json", {**valid_parity_report(), "extra": 1}),
            ("parity negative", "parity.json", {**valid_parity_report(), "matched": -1}),
            ("parity long label", "parity.json", {**valid_parity_report(), "baseline_client": "x" * 200}),
            ("parity bad divergence", "parity.json", {**valid_parity_report(), "divergences": [{"index": 0, "kind": "x"}]}),
            ("parity divergence content", "parity.json", {**valid_parity_report(), "divergences": [{"index": 1, "kind": "x", "data": "leak"}]}),
        ):
            with self.subTest(name=name):
                out_root = self.dir / f"out-{filename}-{name.replace(' ', '')}"
                self.write_json(out_root / filename, payload)
                with self.assertRaises(corpus_results.CorpusResultsError):
                    corpus_results.stage(str(out_root), str(self.dir / "stage2"))

    def test_stage_fails_on_empty_tree(self):
        out_root = self.dir / "empty"
        out_root.mkdir()
        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results.stage(str(out_root), str(self.dir / "stage3"))


class CommentRenderingTests(unittest.TestCase):
    """The PR comment is public: built from staged data only, content-free."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def _cell(self, label, avg, p99, fail=0.0, slot="100", cpu=None, classes=None, requests=12000, dropped=0):
        cell = self.root / "corpus" / "corpus-a" / label / slot
        cell.mkdir(parents=True)
        metrics = {
            "http_req_duration": {"values": {"avg": avg, "med": avg, "p(90)": avg * 2, "p(95)": avg * 2.2, "p(99)": p99, "max": p99 * 3}},
            "http_reqs": {"values": {"count": requests}},
            "http_req_failed": {"values": {"rate": fail}},
            "dropped_iterations": {"values": {"count": dropped}}}
        if classes:
            metrics["classes"] = {name: {"values": {"avg": v, "med": v, "p(90)": v, "p(95)": v, "p(99)": v * 2, "max": v * 3, "count": 100}}
                                  for name, v in classes.items()}
        (cell / "summary.json").write_text(json.dumps({"metrics": metrics}), encoding="utf-8")
        if cpu is not None:
            (cell / "resources.json").write_text(json.dumps({"cpu_ms_per_request": cpu, "requests": 12000}), encoding="utf-8")

    def _timings(self, label, latencies, achieved):
        d = self.root / "corpus" / "corpus-a" / label
        d.mkdir(parents=True, exist_ok=True)
        rows = ["record_index,pass_1_ms,pass_1_status,pass_2_ms,pass_2_status"]
        rows += [f"{i},{ms:.3f},ok,{ms * 1.02:.3f},ok" for i, ms in enumerate(latencies, start=1)]
        (d / "timings.csv").write_text("\n".join(rows) + "\n", encoding="utf-8")
        (d / "timings.meta.json").write_text(json.dumps({
            "head": 1, "chain_id": 1, "block_hash": "0x" + "ab" * 32, "records": len(latencies), "passes": 2,
            "requests": 2 * len(latencies), "target_rps": 0, "achieved_rps": achieved, "concurrency": 16,
            "warmup_seconds": 60, "warmup_rps": 400, "outcomes": {"ok": 2 * len(latencies)}}), encoding="utf-8")

    def test_reports_regression_and_improvement_directions(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 22.0, 90.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("+10.0%", body)
        self.assertIn("-10.0%", body)
        self.assertIn("master", body)

    def test_flags_a_parity_divergence(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 20.0, 100.0)
        report = self.root / "corpus" / "corpus-a" / "nethermind" / "parity.json"
        report.write_text(json.dumps({"total": 497, "matched": 490, "both_rpc_errors": 0, "content_mismatches": 7}), encoding="utf-8")
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("DIVERGES", body)
        self.assertIn("content_mismatches=7", body)

    def test_clean_parity_is_stated_plainly(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 20.0, 100.0)
        report = self.root / "corpus" / "corpus-a" / "nethermind" / "parity.json"
        report.write_text(json.dumps({"total": 497, "matched": 497, "both_rpc_errors": 0}), encoding="utf-8")
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("497/497 identical to master", body)
        self.assertNotIn("DIVERGES", body)

    def test_repeats_pool_into_one_arm_with_aa_spread(self):
        """ABBA repeats (label _r2) and repeated rate slots (100_r2) pool per arm; the master pair is the A/A control."""
        self._cell("nethermind_master", 20.0, 100.0, cpu=19.0)
        self._cell("nethermind", 19.0, 90.0, cpu=18.0)
        self._cell("nethermind_r2", 19.0, 90.0, cpu=18.0)
        self._cell("nethermind_master_r2", 22.0, 110.0, cpu=21.0)
        self._cell("nethermind_master_r2", 21.0, 105.0, slot="100_r2", cpu=20.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("n=3/2 runs", body)
        self.assertNotIn("100_r2", body)
        self.assertIn("| avg | 21.00 ms | 19.00 ms |", body)
        self.assertIn("| CPU-ms/request | 20.00 | 18.00 |", body)
        self.assertRegex(body, r"\| avg \|.*\| 9\.5% \|")

    def test_per_class_table_and_paired_timings(self):
        self._cell("nethermind_master", 20.0, 100.0, classes={"class_1": 10.0, "class_2": 50.0})
        self._cell("nethermind", 20.0, 100.0, classes={"class_1": 10.0, "class_2": 40.0})
        self._timings("nethermind_master", [10.0] * 10 + [100.0] * 10, achieved=800.0)
        self._timings("nethermind", [10.0] * 10 + [80.0] * 10, achieved=880.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("| class_2 | 100 | 50.00 | 40.00 |", body)
        self.assertIn("-20.0%", body)
        self.assertIn("Paired per-record replay (20 records", body)
        self.assertIn("10 faster", body)
        self.assertIn("Closed-loop throughput (concurrency 16): master 800.0 req/s, PR 880.0 req/s", body)

    def test_record_shift_needs_to_exceed_the_records_own_aa_spread(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 20.0, 100.0)
        self._timings("nethermind_master", [10.0] * 10 + [100.0] * 10, achieved=800.0)
        self._timings("nethermind_master_r2", [10.0] * 10 + [90.0] * 10, achieved=800.0)
        self._timings("nethermind", [10.0] * 10 + [80.0] * 10, achieved=880.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("0 records slower and 0 faster", body)
        self.assertRegex(body, r"median delta .* -\d+\.\d%")

    def test_flags_unequal_sample_sizes_and_dropped_iterations(self):
        self._cell("nethermind_master", 20.0, 100.0, requests=20000)
        self._cell("nethermind", 20.0, 100.0, requests=12000, dropped=8000)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("20000/12000 requests/run", body)
        self.assertIn("Unequal load — master 20000 vs PR 12000 requests/run, 8000 iteration(s) dropped", body)

    def test_equal_sample_sizes_are_not_flagged(self):
        self._cell("nethermind_master", 20.0, 100.0, requests=20000)
        self._cell("nethermind", 20.0, 100.0, requests=19900)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("20000/19900 requests/run", body)
        self.assertNotIn("Unequal load", body)

    def test_missing_client_does_not_crash(self):
        self._cell("nethermind_master", 20.0, 100.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("missing a client", body)

    def test_comment_cli_matches_the_workflow_invocation(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 19.0, 90.0)
        argv = ["comment", str(self.root), "--baseline", "nethermind_master", "--candidate", "nethermind"]
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(corpus_results.main(argv), 0)
        body = out.getvalue()
        self.assertIn("corpus-a", body)
        self.assertIn("| metric | master | PR | delta |", body)

    def test_stage_cli_still_dispatches_to_stage(self):
        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(corpus_results.main(["stage", str(self.root / "absent"), str(self.root / "staged-cli")]), 1)
        self.assertIn("output root does not exist", err.getvalue())


if __name__ == "__main__":
    unittest.main()
