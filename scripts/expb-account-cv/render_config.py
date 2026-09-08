#!/usr/bin/env python3
"""Render the fixed five-arm Fusaka EXPB configuration."""

from __future__ import annotations

import argparse
import copy
import os
import sys
from pathlib import Path
from typing import Any, Mapping, Sequence

try:
    import yaml
except ImportError as error:  # pragma: no cover - the pinned EXPB environment supplies PyYAML
    raise SystemExit(f"render_config.py requires PyYAML: {error}") from error

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from expb_account_cv import ARMS, IMAGE, account_extra_flag  # noqa: E402


PAYLOAD_AMOUNT = 1000
PAYLOAD_DELAY = 0
WRITE_BUFFER_FLOOR = "--FlatDb.PersistenceWriteBufferFloor=67108864"


def _scenario_template(scenarios: Mapping[str, Any]) -> dict[str, Any]:
    if "nethermind" in scenarios:
        value = scenarios["nethermind"]
    elif len(scenarios) == 1:
        value = next(iter(scenarios.values()))
    else:
        raise ValueError("source config must contain the standard nethermind scenario")
    if not isinstance(value, dict):
        raise ValueError("source nethermind scenario must be a mapping")
    return copy.deepcopy(value)


def _without_account_overrides(flags: list[str]) -> list[str]:
    kept: list[str] = []
    for flag in flags:
        if flag.startswith("--FlatDb.PersistenceWriteBufferFloor="):
            continue
        if flag.startswith("--Db.FlatAccountDbAdditionalRocksDbOptions="):
            continue
        kept.append(flag)
    return kept


def build_config(
    source_path: Path,
    output_path: Path,
    image: str = IMAGE,
    work_root: Path | None = None,
    output_root: Path | None = None,
    expected_snapshot_source: str | None = None,
    expected_cpuset: str | None = None,
    expected_memory: str | None = None,
) -> dict[str, Any]:
    """Render exactly five sequential scenarios from the standard Fusaka config."""

    with source_path.open("r", encoding="utf-8") as handle:
        document = yaml.safe_load(handle)
    if not isinstance(document, dict) or not isinstance(document.get("scenarios"), dict):
        raise ValueError("source config has no scenarios mapping")
    template = _scenario_template(document["scenarios"])
    if expected_snapshot_source is not None and template.get("snapshot_source") != expected_snapshot_source:
        raise ValueError(f"source snapshot must be {expected_snapshot_source}")
    resources = document.get("resources", {})
    if expected_cpuset is not None and resources.get("cpuset") != expected_cpuset:
        raise ValueError(f"source cpuset must be {expected_cpuset}")
    if expected_memory is not None and resources.get("mem") != expected_memory:
        raise ValueError(f"source memory must be {expected_memory}")
    scenarios: dict[str, Any] = {}
    for arm in ARMS:
        scenario = copy.deepcopy(template)
        scenario["image"] = image
        scenario["amount"] = PAYLOAD_AMOUNT
        scenario["delay"] = PAYLOAD_DELAY
        scenario["repeat"] = 1
        scenario["snapshot_backend"] = "overlay"
        flags = scenario.get("extra_flags", [])
        if not isinstance(flags, list) or not all(isinstance(flag, str) for flag in flags):
            raise ValueError(f"source extra_flags for {arm.label} is not a string list")
        account_flag = account_extra_flag(arm)
        scenario["extra_flags"] = _without_account_overrides(flags) + [WRITE_BUFFER_FLOOR]
        if account_flag is not None:
            scenario["extra_flags"].append(account_flag)
        scenarios[arm.label] = scenario
    document["scenarios"] = scenarios
    if work_root is not None:
        document.setdefault("paths", {})["work"] = str(work_root)
    if output_root is not None:
        document.setdefault("paths", {})["outputs"] = str(output_root)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with output_path.open("w", encoding="utf-8") as handle:
        yaml.safe_dump(document, handle, sort_keys=False, default_flow_style=False)
    return document


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source")
    parser.add_argument("output")
    parser.add_argument("--work-root", required=True)
    parser.add_argument("--output-root", required=True)
    parser.add_argument("--image", default=IMAGE)
    parser.add_argument("--expected-snapshot-source")
    parser.add_argument("--expected-cpuset")
    parser.add_argument("--expected-memory")
    arguments = parser.parse_args(argv)
    build_config(
        Path(arguments.source),
        Path(arguments.output),
        image=arguments.image,
        work_root=Path(arguments.work_root),
        output_root=Path(arguments.output_root),
        expected_snapshot_source=arguments.expected_snapshot_source,
        expected_cpuset=arguments.expected_cpuset,
        expected_memory=arguments.expected_memory,
    )
    print(f"rendered fixed five-arm config: {arguments.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
