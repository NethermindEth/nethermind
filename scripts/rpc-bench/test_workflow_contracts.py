#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[2] / ".github" / "workflows" / "run-rpc-benchmarks.yml"


class RpcBenchmarkWorkflowTests(unittest.TestCase):
    @staticmethod
    def reclaim_step():
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index("      - name: Reclaim root disk before pulling")
        end = workflow.index("\n      - name:", start + 1)
        return workflow[start:end]

    def test_arm_reclaim_keep_list_removes_per_arm_image_options(self):
        reclaim_step = self.reclaim_step()

        # The sweep parser removes #K=V before Docker sees an image. The disk reclaim list must use the same ref.
        self.assertRegex(reclaim_step, r"sweep_keep=.*s/\.\*@//; s/#\[\^\[:space:\]\]\*\$//")
        self.assertRegex(reclaim_step, r"keep_re=.*s/#\[\^\[:space:\]\]\*\$//")

        refs = ["registry.example.net/nethermind:pr#NETHERMIND_FOO=one", "registry.example.net/nethermind:base"]
        normalized = [re.sub(r"#[^\s]*$", "", ref) for ref in refs]
        self.assertEqual(normalized, ["registry.example.net/nethermind:pr", "registry.example.net/nethermind:base"])

    def test_arm_reclaim_headroom_follows_the_docker_image_store(self):
        reclaim_step = self.reclaim_step()

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
