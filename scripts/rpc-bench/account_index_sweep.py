#!/usr/bin/env python3
"""Fixed Account-index CV sweep contract and json-bench registry labels."""

from __future__ import annotations

import argparse
import json
import sys
from typing import Any, Sequence


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

# json-bench rejects dashes in client registry names. Keep this mapping separate from public
# labels, which remain in result paths, parity rows and summaries.
REGISTRY_LABELS = {
    "master": "account_master",
    "forced-interpolation": "account_forced_interpolation",
    "auto-cv-0.2": "account_auto_cv_02",
    "auto-cv-0.05": "account_auto_cv_005",
    "auto-cv-0.1": "account_auto_cv_01",
    "auto-cv-0.15": "account_auto_cv_015",
    "auto-cv-0.25": "account_auto_cv_025",
    "auto-cv-0.35": "account_auto_cv_035",
    "auto-cv-0.5": "account_auto_cv_05",
}


class ContractError(ValueError):
    """Raised when a fixed sweep contract is invalid."""


def arms() -> list[dict[str, Any]]:
    """Return a copy of the fixed ordered arm list."""

    return [dict(arm) for arm in ARMS]


def registry_label(public_label: str) -> str:
    """Return the internal json-bench registry name for a public arm label."""

    try:
        return REGISTRY_LABELS[public_label]
    except KeyError:
        raise ContractError(f"unknown Account arm label {public_label!r}") from None


def validate_opt_in(value: Any) -> bool:
    """Validate the structured option and return whether the sweep is enabled."""

    if not isinstance(value, bool):
        raise ContractError("account_index_sweep must be the JSON boolean true or false")
    return value


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)
    validate_parser = subparsers.add_parser("validate", help="validate the boolean opt-in")
    validate_parser.add_argument("value", choices=("true", "false"))
    arms_parser = subparsers.add_parser("arms", help="print the fixed arms as JSON")
    arms_parser.add_argument("--tsv", action="store_true", help="print label,mode,threshold rows")
    registry_parser = subparsers.add_parser("registry-label", help="print an internal registry label")
    registry_parser.add_argument("label", choices=tuple(REGISTRY_LABELS))
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
            print(registry_label(args.label))
    except ContractError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
