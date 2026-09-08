#!/usr/bin/env python3
"""Build the public five-arm Account CV result and enforce cross-arm identity."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from pathlib import Path
from typing import Any, Sequence

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from expb_account_cv import ARMS, EXPB_REVISION, IMAGE  # noqa: E402


def _read(path: Path) -> dict[str, Any]:
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError(f"result is not an object: {path}")
    return value


def build(results_root: Path, config: Path, output: Path) -> dict[str, Any]:
    arms: list[dict[str, Any]] = []
    fingerprints: list[dict[str, Any]] = []
    ranges: tuple[int, int, int, int] | None = None
    for arm in ARMS:
        arm_dir = results_root / arm.label
        result = _read(arm_dir / "result.json")
        prep = _read(arm_dir / "account-index-prepare.json")
        record = _read(arm_dir / "preparation-record.json")
        if result.get("arm") != arm.label or result.get("payload_count") != 1000:
            raise ValueError(f"invalid analyzed result for {arm.label}")
        current_range = (
            int(result["payload_first"]),
            int(result["payload_last"]),
            int(result["block_first"]),
            int(result["block_last"]),
        )
        if ranges is None:
            ranges = current_range
        elif current_range != ranges:
            raise ValueError(f"payload/block range differs across arms: {arm.label}")
        if prep.get("mode") != arm.mode or float(prep.get("threshold")) != arm.threshold:
            raise ValueError(f"preparation mode/threshold mismatch for {arm.label}")
        if record.get("arm") != arm.label:
            raise ValueError(f"preparation record mismatch for {arm.label}")
        fingerprint = record.get("fingerprint")
        if not isinstance(fingerprint, dict):
            raise ValueError(f"missing preparation fingerprint for {arm.label}")
        fingerprints.append(fingerprint)
        arms.append({
            "label": arm.label,
            "mode": arm.mode,
            "threshold": arm.threshold,
            "search_type": arm.search_type,
            "benchmark": result,
            "preparation": prep,
        })
    first = fingerprints[0]
    for fingerprint in fingerprints[1:]:
        for key in ("account_entry_count", "account_content_sha256_before", "account_content_sha256_after", "helper_sha256"):
            if fingerprint.get(key) != first.get(key):
                raise ValueError(f"cross-arm canonical fingerprint mismatch: {key}")
    canonical = _read(results_root / "canonical-fingerprint.json")
    if any(canonical.get(key) != first.get(key) for key in first):
        raise ValueError("canonical fingerprint file does not match all arm preparations")

    source_sha = hashlib.sha256(config.read_bytes()).hexdigest()
    value = {
        "schema_version": 1,
        "experiment": "expb-fusaka-account-index-cv",
        "image": IMAGE,
        "source_revision": "d66cfd50e0b6f492d8bc5d6b99be22e1821a55e4",
        "expb_revision": EXPB_REVISION,
        "payload_set": "fusaka",
        "payload_amount": 1000,
        "arm_order": [arm.label for arm in ARMS],
        "config_sha256": source_sha,
        "canonical_fingerprint": canonical,
        "arms": arms,
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(value, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return value


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--results-root", required=True)
    parser.add_argument("--config", required=True)
    parser.add_argument("--output", required=True)
    arguments = parser.parse_args(argv)
    try:
        value = build(Path(arguments.results_root), Path(arguments.config), Path(arguments.output))
        print(json.dumps({"arms": value["arm_order"], "output": arguments.output}, sort_keys=True))
        return 0
    except (OSError, UnicodeError, json.JSONDecodeError, ValueError, TypeError) as error:
        print(f"aggregate-account-cv: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
