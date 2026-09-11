#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

from verify_priority_log import parse_records, validate_records


def record(mode="observe", **overrides):
    values = {
        "mode": mode,
        "tid": "123",
        "policy_before": "SCHED_OTHER",
        "nice_before": "0",
        "managed_during": "Highest",
        "policy_during": "SCHED_OTHER",
        "nice_during": "0" if mode == "observe" else "-5",
        "policy_after": "SCHED_OTHER",
        "nice_after": "0",
        "success": "true",
    }
    values.update(overrides)
    records, errors = parse_records(
        [
            "level=info EXPB_PRIORITY "
            + " ".join(f"{key}={value}" for key, value in values.items())
        ]
    )
    assert not errors
    return records[0]


def test_valid_observe_record():
    assert validate_records([record()], expected_mode="observe") == []


def test_valid_nice_record_requires_and_restores_minus_five():
    assert validate_records([record("nice")], expected_mode="nice") == []


def test_empty_log_fails():
    errors = validate_records([], [])
    assert any("no valid" in error for error in errors)


def test_failed_record_fails():
    errors = validate_records([record(success="false")])
    assert any("success=false" in error for error in errors)


def test_nice_arm_must_set_minus_five():
    errors = validate_records([record("nice", nice_during="0")], expected_mode="nice")
    assert any("nice=-5" in error for error in errors)


def test_observe_arm_must_leave_nice_unchanged():
    errors = validate_records([record(nice_during="-5")], expected_mode="observe")
    assert any("observe arm changed nice" in error for error in errors)


def test_policy_and_nice_must_be_restored():
    errors = validate_records(
        [record(policy_after="SCHED_BATCH", nice_after="1")], expected_mode="observe"
    )
    assert any("scheduling policy" in error for error in errors)
    assert any("nice value was not restored" in error for error in errors)


def test_ansi_codes_are_ignored():
    records, errors = parse_records(
        [
            "\x1b[32mEXPB_PRIORITY\x1b[0m "
            "mode=observe tid=123 policy_before=SCHED_OTHER nice_before=0 "
            "managed_during=Highest policy_during=SCHED_OTHER nice_during=0 "
            "policy_after=SCHED_OTHER nice_after=0 success=true"
        ]
    )
    assert not errors
    assert validate_records(records) == []


def test_unknown_policy_is_rejected():
    errors = validate_records([record(policy_before="SCHED_UNKNOWN")])
    assert any("unknown scheduling policy" in error for error in errors)


def test_nice_arm_requires_a_real_priority_raise():
    errors = validate_records(
        [record("nice", nice_before="-5")], expected_mode="nice"
    )
    assert any("cannot demonstrate a raise" in error for error in errors)


def test_nice_values_must_be_in_linux_range():
    errors = validate_records([record(nice_before="20")])
    assert any("nice range" in error for error in errors)
