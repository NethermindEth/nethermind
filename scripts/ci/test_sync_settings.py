#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Exercise the fast sync settings update's retries without network or delays."""

import contextlib
import io
import json
import runpy
import sys
import tempfile
import types
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import call, patch

SCRIPT = Path(__file__).resolve().parents[1] / "sync-settings.py"
ORIGINAL = '{"Sync":{"PivotNumber":1,"PivotHash":"old"}}'


def stub_modules():
    # The CI job installs neither dependency; the script only needs these names from them.
    requests = types.ModuleType("requests")
    requests.RequestException = type("RequestException", (OSError,), {})
    requests.get = requests.post = None
    emoji = types.ModuleType("emoji")
    emoji.emojize = lambda text: text
    return {"requests": requests, "emoji": emoji}


class SyncSettingsTests(unittest.TestCase):
    def run_script(self, root, failing_url, failures):
        failed_requests = 0

        def response(url, **kwargs):
            nonlocal failed_requests
            if url == failing_url and failed_requests < failures:
                failed_requests += 1
                return SimpleNamespace(text="not JSON")
            method = kwargs["params"]["action"] if "params" in kwargs else json.loads(kwargs["data"])["method"]
            result = "0x10000" if method == "eth_blockNumber" else {"hash": "0x" + "ab" * 32, "totalDifficulty": "0x0"}
            return SimpleNamespace(text=json.dumps({"result": result}))

        modules = stub_modules()
        modules["requests"].get = modules["requests"].post = response
        with contextlib.chdir(root), contextlib.redirect_stdout(io.StringIO()), \
                patch.dict(sys.modules, modules), \
                patch("sys.argv", [str(SCRIPT)]), \
                patch("time.sleep") as sleep:
            try:
                runpy.run_path(str(SCRIPT), run_name="__main__")
                exit_message = None
            except SystemExit as e:
                exit_message = str(e)
        return failed_requests, sleep.call_args_list, exit_message

    def make_configs(self, root):
        with patch.dict(sys.modules, stub_modules()):
            definitions = runpy.run_path(str(SCRIPT))
        names = set(definitions["configs"])
        for followers in definitions["PIVOT_FOLLOWERS"].values():
            names.update(followers)
        configs = root / "src/Nethermind/Nethermind.Runner/configs"
        configs.mkdir(parents=True)
        for name in names:
            (configs / f"{name}.json").write_text(ORIGINAL)
        return definitions, configs

    def test_retries_and_continues_after_a_failed_chain(self):
        for failures in (1, 5):
            with self.subTest(failures=failures), tempfile.TemporaryDirectory() as tmp:
                definitions, configs = self.make_configs(Path(tmp))

                failed_requests, sleeps, exit_message = self.run_script(Path(tmp), definitions["configs"]["gnosis"]["url"], failures)

                self.assertEqual(failed_requests, failures)
                self.assertEqual(sleeps, [call(n) for n in ([10] if failures == 1 else [10, 20, 30, 40])])
                gnosis = configs / "gnosis.json"
                if failures == 5:
                    self.assertEqual(exit_message, "Failed to update: gnosis")
                    self.assertEqual(gnosis.read_text(), ORIGINAL)
                else:
                    self.assertIsNone(exit_message)
                    self.assertEqual(json.loads(gnosis.read_text())["Sync"]["PivotNumber"], 50000)
                # Chiado comes after gnosis, so it shows the run keeps going once a chain is given up.
                self.assertEqual(json.loads((configs / "chiado.json").read_text())["Sync"]["PivotNumber"], 50000)

    def test_broken_follower_leaves_its_leader_unchanged(self):
        with tempfile.TemporaryDirectory() as tmp:
            definitions, configs = self.make_configs(Path(tmp))
            (configs / "mainnet_aztec.json").write_text("{}")

            with self.assertRaises(KeyError):
                self.run_script(Path(tmp), failing_url=None, failures=0)

            self.assertEqual((configs / "mainnet.json").read_text(), ORIGINAL)


if __name__ == "__main__":
    unittest.main()
