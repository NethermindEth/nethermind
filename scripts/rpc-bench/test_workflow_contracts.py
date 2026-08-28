#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[2] / ".github" / "workflows" / "run-rpc-benchmarks.yml"

# tool_config keys the sweep reads that do not shape what the cell measures, so they stay out of the cell
# fingerprint: the arms themselves and how they are compared, repeats of the same cell, the json-bench-only
# workload keys, reporting/gate knobs, and the parity-diff switches.
NON_SHAPING_KEYS = {
    "clients", "corpus_baseline", "rounds",
    "iso_configs", "iso_duration",
    "db_isolation_allow_snapshot_mutation", "resource_sampling", "parity_diffs",
    "max_divergence_indexes", "max_fail_rate_pct",
}


class RpcBenchmarkWorkflowTests(unittest.TestCase):
    @staticmethod
    def step(name):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index(f"      - name: {name}")
        end = workflow.index("\n      - name:", start + 1)
        return workflow[start:end]

    @classmethod
    def cell_keys(cls):
        resolve = cls.step("Resolve configuration")
        return set(json.loads(re.search(r"cell_keys='(\[.*?\])'", resolve, re.S).group(1)))

    def test_cache_keys_carry_the_cell_fingerprint(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")

        # Aggregates measured on a differently shaped cell are not a baseline for this run: the fingerprint is part
        # of the key on both sides, so a mismatch misses and the run measures master in-job instead.
        prefix = ("rpc-corpus-baseline-${{ needs.resolve.outputs.arch }}-${{ needs.resolve.outputs.corpus_key }}"
                  "-${{ needs.resolve.outputs.cell_key }}-")
        self.assertIn(f"restore-keys: {prefix}", workflow)
        self.assertIn(f"key: {prefix}lookup-${{{{ github.run_id }}}}", workflow)
        self.assertIn(f"key: {prefix}${{{{ github.run_id }}}}", workflow)

    def test_every_cell_shaping_sweep_knob_is_fingerprinted(self):
        exported = {key for key, _ in re.findall(r"\b([a-z][a-z_0-9]*):([A-Z][A-Z_0-9]*)\b", self.step("Run RPC sweep"))}
        self.assertIn("corpus_requests", exported)
        self.assertIn("node_env_vars", exported)

        # rps_list is exported separately (an absent list means the default, an empty one means no k6 cells).
        self.assertEqual(exported - NON_SHAPING_KEYS, self.cell_keys() - {"rps_list"})

    def test_the_node_envelope_is_fingerprinted_too(self):
        resolve = self.step("Resolve configuration")

        # A cell measured under a different CPU cap or container envelope is not comparable either.
        shape = re.search(r"cell_shape=\"\$\(jq.*?\)\"", resolve, re.S).group(0)
        for knob in ("cpu_max_freq_khz", "cpuset", "memory"):
            self.assertIn(knob, shape)
        self.assertIn('cell_key="$(printf', resolve)

    def test_the_recorded_baseline_names_the_cell_it_was_measured_on(self):
        assemble = self.step("Assemble this run as the master baseline")

        self.assertIn("cell_key: $cell_key", assemble)
        self.assertIn("cell: $cell", assemble)

    def test_arm_reclaim_keep_list_removes_per_arm_image_options(self):
        reclaim_step = self.step("Reclaim root disk before pulling")

        # The sweep parser removes #K=V before Docker sees an image. The disk reclaim list must use the same ref.
        self.assertRegex(reclaim_step, r"sweep_keep=.*s/\.\*@//; s/#\[\^\[:space:\]\]\*\$//")
        self.assertRegex(reclaim_step, r"keep_re=.*s/#\[\^\[:space:\]\]\*\$//")

        refs = ["registry.example.net/nethermind:pr#NETHERMIND_FOO=one", "registry.example.net/nethermind:base"]
        normalized = [re.sub(r"#[^\s]*$", "", ref) for ref in refs]
        self.assertEqual(normalized, ["registry.example.net/nethermind:pr", "registry.example.net/nethermind:base"])

    def test_arm_reclaim_headroom_follows_the_docker_image_store(self):
        reclaim_step = self.step("Reclaim root disk before pulling")

        # A runner may keep its images off `/`; charging the pull headroom to `/` then blocks a box that has room.
        self.assertIn("{{.DockerRootDir}}", reclaim_step)
        self.assertIn("containerd config dump", reclaim_step)
        self.assertIn('avail_gb "${image_store}"', reclaim_step)
        self.assertNotIn("MIN_FREE_GB", reclaim_step)
        # `/` still has to hold RUNNER_TEMP: k6 fixtures, corpus results and logs.
        self.assertIn("MIN_ROOT_FREE_GB", reclaim_step)
        self.assertIn("avail_gb /", reclaim_step)

if __name__ == "__main__":
    unittest.main()
