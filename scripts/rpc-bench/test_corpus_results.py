#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import copy
import contextlib
import io
import json
import signal
import subprocess
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


def perf_records():
    records = []
    for index, name in enumerate(corpus_results.PERF_COUNTER_NAMES, start=1):
        records.append({
            "counter-value": f"{index * 10}.500000",
            "unit": "msec" if name == "task-clock" else "",
            "event": name,
            "event-runtime": 2_500_000,
            "pcnt-running": 50.0,
            "metric-value": "1.000000",
            "metric-unit": "ignored",
        })
    return records


def perf_json(records):
    # perf stat emits one JSON object per line, rather than a single JSON array.
    return "\n".join(json.dumps(record) for record in records) + "\n"


def perf_preflight_records():
    return [record for record in perf_records()
            if record["event"] in corpus_results.PERF_REQUIRED_COUNTERS]


def malformed_perf_json(records):
    raw = perf_json(records).replace('"metric-unit": "ignored"',
                                     f'"metric-unit": "{SENTINEL}"', 1)
    marker = '"counter-value": "10.500000"'
    return (
        ("duplicate key", raw.replace(marker,
                                       f'{marker}, "counter-value": "{SENTINEL}"', 1)),
        ("NaN", raw.replace(marker, '"counter-value": NaN', 1)),
        ("Infinity", raw.replace(marker, '"counter-value": Infinity', 1)),
        ("overflow", raw.replace(marker, '"counter-value": 1e1000000', 1)),
    )


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

    def test_sanitize_defaults_missing_optional_metrics_to_zero(self):
        # k6 omits checks (no check() calls in the workload), http_req_failed, and
        # dropped_iterations when nothing triggered them — absence means zero.
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
            check=False, text=True, capture_output=True,
        )
        self.assertEqual(result.returncode, 1)
        self.assertNotIn(SENTINEL, result.stderr)

    def test_normalize_perf_stat_keeps_the_fixed_counter_schema(self):
        data = corpus_results.normalize_perf_data(perf_json(perf_records()), 10)

        self.assertEqual(set(data), {"schema_version", "requests", "counters"})
        self.assertEqual(data["schema_version"], 1)
        self.assertEqual(data["requests"], 10)
        self.assertEqual(tuple(data["counters"]), corpus_results.PERF_COUNTER_NAMES)
        task_clock = data["counters"]["task-clock"]
        self.assertEqual(task_clock["status"], "collected")
        self.assertEqual(task_clock["unit"], "milliseconds")
        self.assertEqual(task_clock["raw_count"], 10.5)
        self.assertEqual(task_clock["per_request"], 1.05)
        self.assertEqual(task_clock["time_running_ns"], 2_500_000.0)
        self.assertEqual(task_clock["time_enabled_ns"], 5_000_000.0)
        self.assertEqual(task_clock["scale"], 2.0)
        self.assertNotIn("ignored", json.dumps(data))

    def test_normalize_perf_stat_marks_optional_unavailable_events(self):
        records = perf_records()
        next(record for record in records if record["event"] == "LLC-loads")["counter-value"] = "<not supported>"

        data = corpus_results.normalize_perf_data(perf_json(records), 10)

        self.assertEqual(data["counters"]["LLC-loads"], {
            "status": "unsupported", "unit": "count", "raw_count": None, "per_request": None,
            "time_enabled_ns": None, "time_running_ns": None, "scale": None,
        })

    def test_perf_parser_rejects_missing_required_malformed_and_injected_records(self):
        cases = []
        missing = perf_records()
        missing.pop()
        cases.append(("missing", missing))
        required_unavailable = perf_records()
        next(record for record in required_unavailable if record["event"] == "cycles")["counter-value"] = "<not counted>"
        cases.append(("required unavailable", required_unavailable))
        duplicate = perf_records()
        duplicate.append(copy.deepcopy(duplicate[0]))
        cases.append(("duplicate", duplicate))
        unknown = perf_records()
        unknown[-1]["event"] = SENTINEL
        cases.append(("unknown event", unknown))
        injected = perf_records()
        injected[0]["injected"] = SENTINEL
        cases.append(("injected field", injected))
        unsafe = perf_records()
        unsafe[0]["counter-value"] = "NaN"
        cases.append(("unsafe count", unsafe))
        wrong_unit = perf_records()
        wrong_unit[0]["unit"] = SENTINEL
        cases.append(("wrong unit", wrong_unit))

        for name, records in cases:
            with self.subTest(name=name):
                with self.assertRaises(corpus_results.CorpusResultsError):
                    corpus_results.normalize_perf_data(perf_json(records), 10)

    def test_perf_preflight_uses_the_required_counter_parser(self):
        raw = self.dir / "perf-preflight.json"
        raw.write_text(perf_json(perf_preflight_records()), encoding="utf-8")

        corpus_results.validate_perf_preflight(str(raw))

    def test_literal_invalid_perf_json_is_rejected_without_echo_by_normalize_and_preflight(self):
        commands = (
            ("normalize", perf_records(),
             lambda raw, out: ("perf-normalize", str(raw), str(out), "10"),
             lambda raw: corpus_results.normalize_perf_data(raw.read_text(encoding="utf-8"), 10)),
            ("preflight", perf_preflight_records(),
             lambda raw, _: ("perf-preflight", str(raw)),
             lambda raw: corpus_results.validate_perf_preflight(str(raw))),
        )
        for command_name, records, arguments, validate in commands:
            for case_name, content in malformed_perf_json(records):
                with self.subTest(command=command_name, case=case_name):
                    raw = self.dir / f"raw-perf-{command_name}-{case_name}.json"
                    raw.write_text(content, encoding="utf-8")
                    with self.assertRaises(corpus_results.CorpusResultsError) as error:
                        validate(raw)
                    self.assertNotIn(SENTINEL, str(error.exception))
                    result = subprocess.run(
                        [sys.executable, str(Path(__file__).with_name("corpus_results.py")),
                         *arguments(raw, self.dir / "out-perf.json")],
                        check=False, text=True, capture_output=True,
                    )
                    self.assertEqual(result.returncode, 1)
                    self.assertNotIn(SENTINEL, result.stderr)
                    self.assertNotIn(SENTINEL, result.stdout)

    @unittest.skipUnless(corpus_results._pidfd_supported() and Path("/proc").is_dir(),
                         "pidfd signaling is unavailable")
    def test_perf_pidfd_signal_requires_matching_process_start_time(self):
        child = subprocess.Popen(["/bin/sleep", "60"], stdout=subprocess.DEVNULL,
                                 stderr=subprocess.DEVNULL)
        try:
            _, start_time = corpus_results._perf_process_identity(child.pid)
            mismatched_start_time = str(int(start_time) + 1)

            self.assertEqual(corpus_results.signal_perf_process(
                child.pid, mismatched_start_time, "TERM"), "gone")
            self.assertIsNone(child.poll())
            self.assertEqual(corpus_results.signal_perf_process(child.pid, start_time, "TERM"), "sent")
            self.assertEqual(child.wait(timeout=5), -signal.SIGTERM)
        finally:
            if child.poll() is None:
                try:
                    child.kill()
                except OSError:
                    pass
                child.wait(timeout=5)

    def test_perf_normalize_cli_reports_content_free_error(self):
        records = perf_records()
        records[0]["event"] = SENTINEL
        raw = self.dir / "raw-perf.json"
        raw.write_text(perf_json(records), encoding="utf-8")
        result = subprocess.run(
            [sys.executable, str(Path(__file__).with_name("corpus_results.py")),
             "perf-normalize", str(raw), str(self.dir / "out-perf.json"), "10"],
            check=False, text=True, capture_output=True,
        )

        self.assertEqual(result.returncode, 1)
        self.assertNotIn(SENTINEL, result.stderr)
        self.assertNotIn(SENTINEL, result.stdout)

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

    def test_stage_allows_valid_perf_stat_and_rejects_its_injected_fields(self):
        out_root = self.dir / "out-perf"
        self.write_json(out_root / "summary.json", corpus_results.sanitize_data(raw_summary()))
        valid = corpus_results.normalize_perf_data(perf_json(perf_records()), 90)
        valid["counters"]["LLC-loads"].update(status="unsupported", raw_count=None,
                                                 per_request=None, time_enabled_ns=None,
                                                 time_running_ns=None, scale=None)
        self.write_json(out_root / "perf-stat.json", valid)

        stage_root = self.dir / "stage-perf"
        corpus_results.stage(str(out_root), str(stage_root))
        staged = json.loads((stage_root / "perf-stat.json").read_text(encoding="utf-8"))
        self.assertEqual(staged["counters"]["LLC-loads"], {
            "status": "unsupported", "unit": "count", "raw_count": None, "per_request": None,
            "time_enabled_ns": None, "time_running_ns": None, "scale": None,
        })

        invalid = copy.deepcopy(valid)
        invalid["counters"]["cycles"]["unexpected"] = SENTINEL
        bad_root = self.dir / "out-bad-perf"
        self.write_json(bad_root / "summary.json", corpus_results.sanitize_data(raw_summary()))
        self.write_json(bad_root / "perf-stat.json", invalid)
        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results.stage(str(bad_root), str(self.dir / "stage-bad-perf"))

    def test_stage_rejects_duplicate_perf_stat_json_keys(self):
        out_root = self.dir / "out-duplicate-perf"
        self.write_json(out_root / "summary.json", corpus_results.sanitize_data(raw_summary()))
        counters = json.dumps(corpus_results.normalize_perf_data(perf_json(perf_records()), 90)["counters"])
        (out_root / "perf-stat.json").write_text(
            f'{{"schema_version":1,"requests":90,"requests":90,"counters":{counters}}}',
            encoding="utf-8")

        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results.stage(str(out_root), str(self.dir / "stage-duplicate-perf"))

    def test_stage_rejects_wrong_perf_stat_status_unit_and_required_data(self):
        valid = corpus_results.normalize_perf_data(perf_json(perf_records()), 90)

        def unsupported_required(data):
            counter = data["counters"]["cycles"]
            counter.update(status="unsupported", raw_count=None, per_request=None,
                           time_enabled_ns=None, time_running_ns=None, scale=None)

        def unsupported_optional_with_metadata(data):
            counter = data["counters"]["LLC-loads"]
            counter.update(status="unsupported", raw_count=None, per_request=None,
                           time_enabled_ns=None, time_running_ns=None, scale=1)

        cases = [
            ("wrong status", lambda data: data["counters"]["cycles"].update(status=SENTINEL)),
            ("wrong unit", lambda data: data["counters"]["cycles"].update(unit=SENTINEL)),
            ("required unavailable", unsupported_required),
            ("optional unavailable metadata", unsupported_optional_with_metadata),
            ("unsafe raw count", lambda data: data["counters"]["cycles"].update(raw_count=float("inf"))),
            ("missing counter", lambda data: data["counters"].pop("cycles")),
            ("boolean schema", lambda data: data.update(schema_version=True)),
        ]
        for name, mutate in cases:
            with self.subTest(name=name):
                out_root = self.dir / f"out-invalid-perf-{name.replace(' ', '-') }"
                self.write_json(out_root / "summary.json", corpus_results.sanitize_data(raw_summary()))
                payload = copy.deepcopy(valid)
                mutate(payload)
                self.write_json(out_root / "perf-stat.json", payload)
                with self.assertRaises(corpus_results.CorpusResultsError):
                    corpus_results.stage(str(out_root), str(self.dir / f"stage-invalid-perf-{name.replace(' ', '-') }"))

    def test_stage_excludes_warmup_cells(self):
        """A staged warmup/summary.json would displace the measured cell in the PR comment:
        comment() keys cells by directory position, and 'warmup' sorts after '100'."""
        out_root = self.dir / "out"
        sanitized = corpus_results.sanitize_data(raw_summary())
        self.write_json(out_root / "corpus" / "a" / "nm" / "100" / "summary.json", sanitized)
        self.write_json(out_root / "corpus" / "a" / "nm" / "warmup" / "summary.json", sanitized)
        # the sweep's actual scratch layout uses a 'warmup-cell' segment - the guard must match it
        self.write_json(out_root / "warmup-cell" / "a" / "nm" / "summary.json", sanitized)
        # ...but a corpus LABEL containing 'warmup' is a legitimate scenario and must survive
        self.write_json(out_root / "corpus" / "warmup-heavy" / "nm" / "100" / "summary.json", sanitized)

        stage_root = self.dir / "stage-warm"
        corpus_results.stage(str(out_root), str(stage_root))

        staged = sorted(p.relative_to(stage_root).as_posix() for p in stage_root.rglob("*") if p.is_file())
        self.assertEqual(staged, ["corpus/a/nm/100/summary.json",
                                  "corpus/warmup-heavy/nm/100/summary.json"])

    def test_timings_meta_schema_requires_warmup_seconds(self):
        """The matrix must say whether it was measured warm — cold p99 runs ~60% high and a cold
        matrix is otherwise indistinguishable from a warm one."""
        meta = {"head": 100, "chain_id": 1, "block_hash": "0x" + "ab" * 32, "records": 3,
                "passes": 2, "requests": 6, "target_rps": 50.0, "achieved_rps": 49.9,
                "concurrency": 4, "warmup_seconds": 240, "outcomes": {"ok": 6}}
        path = self.write_json(self.dir / "timings.meta.json", meta)
        corpus_results._validate_timings_meta(path)  # complete schema passes

        legacy = {k: v for k, v in meta.items() if k != "warmup_seconds"}
        path2 = self.write_json(self.dir / "legacy" / "timings.meta.json", legacy)
        with self.assertRaises(corpus_results.CorpusResultsError):
            corpus_results._validate_timings_meta(path2)

    def test_manifest_is_validated_and_relativized(self):
        """The staged manifest must not leak runner-absolute paths, and garbage must not stage."""
        out_root = self.dir / "out"
        sanitized = corpus_results.sanitize_data(raw_summary())
        self.write_json(out_root / "corpus" / "a" / "nm" / "100" / "summary.json", sanitized)
        cell = out_root / "corpus" / "a" / "nm" / "100"
        (out_root / "summaries.manifest").write_text(
            f"iso|a|nm|100={cell / 'jsonbench-summary.md'}\n", encoding="utf-8")

        stage_root = self.dir / "stage-manifest"
        corpus_results.stage(str(out_root), str(stage_root))
        staged = (stage_root / "summaries.manifest").read_text(encoding="utf-8")
        self.assertEqual(staged, "iso|a|nm|100=corpus/a/nm/100/jsonbench-summary.md\n")
        self.assertNotIn(str(out_root), staged)

        (out_root / "summaries.manifest").write_text(
            "iso|a|nm|100=/etc/passwd\n", encoding="utf-8")
        # A malformed INDEX drops only itself: content files still stage, because failing the
        # whole artifact over an index nothing downstream reads would discard a multi-hour sweep.
        for tag, bad in (("escape", "iso|a|nm|100=/etc/passwd"),
                         ("shapeless", "not a manifest line"),
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
    """The PR comment is public, so it must be built from staged data and stay content-free."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def _cell(self, label, avg, p99, fail=0.0, slot="100"):
        cell = self.root / "corpus" / "corpus-a" / label / slot
        cell.mkdir(parents=True)
        (cell / "summary.json").write_text(json.dumps({"metrics": {
            "http_req_duration": {"values": {"avg": avg, "med": avg, "p(90)": avg * 2,
                                             "p(95)": avg * 2.2, "p(99)": p99, "max": p99 * 3}},
            "http_reqs": {"values": {"count": 12000}},
            "http_req_failed": {"values": {"rate": fail}}}}), encoding="utf-8")

    def test_reports_regression_and_improvement_directions(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 22.0, 90.0)          # avg worse, p99 better
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("+10.0%", body)                  # avg regression
        self.assertIn("-10.0%", body)                  # p99 improvement
        self.assertIn("master", body)

    def test_flags_a_parity_divergence(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 20.0, 100.0)
        report = self.root / "corpus" / "corpus-a" / "nethermind" / "parity.json"
        report.write_text(json.dumps({"total": 497, "matched": 490, "both_rpc_errors": 0,
                                      "content_mismatches": 7}), encoding="utf-8")
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("DIVERGES", body)
        self.assertIn("content_mismatches=7", body)

    def test_clean_parity_is_stated_plainly(self):
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 20.0, 100.0)
        report = self.root / "corpus" / "corpus-a" / "nethermind" / "parity.json"
        report.write_text(json.dumps({"total": 497, "matched": 497, "both_rpc_errors": 0}),
                          encoding="utf-8")
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("497/497 identical to master", body)
        self.assertNotIn("DIVERGES", body)

    def test_repeated_rate_slots_render_as_separate_rows(self):
        """A repeated rate is the drift control; keying on (corpus, label) alone kept only the
        slot that sorted last, silently discarding it."""
        self._cell("nethermind_master", 20.0, 100.0, slot="100")
        self._cell("nethermind", 19.0, 90.0, slot="100")
        self._cell("nethermind_master", 21.0, 110.0, slot="100_r2")
        self._cell("nethermind", 20.0, 95.0, slot="100_r2")
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("@ `100` rps", body)
        self.assertIn("@ `100_r2` rps", body)
        self.assertIn("| avg | 20.00 ms | 19.00 ms |", body)
        self.assertIn("| avg | 21.00 ms | 20.00 ms |", body)

    def test_missing_client_does_not_crash(self):
        self._cell("nethermind_master", 20.0, 100.0)
        body = corpus_results.comment(str(self.root), "nethermind_master", "nethermind")
        self.assertIn("missing a client", body)

    def test_comment_cli_matches_the_workflow_invocation(self):
        """Every subcommand must dispatch to itself — `comment` once fell through to `stage`."""
        self._cell("nethermind_master", 20.0, 100.0)
        self._cell("nethermind", 19.0, 90.0)
        argv = ["comment", str(self.root), "--baseline", "nethermind_master",
                "--candidate", "nethermind"]
        with contextlib.redirect_stdout(io.StringIO()) as out:
            self.assertEqual(corpus_results.main(argv), 0)
        body = out.getvalue()
        self.assertIn("corpus-a", body)
        self.assertIn("| metric | master | PR | delta |", body)

    def test_stage_cli_still_dispatches_to_stage(self):
        """Dispatching to the wrong handler raises AttributeError; reaching stage returns 1."""
        with contextlib.redirect_stderr(io.StringIO()) as err:
            self.assertEqual(corpus_results.main(
                ["stage", str(self.root / "absent"), str(self.root / "staged-cli")]), 1)
        self.assertIn("output root does not exist", err.getvalue())


if __name__ == "__main__":
    unittest.main()
