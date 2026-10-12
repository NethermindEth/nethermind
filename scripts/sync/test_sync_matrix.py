#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression coverage for the sync-test network selection and L1 mapping.

Both behaviours here were bugs found in production runs: `--network` was passed empty for
every op-*/world-* sync, and filtering on "hoodi" also selected "taiko-hoodi".
"""

import json
import re
import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SELECT = REPO / "scripts" / "sync" / "select-networks.sh"
CHECK_SHAPES = REPO / "scripts" / "sync" / "check-runner-shapes.sh"
SYNC_LIB = REPO / ".github" / "actions" / "sync-chain" / "lib.sh"
MATRIX = REPO / "scripts" / "config" / "testnet-matrix.json"
MATRIX_WORKFLOWS = [
    REPO / ".github" / "workflows" / name
    for name in ("sync-supported-chains.yml", "sync-master-validation.yml")
]


def provisioning_expression(workflow):
    """The jq expression a matrix builder uses to pick each entry's provisioning model."""
    match = re.search(
        r"provisioning_model: \((.*?)\n\s*\),", workflow.read_text(), re.DOTALL
    )
    if match is None:
        raise AssertionError(f"{workflow.name} has no provisioning_model expression")
    return " ".join(match.group(1).split())


def select(matrix, network_filter):
    """Runs select-networks.sh and returns the network names it kept."""
    out = subprocess.run(
        [str(SELECT), network_filter],
        input=json.dumps(matrix),
        capture_output=True,
        text=True,
        check=True,
    )
    return [entry["network"] for entry in json.loads(out.stdout)]


def check_shapes(matrix):
    return subprocess.run(
        [str(CHECK_SHAPES)], input=json.dumps(matrix), capture_output=True, text=True
    )


def sh(snippet):
    """Evaluates a snippet with the sync-chain helpers sourced."""
    out = subprocess.run(
        ["bash", "-c", f'set -euo pipefail; . "{SYNC_LIB}"; {snippet}'],
        capture_output=True,
        text=True,
        check=True,
    )
    return out.stdout.strip()


class SelectNetworksTest(unittest.TestCase):
    def setUp(self):
        self.matrix = json.loads(MATRIX.read_text())

    def test_exact_name_does_not_select_a_longer_network(self):
        # "hoodi" is a substring of "taiko-hoodi"; an exact name must win.
        self.assertEqual(select(self.matrix, "hoodi"), ["hoodi"])

    def test_exact_name_of_the_longer_network(self):
        self.assertEqual(select(self.matrix, "taiko-hoodi"), ["taiko-hoodi"])

    def test_partial_name_still_matches_every_network_containing_it(self):
        self.assertEqual(select(self.matrix, "taiko"), ["taiko-alethia", "taiko-hoodi"])

    def test_partial_name_selects_the_l2_sepolia_variants(self):
        self.assertEqual(select(self.matrix, "-sepolia"), ["op-sepolia", "world-sepolia"])

    def test_exact_name_keeps_every_entry_for_that_network(self):
        # Callers may filter an already expanded matrix, where a network appears per mode.
        expanded = [
            {"network": "hoodi", "mode": "Flat"},
            {"network": "hoodi", "mode": "HalfPath"},
            {"network": "taiko-hoodi", "mode": "Flat"},
        ]
        self.assertEqual(select(expanded, "hoodi"), ["hoodi", "hoodi"])

    def test_no_match_yields_an_empty_matrix(self):
        # The workflows turn this into a hard failure rather than a run that validates nothing.
        self.assertEqual(select(self.matrix, "nope"), [])

    def test_empty_filter_passes_the_matrix_through(self):
        self.assertEqual(
            select(self.matrix, ""), [entry["network"] for entry in self.matrix]
        )


class ResolveL1NetworkTest(unittest.TestCase):
    def test_op_and_world_sepolia_both_resolve_to_sepolia(self):
        self.assertEqual(sh('resolve_l1_network op-sepolia'), "sepolia")
        self.assertEqual(sh('resolve_l1_network world-sepolia'), "sepolia")

    def test_op_and_world_mainnet_both_resolve_to_mainnet(self):
        self.assertEqual(sh('resolve_l1_network op-mainnet'), "mainnet")
        self.assertEqual(sh('resolve_l1_network world-mainnet'), "mainnet")

    def test_non_l2_networks_are_unchanged(self):
        for network in ("hoodi", "sepolia", "mainnet", "gnosis", "chiado"):
            self.assertEqual(sh(f'resolve_l1_network {network}'), network)

    def test_only_world_networks_select_the_worldchain_chain(self):
        for network in ("world-sepolia", "world-mainnet"):
            self.assertEqual(sh(f'is_worldchain {network} && echo yes || echo no'), "yes")
        for network in ("op-sepolia", "op-mainnet", "hoodi", "mainnet"):
            self.assertEqual(sh(f'is_worldchain {network} && echo yes || echo no'), "no")


class TestnetMatrixTest(unittest.TestCase):
    def test_every_entry_carries_the_fields_the_workflows_read(self):
        required = {
            "network", "timeout", "machine_type", "local_ssd_count", "spot",
            "cl", "cl_image", "checkpoint-sync-url",
        }
        for entry in json.loads(MATRIX.read_text()):
            missing = required - set(entry)
            self.assertEqual(missing, set(), f"{entry.get('network')} is missing {missing}")
            self.assertIsInstance(entry["local_ssd_count"], int)
            self.assertIsInstance(entry["spot"], bool)

    def test_every_entry_syncs_on_local_ssd_its_machine_type_can_carry(self):
        matrix = json.loads(MATRIX.read_text())
        for entry in matrix:
            with self.subTest(network=entry["network"]):
                self.assertRegex(entry["machine_type"], r"^(c2|c3d)-")
                # Zero leaves the sync on the 100 GB boot disk; startup-script.sh only warns.
                self.assertGreaterEqual(entry["local_ssd_count"], 1)
        result = check_shapes(matrix)
        self.assertEqual(result.returncode, 0, result.stderr)


class RunnerShapeCheckTest(unittest.TestCase):
    def check(self, machine_type, local_ssd_count):
        entry = {"network": "n", "machine_type": machine_type, "local_ssd_count": local_ssd_count}
        return entry, check_shapes([entry])

    def test_shapes_the_runner_can_boot_pass_through_unchanged(self):
        for machine_type, count in (
            ("c2-standard-8", 2),
            ("c3d-standard-8-lssd", 1),
            ("c3d-standard-30-lssd", 2),
            ("c3d-highmem-8-lssd", 1),
            ("c3d-highmem-360-lssd", 32),
            ("c3d-standard-8", 0),
            ("c3-standard-8-lssd", 2),
            ("n2-standard-8", 4),
        ):
            with self.subTest(machine_type=machine_type, local_ssd_count=count):
                entry, result = self.check(machine_type, count)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(json.loads(result.stdout), [entry])

    def test_shapes_the_runner_cannot_boot_are_rejected(self):
        for machine_type, count in (
            ("c3d-standard-8-lssd", 2),
            ("c3d-standard-16-lssd", 2),
            ("c3d-highmem-8-lssd", 2),
            ("c3d-highcpu-8-lssd", 1),
            ("c3d-standard-8", 1),
        ):
            with self.subTest(machine_type=machine_type, local_ssd_count=count):
                _, result = self.check(machine_type, count)
                self.assertEqual(result.returncode, 1)
                self.assertIn(f"::error title=Sync matrix::n: {machine_type}", result.stderr)

    def test_both_matrix_builders_check_the_final_matrix(self):
        for workflow in MATRIX_WORKFLOWS:
            with self.subTest(workflow=workflow.name):
                text = workflow.read_text()
                self.assertIn("check-runner-shapes.sh", text)
                # After the machine_type override is applied, or the override goes unchecked.
                self.assertLess(text.index("$machine_type"), text.index("check-runner-shapes.sh"))


class ProvisioningModelTest(unittest.TestCase):
    """STANDARD must only come from an explicit request; anything unstated resolves to SPOT."""

    def resolve(self, model_input, config):
        out = subprocess.run(
            [
                "jq", "-nr",
                "--arg", "model", model_input,
                "--argjson", "config", json.dumps(config),
                provisioning_expression(MATRIX_WORKFLOWS[0]),
            ],
            capture_output=True,
            text=True,
            check=True,
        )
        return out.stdout.strip()

    def test_both_matrix_builders_share_one_expression(self):
        first, second = (provisioning_expression(w) for w in MATRIX_WORKFLOWS)
        self.assertEqual(first, second)

    def test_an_entry_without_a_spot_key_resolves_to_spot(self):
        self.assertEqual(self.resolve("Default", {"network": "new-chain"}), "SPOT")

    def test_a_null_spot_resolves_to_spot(self):
        self.assertEqual(self.resolve("Default", {"spot": None}), "SPOT")

    def test_spot_true_resolves_to_spot(self):
        self.assertEqual(self.resolve("Default", {"spot": True}), "SPOT")

    def test_an_explicit_spot_false_is_honoured(self):
        self.assertEqual(self.resolve("Default", {"spot": False}), "STANDARD")

    def test_an_empty_input_behaves_like_default(self):
        self.assertEqual(self.resolve("", {}), "SPOT")

    def test_the_spot_input_overrides_spot_false(self):
        self.assertEqual(self.resolve("Spot", {"spot": False}), "SPOT")

    def test_the_standard_input_overrides_spot_true(self):
        self.assertEqual(self.resolve("Standard", {"spot": True}), "STANDARD")


if __name__ == "__main__":
    unittest.main()
