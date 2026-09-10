#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression coverage for the sync-test network selection and L1 mapping.

Both behaviours here were bugs found in production runs: `--network` was passed empty for
every op-*/world-* sync, and filtering on "hoodi" also selected "taiko-hoodi".
"""

import json
import subprocess
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
SELECT = REPO / "scripts" / "sync" / "select-networks.sh"
SYNC_LIB = REPO / ".github" / "actions" / "sync-chain" / "lib.sh"
MATRIX = REPO / "scripts" / "config" / "testnet-matrix.json"


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


if __name__ == "__main__":
    unittest.main()
