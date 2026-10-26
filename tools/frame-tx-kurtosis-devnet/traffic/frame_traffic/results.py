"""Structured output.

Two streams, deliberately separate:

  * `RESULT key=value` lines, the convention the in-process harnesses already use, carrying
    aggregates and the per-transaction privacy-inclusion verdicts. These are what a later
    analysis pass reads.
  * a JSONL event log of every submission, so a surprising aggregate can be traced back to
    the individual transactions without re-running the scenario.

Native client telemetry is not duplicated here. Mempool size, block timing, CPU and the
frame-transaction rejection counters come from Prometheus scraping the clients; this file
covers only what the clients cannot see, which is submission-side timing and the fate of a
transaction the generator knows it sent."""
from __future__ import annotations

import json
import os
import threading
import time


def format_value(value) -> str:
    if isinstance(value, bool):
        return "yes" if value else "no"
    if isinstance(value, float):
        return "{0:.1f}".format(value)
    text = str(value)
    if text == "" or any(c.isspace() for c in text) or '"' in text:
        return '"{0}"'.format(text.replace('"', "'"))
    return text


def percentile(sorted_values: list[float], quantile: float) -> float:
    if not sorted_values:
        return 0.0
    if len(sorted_values) == 1:
        return sorted_values[0]
    position = quantile * (len(sorted_values) - 1)
    lower = int(position)
    upper = min(lower + 1, len(sorted_values) - 1)
    weight = position - lower
    return sorted_values[lower] * (1 - weight) + sorted_values[upper] * weight


class Recorder:
    def __init__(self, results_dir: str, scenario_id: str):
        self.scenario_id = scenario_id
        self.results_dir = results_dir
        os.makedirs(results_dir, exist_ok=True)
        self._result_path = os.path.join(results_dir, "{0}.result".format(scenario_id))
        self._events_path = os.path.join(results_dir, "{0}.events.jsonl".format(scenario_id))
        self._lock = threading.Lock()
        self._result_file = open(self._result_path, "a", buffering=1)
        self._events_file = open(self._events_path, "a", buffering=1)

    def emit(self, case: str, **fields) -> None:
        parts = ["RESULT", "case={0}".format(case), "scenario={0}".format(self.scenario_id)]
        for key in fields:
            parts.append("{0}={1}".format(key, format_value(fields[key])))
        line = " ".join(parts)
        with self._lock:
            print(line, flush=True)
            self._result_file.write(line + "\n")

    def event(self, **fields) -> None:
        fields.setdefault("t", time.time())
        fields.setdefault("scenario", self.scenario_id)
        with self._lock:
            self._events_file.write(json.dumps(fields, sort_keys=True) + "\n")

    def write_metadata(self, metadata: dict) -> str:
        path = os.path.join(self.results_dir, "{0}.run.json".format(self.scenario_id))
        with open(path, "w") as handle:
            json.dump(metadata, handle, indent=2, sort_keys=True)
            handle.write("\n")
        return path

    def close(self) -> None:
        with self._lock:
            self._result_file.close()
            self._events_file.close()


class LatencySeries:
    """Submission latencies for one (role, node) pair."""

    def __init__(self):
        self._values: list[float] = []
        self._lock = threading.Lock()
        self.accepted = 0
        self.rejected = 0
        self.errored = 0
        self.reject_reasons: dict[str, int] = {}

    def add(self, micros: float, outcome: str, reason: str = "") -> None:
        with self._lock:
            self._values.append(micros)
            if outcome == "accepted":
                self.accepted += 1
            elif outcome == "rejected":
                self.rejected += 1
                key = reason[:120]
                self.reject_reasons[key] = self.reject_reasons.get(key, 0) + 1
            else:
                self.errored += 1

    def summary(self) -> dict:
        with self._lock:
            values = sorted(self._values)
            reasons = dict(self.reject_reasons)
            accepted, rejected, errored = self.accepted, self.rejected, self.errored
        top_reason = max(reasons.items(), key=lambda kv: kv[1])[0] if reasons else ""
        return {
            "samples": len(values),
            "accepted": accepted,
            "rejected": rejected,
            "errored": errored,
            "submit_p50_us": percentile(values, 0.50),
            "submit_p95_us": percentile(values, 0.95),
            "submit_p99_us": percentile(values, 0.99),
            "submit_max_us": values[-1] if values else 0.0,
            "top_reject_reason": top_reason,
        }
