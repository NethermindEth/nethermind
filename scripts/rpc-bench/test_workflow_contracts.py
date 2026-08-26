#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import re
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).parents[2] / ".github" / "workflows" / "run-rpc-benchmarks.yml"


class RpcBenchmarkWorkflowTests(unittest.TestCase):
    def test_arm_reclaim_keep_list_removes_per_arm_image_options(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        start = workflow.index("      - name: Reclaim root disk before pulling")
        end = workflow.index("\n      - name:", start + 1)
        reclaim_step = workflow[start:end]

        # The sweep parser removes #K=V before Docker sees an image. The disk reclaim list must use the same ref.
        self.assertRegex(reclaim_step, r"sweep_keep=.*s/\.\*@//; s/#\[\^\[:space:\]\]\*\$//")
        self.assertRegex(reclaim_step, r"keep_re=.*s/#\[\^\[:space:\]\]\*\$//")

        refs = ["registry.example.net/nethermind:pr#NETHERMIND_FOO=one", "registry.example.net/nethermind:base"]
        normalized = [re.sub(r"#[^\s]*$", "", ref) for ref in refs]
        self.assertEqual(normalized, ["registry.example.net/nethermind:pr", "registry.example.net/nethermind:base"])


if __name__ == "__main__":
    unittest.main()
