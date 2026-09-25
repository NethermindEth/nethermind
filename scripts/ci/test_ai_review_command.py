#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression checks for authorization and scheduling of AI review comments."""

import re
import shutil
import subprocess
import unittest
from pathlib import Path


WORKFLOW = Path(__file__).resolve().parents[2] / ".github/workflows/ai-review-command.yml"


class AiReviewCommandTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text()

    def test_only_authorized_command_job_enters_pr_concurrency_group(self):
        self.assertNotRegex(self.workflow, r"(?m)^concurrency:")
        self.assertRegex(
            self.workflow,
            r"(?m)^  command:\n(?:    (?!concurrency:).*\n)*"
            r"    concurrency:\n"
            r"      group: ai-pr-review-command-\$\{\{ github\.event\.issue\.number \}\}\n"
            r"      cancel-in-progress: false$",
        )

    @unittest.skipUnless(shutil.which("node"), "Node.js is required to evaluate the workflow's JavaScript")
    def test_supported_commands_reject_flag_like_ask_text(self):
        expression = re.search(r"            const supported = .*?;", self.workflow, re.DOTALL)
        self.assertIsNotNone(expression)
        cases = {
            "/review": True,
            "/improve": True,
            "/describe": True,
            "/ask why did this change?": True,
            "/review extra": False,
            "/ask --config.model=expensive why?": False,
            "/ask explain this --config.model=expensive": False,
            '/ask why "--config.model=expensive"': False,
            "/ask why \\--config.model=expensive": False,
        }
        for body, expected in cases.items():
            with self.subTest(body=body):
                script = f"const body = process.argv[1];\n{expression.group()}\nprocess.stdout.write(String(supported));"
                result = subprocess.run(
                    ["node", "-e", script, body], capture_output=True, text=True, check=True,
                )
                self.assertEqual(str(expected).lower(), result.stdout)


if __name__ == "__main__":
    unittest.main()
