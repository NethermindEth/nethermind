#!/usr/bin/env python3
"""Constants and small helpers for the bounded five-arm EXPB sweep."""

from __future__ import annotations

import re
from dataclasses import dataclass


IMAGE_TAG = "nethermindeth/nethermind:rocksdb-auto-base-d66cfd50e0"
IMAGE_DIGEST = "sha256:381e9176ba81ec40fa3e55aa77157b8d1364f74012e55041d939860e3279a745"
IMAGE = f"{IMAGE_TAG}@{IMAGE_DIGEST}"
EXPB_REVISION = "9609a1b66aba5b67c6accc1930cb3e50748ca72f"


@dataclass(frozen=True)
class Arm:
    label: str
    mode: str
    threshold: float
    search_type: str


ARMS: tuple[Arm, ...] = (
    Arm("master", "binary", -1.0, "default"),
    Arm("forced-interpolation", "interpolation", -1.0, "kInterpolation"),
    Arm("auto-cv-0.1", "auto", 0.1, "kAuto"),
    Arm("auto-cv-0.2", "auto", 0.2, "kAuto"),
    Arm("auto-cv-0.5", "auto", 0.5, "kAuto"),
)
ARM_BY_LABEL = {arm.label: arm for arm in ARMS}


class HarnessError(RuntimeError):
    """Raised when a sweep artifact does not satisfy its bounded contract."""


def arm_for_label(label: str) -> Arm:
    try:
        return ARM_BY_LABEL[label]
    except KeyError as error:
        raise HarnessError(f"unknown Account CV arm {label!r}") from error


def safe_executor_name(label: str) -> str:
    return re.sub(r"[^a-zA-Z0-9_-]", "-", label)


def arm_for_snapshot_name(name: str) -> Arm:
    prefix = "expb-executor-"
    if not name.startswith(prefix):
        raise HarnessError(f"unknown Account CV snapshot name {name!r}")
    return arm_for_label(name[len(prefix) :].replace("-0-", "-0."))


def expected_client_container(label: str) -> str:
    return f"expb-executor-{safe_executor_name(label)}-nethermind"


def account_extra_flag(arm: Arm) -> str | None:
    """Return only the Account override requested by a candidate arm."""

    if arm.search_type == "default":
        return None
    option = f"block_based_table_factory.index_block_search_type={arm.search_type};"
    if arm.search_type == "kAuto":
        option += f"block_based_table_factory.uniform_cv_threshold={arm.threshold:g};"
    return f"--Db.FlatAccountDbAdditionalRocksDbOptions={option}"


def self_test() -> None:
    assert [arm.label for arm in ARMS] == [
        "master",
        "forced-interpolation",
        "auto-cv-0.1",
        "auto-cv-0.2",
        "auto-cv-0.5",
    ]
    assert account_extra_flag(ARMS[0]) is None
    assert account_extra_flag(ARMS[1]) == (
        "--Db.FlatAccountDbAdditionalRocksDbOptions="
        "block_based_table_factory.index_block_search_type=kInterpolation;"
    )
    assert account_extra_flag(ARMS[2]).endswith("uniform_cv_threshold=0.1;")
    assert expected_client_container("auto-cv-0.1") == "expb-executor-auto-cv-0-1-nethermind"
    print("expb-account-cv self-test passed")


if __name__ == "__main__":
    import sys

    if sys.argv[1:] == ["--self-test"]:
        self_test()
    else:
        from expb import app

        app()
