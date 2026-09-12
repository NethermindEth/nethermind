#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression coverage for the gcp-runner zone ordering and create-error classification."""

import os
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
ACTION = REPO / ".github" / "actions" / "gcp-runner"
LIB = ACTION / "lib.sh"

ZONES = ",".join(
    f"{region}-{suffix}"
    for region, suffixes in [
        ("europe-west1", "bcd"),
        ("europe-west4", "abc"),
        ("europe-north1", "abc"),
        ("europe-west3", "abc"),
        ("europe-west2", "abc"),
        ("europe-west6", "abc"),
    ]
    for suffix in suffixes
)


def sh(snippet):
    """Evaluates a snippet with the gcp-runner helpers sourced."""
    out = subprocess.run(
        ["bash", "-c", f'set -euo pipefail; . "{LIB}"; {snippet}'],
        capture_output=True,
        text=True,
        check=True,
    )
    return out.stdout.strip()


def order(seed, zones=ZONES):
    result = sh(f'order_zones "{seed}" "{zones}"')
    return result.split(",") if result else []


def regions(zones):
    """The region of each zone, with consecutive duplicates collapsed."""
    out = []
    for zone in zones:
        region = zone.rsplit("-", 1)[0]
        if not out or out[-1] != region:
            out.append(region)
    return out


class OrderZonesTest(unittest.TestCase):
    def test_every_zone_is_kept_exactly_once(self):
        self.assertCountEqual(order("seed"), ZONES.split(","))

    def test_zones_of_a_region_stay_contiguous(self):
        for seed in ("mainnet", "gnosis", "hoodi", "sepolia"):
            collapsed = regions(order(seed))
            self.assertEqual(len(collapsed), len(set(collapsed)), seed)

    def test_the_same_seed_gives_the_same_order(self):
        self.assertEqual(order("gh-f-1-master-mainnet"), order("gh-f-1-master-mainnet"))

    def test_concurrent_runners_start_in_different_regions(self):
        starts = {
            order(name)[0].rsplit("-", 1)[0]
            for name in (
                "gh-f-1-master-mainnet",
                "gh-f-1-master-gnosis",
                "gh-hp-1-master-mainnet",
                "gh-hp-1-master-gnosis",
            )
        }
        self.assertGreater(len(starts), 1)

    def test_a_single_region_list_is_returned_complete(self):
        single = "europe-west1-b,europe-west1-c,europe-west1-d"
        self.assertCountEqual(order("seed", single), single.split(","))

    def test_a_single_zone_is_unchanged(self):
        self.assertEqual(order("seed", "europe-west1-b"), ["europe-west1-b"])

    def test_whitespace_and_empty_entries_are_dropped(self):
        self.assertCountEqual(
            order("seed", " europe-west1-b , ,europe-west4-a "),
            ["europe-west1-b", "europe-west4-a"],
        )

    def test_an_empty_list_yields_nothing(self):
        self.assertEqual(order("seed", ""), [])


class CreateErrorClassificationTest(unittest.TestCase):
    """Each gcloud message must match exactly one class, so create.sh picks one branch."""

    CASES = {
        "QUOTA_CREATE_ERR": [
            "ERROR: (gcloud.compute.instances.create) Could not fetch resource:"
            " - Quota 'LOCAL_SSD_TOTAL_GB' exceeded. Limit: 15000.0 in region europe-west1.",
            "Constraint QUOTA_EXCEEDED violated",
        ],
        "RETRYABLE_CREATE_ERR": [
            "ERROR: (gcloud.compute.instances.create) Could not fetch resource:"
            " - The zone 'projects/p/zones/europe-west1-b' does not have enough resources"
            " available to fulfill the request.",
            "ZONE_RESOURCE_POOL_EXHAUSTED",
        ],
        "FATAL_CREATE_ERR": [
            "ERROR: (gcloud.compute.instances.create) PERMISSION_DENIED: Required"
            " 'compute.instances.create' permission for 'projects/p/zones/z/instances/i'",
        ],
    }

    def matches(self, message):
        names = ["QUOTA_CREATE_ERR", "RETRYABLE_CREATE_ERR", "FATAL_CREATE_ERR"]
        hits = sh(
            "; ".join(
                f'grep -qE "${name}" <<<{message!r} && echo {name} || true' for name in names
            )
        )
        return hits.split()

    def test_each_message_matches_exactly_its_own_class(self):
        for name, messages in self.CASES.items():
            for message in messages:
                with self.subTest(message=message[:60]):
                    self.assertEqual(self.matches(message), [name])

    def test_an_unrecognised_message_matches_nothing(self):
        self.assertEqual(self.matches("ERROR: something entirely new"), [])


class CreateZoneWalkTest(unittest.TestCase):
    """Runs create.sh against stubbed gcloud/gh/curl to check which zones it actually tries."""

    GCLOUD_STUB = """#!/usr/bin/env bash
    zone=""
    for a in "$@"; do case "$a" in --zone=*) zone="${a#--zone=}" ;; esac; done
    case "$*" in *"instances delete"*|*"instances list"*|*get-serial-port-output*) exit 0 ;; esac
    echo "$zone" >> "$ATTEMPTS"
    if [ "$zone" = "${SUCCEED_ZONE:-}" ]; then
      echo '[{"networkInterfaces":[{"accessConfigs":[{"natIP":"1.2.3.4"}]}]}]'
      exit 0
    fi
    case ",${CAPACITY_ZONES:-}," in
      *",${zone},"*) echo "ERROR: The zone '$zone' does not have enough resources available." >&2 ;;
      *) echo "ERROR: - Quota 'LOCAL_SSD_TOTAL_GB' exceeded. Limit: 15000.0 in region ${zone%-*}." >&2 ;;
    esac
    exit 1
    """

    GH_STUB = """#!/usr/bin/env bash
    [ -t 0 ] || cat >/dev/null
    echo '{"runner":{"id":42},"encoded_jit_config":"ZmFrZQ=="}'
    """

    CURL_STUB = """#!/usr/bin/env bash
    out=""; prev=""
    for a in "$@"; do [ "$prev" = "-o" ] && out="$a"; prev="$a"; done
    [ -n "$out" ] && echo '{"status":"online"}' > "$out"
    echo 200
    """

    ENV = {
        "GITHUB_REPOSITORY": "o/r",
        "GITHUB_RUN_ID": "999",
        "GITHUB_RUN_ATTEMPT": "1",
        "GH_TOKEN": "x",
        "RUNNER_LABEL": "f-1-master-mainnet",
        "RUNNER_GROUP_ID": "1",
        "RUNNER_VERSION": "2.336.0",
        "RUNNER_SERVICE_ACCOUNT": "sa@p.iam.gserviceaccount.com",
        "PROJECT_ID": "p",
        "MACHINE_TYPE": "c2-standard-8",
        "LOCAL_SSD_COUNT": "2",
        "SPOT_FALLBACK_TO_STANDARD": "true",
        "BOOT_DISK_SIZE": "100",
        "BOOT_DISK_TYPE": "pd-balanced",
        "BOOT_TIMEOUT": "600",
        "MAX_RUN_DURATION": "7h",
        "DATA_MOUNT": "/data",
        "NETWORK": "gh-runner",
        "SUBNET": "gh-runner",
        "NETWORK_TAG": "gh-runner",
        "IMAGE_FAMILY": "ubuntu-2404-lts-amd64",
        "IMAGE_PROJECT": "ubuntu-os-cloud",
    }

    def create(self, zones, **overrides):
        """Returns (exit code, stdout, the zones gcloud was asked to create in, in order)."""
        with tempfile.TemporaryDirectory() as tmp:
            tmp = Path(tmp)
            binaries = tmp / "bin"
            binaries.mkdir()
            for name, body in (
                ("gcloud", self.GCLOUD_STUB),
                ("gh", self.GH_STUB),
                ("curl", self.CURL_STUB),
            ):
                stub = binaries / name
                stub.write_text(textwrap.dedent(body))
                stub.chmod(0o755)

            attempts = tmp / "attempts"
            attempts.touch()
            env = {
                **os.environ,
                **self.ENV,
                "PATH": f"{binaries}:{os.environ['PATH']}",
                "ATTEMPTS": str(attempts),
                "RUNNER_TEMP": str(tmp),
                "GITHUB_OUTPUT": str(tmp / "out"),
                "ZONES": zones,
                "ZONE_ORDER": "listed",
                "PROVISIONING_MODEL": "STANDARD",
                **overrides,
            }
            run = subprocess.run(
                ["bash", str(ACTION / "create.sh")],
                capture_output=True,
                text=True,
                stdin=subprocess.DEVNULL,
                env=env,
            )
            return run.returncode, run.stdout + run.stderr, attempts.read_text().split()

    def test_a_quota_error_skips_the_regions_other_zones_and_tries_the_next(self):
        code, out, attempts = self.create(
            "europe-west1-b,europe-west1-c,europe-west1-d,europe-west4-a,europe-west4-b",
            SUCCEED_ZONE="europe-west4-a",
        )
        self.assertEqual(code, 0, out)
        self.assertEqual(attempts, ["europe-west1-b", "europe-west4-a"])

    def test_a_capacity_error_still_walks_the_sibling_zones(self):
        code, out, attempts = self.create(
            "europe-west1-b,europe-west1-c,europe-west1-d",
            CAPACITY_ZONES="europe-west1-b,europe-west1-c",
            SUCCEED_ZONE="europe-west1-d",
        )
        self.assertEqual(code, 0, out)
        self.assertEqual(
            attempts, ["europe-west1-b", "europe-west1-c", "europe-west1-d"]
        )

    def test_the_standard_retry_tries_a_region_spot_found_at_quota(self):
        code, out, attempts = self.create(
            "europe-west1-b,europe-west4-a",
            PROVISIONING_MODEL="SPOT",
            SUCCEED_ZONE="",
        )
        self.assertEqual(code, 1, out)
        self.assertEqual(
            attempts,
            ["europe-west1-b", "europe-west4-a", "europe-west1-b", "europe-west4-a"],
        )
        self.assertIn("regions at quota:", out)

    def test_rotation_moves_the_first_zone_tried_off_the_head_of_the_list(self):
        zones = "europe-west1-b,europe-west1-c,europe-west4-a,europe-north1-a"
        _, _, listed = self.create(zones, SUCCEED_ZONE="")
        _, _, rotated = self.create(zones, ZONE_ORDER="rotate", SUCCEED_ZONE="")
        self.assertEqual(listed[0], "europe-west1-b")
        self.assertNotEqual(rotated[0], listed[0])


if __name__ == "__main__":
    unittest.main()
