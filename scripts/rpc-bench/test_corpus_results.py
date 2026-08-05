#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import corpus_parity  # noqa: E402
import corpus_results  # noqa: E402

SENTINEL = "SENTINEL_PRIVATE_DATA"


def raw_summary(**overrides):
    metrics = {
        "http_req_duration": {"values": {"avg": 1.0, "med": 1.0, "p(90)": 2.0, "p(95)": 3.0, "p(99)": 4.0, "max": 5.0}},
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

    def test_sanitize_accepts_k6_rate_layout_variants(self):
        for metric in ({"rate": 0.25}, {"value": 0.25}, {"values": {"rate": 0.25}}, {"values": {"value": 0.25}}):
            with self.subTest(metric=metric):
                data = corpus_results.sanitize_data(raw_summary(http_req_failed=metric))
                self.assertEqual(data["metrics"]["http_req_failed"]["values"]["rate"], 0.25)

    def test_sanitize_defaults_missing_dropped_iterations_to_zero(self):
        raw = raw_summary()
        del raw["metrics"]["dropped_iterations"]
        data = corpus_results.sanitize_data(raw)
        self.assertEqual(data["metrics"]["dropped_iterations"]["values"]["count"], 0)

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
        import subprocess
        result = subprocess.run(
            [sys.executable, str(Path(__file__).with_name("corpus_results.py")),
             "sanitize", str(self.dir / f"{SENTINEL}.json"), str(self.dir / "out.json")],
            check=False, text=True, capture_output=True,
        )
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
        self.assertEqual(staged, [
            "corpus/a/nm/10/jsonbench-summary.md",
            "corpus/a/nm/10/summary.json",
            "corpus/a/reth/parity.json",
        ])
        blob = "\n".join(p.read_text(encoding="utf-8") for p in stage_root.rglob("*") if p.is_file())
        self.assertNotIn(SENTINEL, blob)

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


if __name__ == "__main__":
    unittest.main()
