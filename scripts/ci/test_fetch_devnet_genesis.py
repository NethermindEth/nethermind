# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("fetch-devnet-genesis.sh")


class FetchDevnetGenesisTests(unittest.TestCase):
    def test_download_failures_and_repository_coverage(self):
        cases = [
            ({"first/devnet-1": 200, "first/devnet-2": 404, "second/devnet-1": 200}, True, 2),
            ({"first/devnet-1": 200, "first/devnet-2": 200, "second/devnet-1": 404}, False, 0),
            *[
                ({"first/devnet-1": 200, "first/devnet-2": status, "second/devnet-1": 200}, False, 0)
                for status in (401, 403, 429, 500, 503, 0)
            ],
        ]
        for responses, succeeds, expected_files in cases:
            with self.subTest(responses=responses), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                tools = root / "bin"
                tools.mkdir()
                self.tool(tools / "gh", """
import sys
endpoint = sys.argv[2]
if "/commits/" in endpoint:
    print("pinned-sha")
else:
    assert endpoint.endswith("?ref=pinned-sha"), endpoint
    print("devnet-1\\ndevnet-2" if "/first/" in endpoint else "devnet-1")
""")
                self.tool(tools / "curl", """
import json, os, pathlib, sys
args = sys.argv[1:]
url = args[-1]
assert url.endswith("?ref=pinned-sha"), url
repo = url.split("/ethpandaops/")[1].split("/")[0]
devnet = url.split("/network-configs/")[1].split("/")[0]
status = json.loads(os.environ["RESPONSES"])[repo + "/" + devnet]
pathlib.Path(args[args.index("--output") + 1]).write_text("{}")
print(status, end="")
sys.exit(7 if status == 0 else 0)
""")
                env_file = root / "env"
                result = subprocess.run(
                    ["bash", str(SCRIPT)],
                    env=dict(os.environ, PATH=f"{tools}:{os.environ['PATH']}", RUNNER_TEMP=temp,
                             GITHUB_ENV=str(env_file), GH_TOKEN="test-token", REPOS="first second",
                             DEVNETS_PER_REPO="2", RESPONSES=json.dumps(responses)),
                    capture_output=True, text=True, timeout=10)
                self.assertEqual(result.returncode == 0, succeeds, result.stdout + result.stderr)
                self.assertEqual(env_file.exists(), succeeds)
                if succeeds:
                    directory = Path(env_file.read_text().strip().split("=", 1)[1])
                    self.assertEqual(len(list(directory.glob("*.json"))), expected_files)
                self.assertNotIn("test-token", result.stdout + result.stderr)

    @staticmethod
    def tool(path, source):
        path.write_text("#!/usr/bin/env python3\n" + source)
        path.chmod(0o755)


if __name__ == "__main__":
    unittest.main()
