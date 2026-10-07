#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression checks for authorization and scheduling of AI review comments."""

import json
import re
import shutil
import subprocess
import unittest
import textwrap
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
            "/REVIEW": True,
            "/ask": False,
            "/ask   ": False,
            "/review\nextra": False,
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


    @unittest.skipUnless(shutil.which("node"), "Node.js is required to evaluate the workflow's JavaScript")
    def test_authorization_requires_live_write_access_and_propagates_api_errors(self):
        script = re.search(r"          script: \|\n(.*?)\n\n  command:", self.workflow, re.DOTALL)
        self.assertIsNotNone(script)
        body = textwrap.dedent(script.group(1))
        cases = [
            ("/review", "User", "write", None, True, True, None),
            ("/REVIEW ", "User", "maintain", None, True, True, None),
            ("/ask why?", "User", "admin", None, True, True, None),
            ("/review", "User", "read", None, False, True, None),
            ("/review", "User", "triage", None, False, True, None),
            ("/review", "User", None, 404, False, True, None),
            ("/review", "User", None, 403, False, True, 403),
            ("/review", "User", None, 500, False, True, 500),
            ("/review", "Bot", "admin", None, False, False, None),
            ("thanks", "User", "admin", None, False, False, None),
            ("/review extra", "User", "admin", None, False, False, None),
        ]
        harness = """
            const [text, type, permission, apiError] = JSON.parse(process.argv[1]);
            let called = false, allowed = false;
            const context = {repo: {owner: 'test', repo: 'test'}, payload: {comment: {body: text, user: {type, login: 'tester'}}}};
            const github = {rest: {repos: {getCollaboratorPermissionLevel: async () => {
                called = true;
                if (apiError) throw {status: apiError};
                return {data: {permission}};
            }}}};
            const core = {info: () => {}, setOutput: (key, value) => {allowed = key === 'allowed' && value === 'true';}};
            const run = async () => { WORKFLOW_SCRIPT };
            run().then(() => process.stdout.write(JSON.stringify({called, allowed, error: null})),
                error => process.stdout.write(JSON.stringify({called, allowed, error: error.status})));
        """.replace("WORKFLOW_SCRIPT", body)
        for text, user_type, permission, api_error, allowed, called, error in cases:
            with self.subTest(text=text, user_type=user_type, permission=permission, api_error=api_error):
                result = subprocess.run(["node", "-e", harness, json.dumps([text, user_type, permission, api_error])],
                    capture_output=True, text=True, check=True)
                self.assertEqual({"called": called, "allowed": allowed, "error": error}, json.loads(result.stdout))


if __name__ == "__main__":
    unittest.main()
