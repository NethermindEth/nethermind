#!/usr/bin/env python3
"""Fixed, aggregate-only contract for the Account index CV sweep.

The sweep deliberately has one ordering and one set of labels.  Keeping that contract in a
small parser makes the workflow opt-in explicit and prevents arbitrary command-line fragments
from reaching a benchmark container.
"""

from __future__ import annotations

import argparse
import json
import math
import re
import sys
from pathlib import Path
from typing import Any, Sequence


IMAGE = "nethermindeth/nethermind:rocksdb-auto-base-d66cfd50e0"
CORPUS = "eth-call-corpus-20260805T104605Z-497-safe.jsonl.gz"

ARMS: tuple[dict[str, Any], ...] = (
    {"label": "master", "mode": "binary", "threshold": -1.0},
    {"label": "forced-interpolation", "mode": "interpolation", "threshold": -1.0},
    {"label": "auto-cv-0.2", "mode": "auto", "threshold": 0.2},
    {"label": "auto-cv-0.05", "mode": "auto", "threshold": 0.05},
    {"label": "auto-cv-0.1", "mode": "auto", "threshold": 0.1},
    {"label": "auto-cv-0.15", "mode": "auto", "threshold": 0.15},
    {"label": "auto-cv-0.25", "mode": "auto", "threshold": 0.25},
    {"label": "auto-cv-0.35", "mode": "auto", "threshold": 0.35},
    {"label": "auto-cv-0.5", "mode": "auto", "threshold": 0.5},
)

REPORT_KEYS = frozenset(
    {"SchemaVersion", "DbPath", "ScratchRoot", "Mode", "Threshold", "Options", "Content", "Tables", "Ssts", "Timing", "Layout"}
)
OPTIONS_KEYS = frozenset({"AccountIndexSearchType", "UniformCvThreshold", "AutomaticCompactionsDisabled"})
CONTENT_KEYS = frozenset({"Before", "After", "Unchanged"})
CONTENT_DIGEST_KEYS = frozenset({"Count", "Sha256"})
TABLES_KEYS = frozenset({"Before", "After"})
TABLE_KEYS = frozenset({"UniformBlocks", "IndexBytes", "FilterBytes", "EstimateTableReadersMemory", "Entries"})
SSTS_KEYS = frozenset({"Before", "After", "Rewritten"})
SST_KEYS = frozenset({"Number", "SizeBytes"})
TIMING_KEYS = frozenset({"WallMilliseconds", "CpuMilliseconds", "WorkingSetBytesBefore", "WorkingSetBytesAfter", "PeakWorkingSetBytes", "PrivateBytesBefore", "PrivateBytesAfter"})
LAYOUT_KEYS = frozenset({"Current", "Manifest", "Files", "FlatColumnFamilies"})
SHA256 = re.compile(r"^[0-9a-f]{64}$")
PUBLIC_KEYS = frozenset(
    {
        "schema_version", "mode", "threshold", "resolved_index_search_type", "resolved_uniform_cv_threshold",
        "automatic_compactions_disabled", "account_entry_count", "account_sst_count", "uniform_index_count",
        "persisted_index_bytes", "filter_bytes", "table_reader_memory_bytes", "account_content_sha256",
        "preparation_cpu_seconds", "preparation_wall_seconds", "preparation_working_set_before_bytes",
        "preparation_working_set_after_bytes", "preparation_peak_memory_bytes", "preparation_private_bytes_before",
        "preparation_private_bytes_after", "helper_sha256",
    }
)


class ContractError(ValueError):
    """Raised when a public sweep contract or helper result is invalid."""


def arms() -> list[dict[str, Any]]:
    """Return a copy of the fixed ordered arm list."""

    return [dict(arm) for arm in ARMS]


def validate_opt_in(value: Any) -> bool:
    """Validate the structured option and return whether the sweep is enabled."""

    if not isinstance(value, bool):
        raise ContractError("account_index_sweep must be the JSON boolean true or false")
    return value


def _finite_number(value: Any, name: str) -> float:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ContractError(f"{name} must be a finite JSON number")
    return float(value)


def _exact_keys(value: Any, expected: frozenset[str], name: str) -> dict[str, Any]:
    if not isinstance(value, dict) or set(value) != expected:
        raise ContractError(f"helper result {name} schema mismatch")
    return value


def _non_negative_int(value: Any, name: str) -> int:
    if isinstance(value, bool) or not isinstance(value, int) or value < 0:
        raise ContractError(f"helper result {name} must be a non-negative integer")
    return value


def _digest(value: Any, name: str) -> str:
    if not isinstance(value, str) or SHA256.fullmatch(value) is None:
        raise ContractError(f"helper result {name} must be lowercase SHA-256")
    return value


def _table(value: Any, name: str) -> dict[str, Any]:
    table = _exact_keys(value, TABLE_KEYS, name)
    for key in ("UniformBlocks", "IndexBytes", "FilterBytes", "EstimateTableReadersMemory", "Entries"):
        _non_negative_int(table[key], f"{name}.{key}")
    return table


def validate_preparation(value: Any, expected_mode: str, expected_threshold: float) -> dict[str, Any]:
    """Validate the helper's fixed report and return only aggregate, non-private fields."""

    report = _exact_keys(value, REPORT_KEYS, "top-level")
    if report["SchemaVersion"] != 1 or isinstance(report["SchemaVersion"], bool):
        raise ContractError("helper result SchemaVersion must be integer 1")
    if report["Mode"] != expected_mode:
        raise ContractError(f"helper result mode does not match requested {expected_mode}")
    threshold = _finite_number(report["Threshold"], "helper result Threshold")
    if threshold != expected_threshold:
        raise ContractError("helper result threshold does not match the requested arm")

    options = _exact_keys(report["Options"], OPTIONS_KEYS, "Options")
    expected_search = {"binary": "kBinary", "interpolation": "kInterpolation", "auto": "kAuto"}[expected_mode]
    if options["AccountIndexSearchType"] != expected_search or not isinstance(options["UniformCvThreshold"], str):
        raise ContractError("helper result resolved Account options do not match the requested arm")
    try:
        resolved_threshold = float(options["UniformCvThreshold"])
    except (TypeError, ValueError):
        raise ContractError("helper result UniformCvThreshold is not numeric") from None
    if not math.isfinite(resolved_threshold) or resolved_threshold != expected_threshold:
        raise ContractError("helper result UniformCvThreshold does not match the requested arm")
    if options["AutomaticCompactionsDisabled"] is not True:
        raise ContractError("helper result must disable automatic compactions")

    content = _exact_keys(report["Content"], CONTENT_KEYS, "Content")
    before = _exact_keys(content["Before"], CONTENT_DIGEST_KEYS, "Content.Before")
    after = _exact_keys(content["After"], CONTENT_DIGEST_KEYS, "Content.After")
    before_count = _non_negative_int(before["Count"], "Content.Before.Count")
    after_count = _non_negative_int(after["Count"], "Content.After.Count")
    before_digest = _digest(before["Sha256"], "Content.Before.Sha256")
    after_digest = _digest(after["Sha256"], "Content.After.Sha256")
    if content["Unchanged"] is not True or before_count != after_count or before_digest != after_digest:
        raise ContractError("helper result Account content digest changed")

    tables = _exact_keys(report["Tables"], TABLES_KEYS, "Tables")
    before_table = _table(tables["Before"], "Tables.Before")
    after_table = _table(tables["After"], "Tables.After")
    ssts = _exact_keys(report["Ssts"], SSTS_KEYS, "Ssts")
    before_ssts = ssts["Before"]
    after_ssts = ssts["After"]
    if not isinstance(before_ssts, list) or not isinstance(after_ssts, list) or not before_ssts or not after_ssts:
        raise ContractError("helper result must report non-empty Account SST identities")
    for name, identities in (("Ssts.Before", before_ssts), ("Ssts.After", after_ssts)):
        for index, identity in enumerate(identities):
            item = _exact_keys(identity, SST_KEYS, f"{name}[{index}]")
            _non_negative_int(item["Number"], f"{name}[{index}].Number")
            _non_negative_int(item["SizeBytes"], f"{name}[{index}].SizeBytes")
    before_numbers = {item["Number"] for item in before_ssts}
    after_numbers = {item["Number"] for item in after_ssts}
    if ssts["Rewritten"] is not True or before_ssts == after_ssts or before_numbers & after_numbers:
        raise ContractError("helper result did not rewrite Account SSTs")

    timing = _exact_keys(report["Timing"], TIMING_KEYS, "Timing")
    for key in ("WorkingSetBytesBefore", "WorkingSetBytesAfter", "PeakWorkingSetBytes", "PrivateBytesBefore", "PrivateBytesAfter"):
        _non_negative_int(timing[key], f"Timing.{key}")
    wall_ms = _finite_number(timing["WallMilliseconds"], "Timing.WallMilliseconds")
    cpu_ms = _finite_number(timing["CpuMilliseconds"], "Timing.CpuMilliseconds")
    if wall_ms < 0 or cpu_ms < 0:
        raise ContractError("helper result timing must be non-negative")
    layout = _exact_keys(report["Layout"], LAYOUT_KEYS, "Layout")
    if not all(isinstance(layout[key], str) and layout[key] for key in ("Current", "Manifest")):
        raise ContractError("helper result layout identity is invalid")
    if not all(isinstance(layout[key], list) for key in ("Files", "FlatColumnFamilies")):
        raise ContractError("helper result layout lists are invalid")

    index_bytes = after_table["IndexBytes"]
    reader_memory = after_table["EstimateTableReadersMemory"]
    if (before_count < 1
            or before_table["IndexBytes"] < 1 or index_bytes < 1
            or before_table["EstimateTableReadersMemory"] < 1 or reader_memory < 1):
        raise ContractError("helper result is missing non-zero Account content/index/memory metrics")

    return {
        "schema_version": 1,
        "mode": expected_mode,
        "threshold": expected_threshold,
        "resolved_index_search_type": options["AccountIndexSearchType"],
        "resolved_uniform_cv_threshold": options["UniformCvThreshold"],
        "automatic_compactions_disabled": options["AutomaticCompactionsDisabled"],
        "account_entry_count": before_count,
        "account_sst_count": len(after_ssts),
        "uniform_index_count": after_table["UniformBlocks"],
        "persisted_index_bytes": index_bytes,
        "filter_bytes": after_table["FilterBytes"],
        "table_reader_memory_bytes": reader_memory,
        "account_content_sha256": after_digest,
        "preparation_cpu_seconds": round(cpu_ms / 1000.0, 6),
        "preparation_wall_seconds": round(wall_ms / 1000.0, 6),
        "preparation_working_set_before_bytes": timing["WorkingSetBytesBefore"],
        "preparation_working_set_after_bytes": timing["WorkingSetBytesAfter"],
        "preparation_peak_memory_bytes": timing["PeakWorkingSetBytes"],
        "preparation_private_bytes_before": timing["PrivateBytesBefore"],
        "preparation_private_bytes_after": timing["PrivateBytesAfter"],
        "helper_sha256": "",
    }


def validate_sanitized_preparation(value: Any) -> dict[str, Any]:
    """Validate the aggregate-only preparation artifact before public staging."""

    if not isinstance(value, dict) or set(value) != PUBLIC_KEYS:
        raise ContractError("Account preparation aggregate has an unexpected schema")
    if value["schema_version"] != 1 or isinstance(value["schema_version"], bool):
        raise ContractError("Account preparation aggregate schema_version must be integer 1")
    if value["mode"] not in {"binary", "interpolation", "auto"}:
        raise ContractError("Account preparation aggregate mode is invalid")
    threshold = _finite_number(value["threshold"], "Account preparation aggregate threshold")
    if value["mode"] == "auto" and not 0 <= threshold <= 1:
        raise ContractError("Account preparation aggregate auto threshold is outside [0,1]")
    if value["mode"] != "auto" and threshold != -1:
        raise ContractError("Account preparation aggregate control threshold must be -1")
    expected_search = {"binary": "kBinary", "interpolation": "kInterpolation", "auto": "kAuto"}[value["mode"]]
    if value["resolved_index_search_type"] != expected_search:
        raise ContractError("Account preparation aggregate search type is inconsistent")
    if not isinstance(value["resolved_uniform_cv_threshold"], str):
        raise ContractError("Account preparation aggregate resolved threshold is not a string")
    try:
        resolved_threshold = float(value["resolved_uniform_cv_threshold"])
    except ValueError:
        raise ContractError("Account preparation aggregate resolved threshold is not numeric") from None
    if not math.isfinite(resolved_threshold) or resolved_threshold != threshold:
        raise ContractError("Account preparation aggregate resolved threshold is inconsistent")
    if value["automatic_compactions_disabled"] is not True:
        raise ContractError("Account preparation aggregate must disable automatic compactions")
    for key in ("account_entry_count", "account_sst_count", "uniform_index_count", "persisted_index_bytes", "filter_bytes", "table_reader_memory_bytes",
                "preparation_working_set_before_bytes", "preparation_working_set_after_bytes", "preparation_peak_memory_bytes",
                "preparation_private_bytes_before", "preparation_private_bytes_after"):
        _non_negative_int(value[key], f"Account preparation aggregate {key}")
    if value["account_entry_count"] < 1 or value["account_sst_count"] < 1 or value["persisted_index_bytes"] < 1 or value["table_reader_memory_bytes"] < 1:
        raise ContractError("Account preparation aggregate has missing non-zero metrics")
    _digest(value["account_content_sha256"], "Account preparation aggregate account_content_sha256")
    _digest(value["helper_sha256"], "Account preparation aggregate helper_sha256")
    for key in ("preparation_cpu_seconds", "preparation_wall_seconds"):
        number = _finite_number(value[key], f"Account preparation aggregate {key}")
        if number < 0:
            raise ContractError(f"Account preparation aggregate {key} is negative")
    return value


def validate_result_file(raw_path: str, output_path: str, expected_mode: str, expected_threshold: float, helper_sha256: str) -> None:
    try:
        value = json.loads(Path(raw_path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        raise ContractError(f"helper result is unreadable ({error.__class__.__name__})") from None
    if SHA256.fullmatch(helper_sha256) is None:
        raise ContractError("helper SHA-256 is invalid")
    sanitized = validate_preparation(value, expected_mode, expected_threshold)
    sanitized["helper_sha256"] = helper_sha256
    target = Path(output_path)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(sanitized, sort_keys=True, separators=(",", ":")) + "\n", encoding="utf-8")


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate_parser = subparsers.add_parser("validate", help="validate the boolean opt-in")
    validate_parser.add_argument("value", choices=("true", "false"))
    arms_parser = subparsers.add_parser("arms", help="print the fixed arms as JSON")
    arms_parser.add_argument("--tsv", action="store_true", help="print label,mode,threshold rows")
    result_parser = subparsers.add_parser("validate-result", help="validate and sanitize helper JSON")
    result_parser.add_argument("raw")
    result_parser.add_argument("output")
    result_parser.add_argument("--mode", required=True, choices=("binary", "interpolation", "auto"))
    result_parser.add_argument("--threshold", required=True, type=float)
    result_parser.add_argument("--helper-sha256", required=True)
    args = parser.parse_args(argv)
    try:
        if args.command == "validate":
            print("true" if validate_opt_in(args.value == "true") else "false")
        elif args.command == "arms":
            if args.tsv:
                for arm in ARMS:
                    print(f"{arm['label']}\t{arm['mode']}\t{arm['threshold']:g}")
            else:
                print(json.dumps(ARMS, separators=(",", ":")))
        else:
            validate_result_file(args.raw, args.output, args.mode, args.threshold, args.helper_sha256)
            print(f"validated helper aggregate for mode={args.mode} threshold={args.threshold:g}")
    except ContractError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
