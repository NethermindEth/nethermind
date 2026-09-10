# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(sys.platform == "linux", "watchdog uses Linux process groups")
class WatchdogTests(unittest.TestCase):
    def test_hung_diagnostics_are_bounded_and_build_is_killed(self):
        for stalled_tool in ("dotnet", "dotnet-stack"):
            with self.subTest(tool=stalled_tool), tempfile.TemporaryDirectory() as directory:
                work = Path(directory)
                tools = work / "tools"
                tools.mkdir()
                diag = work / "diagnostics"
                scheduler = work / "scheduler"
                scheduler.mkdir()
                (scheduler / "SchedulerState.txt").write_text("scheduler evidence")
                for tool in ("dotnet", "dotnet-stack", "pgrep", "setsid"):
                    command = {
                        "dotnet": "sleep 60" if stalled_tool == "dotnet" else "exit 0",
                        "dotnet-stack": "sleep 60" if stalled_tool == "dotnet-stack" else "exit 0",
                        "pgrep": 'cat "$DIAG_DIR/command.pid"',
                        # Exercise the supervisor PID differing from the build's session leader.
                        "setsid": f'exec {shutil.which("setsid")} --fork "$@"',
                    }[tool]
                    path = tools / tool
                    path.write_text("#!/bin/bash\n" + command + "\n")
                    path.chmod(0o755)
                env = dict(os.environ, PATH=f"{tools}:{os.environ['PATH']}",
                           DIAG_DIR=str(diag), MSBUILDDEBUGPATH=str(scheduler),
                           STALL_SECONDS="1", POLL_SECONDS="1", DIAGNOSTIC_SECONDS="3",
                           INSTALL_TIMEOUT_SECONDS="1", STACK_TIMEOUT_SECONDS="1")
                result = subprocess.run(["bash", str(ROOT / "scripts/ci/run-with-stall-watchdog.sh"),
                                         "sleep", "60"], env=env, capture_output=True, text=True, timeout=15)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertEqual((diag / "msbuild-debug/SchedulerState.txt").read_text(), "scheduler evidence")
                pid = int((diag / "command.pid").read_text())
                with self.assertRaises(ProcessLookupError):
                    os.kill(pid, 0)


@unittest.skipUnless(shutil.which("dotnet"), ".NET SDK required")
class GeneratorTargetTests(unittest.TestCase):
    def test_missing_or_ambiguous_generator_has_actionable_error(self):
        for count in (0, 1, 2):
            with self.subTest(count=count), tempfile.TemporaryDirectory() as directory:
                result = self.run_target(Path(directory), count, False)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("RPC JSON context generator", result.stdout)
                self.assertNotIn("MSB3073", result.stdout)

    def test_design_time_build_does_not_launch_generator(self):
        with tempfile.TemporaryDirectory() as directory:
            result = self.run_target(Path(directory), 0, True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    @staticmethod
    def run_target(work, count, design_time):
        project = ET.Element("Project")
        props = ET.SubElement(project, "PropertyGroup")
        ET.SubElement(props, "GenerateRpcJsonContext").text = "true"
        ET.SubElement(props, "DesignTimeBuild").text = str(design_time).lower()
        ET.SubElement(project, "Import", Project=str(ROOT / "src/Nethermind/Directory.Build.targets"))
        ET.SubElement(project, "Target", Name="ResolveReferences")
        items = ET.SubElement(project, "ItemGroup")
        for index in range(count):
            ET.SubElement(items, "_RpcJsonContextGeneratorTool", Include=str(work / f"missing-{index}.dll"))
        path = work / "test.proj"
        ET.ElementTree(project).write(path)
        return subprocess.run(["dotnet", "msbuild", str(path), "-nologo", "-t:_GenerateRpcJsonSerializerContext"],
                              cwd=work, capture_output=True, text=True, timeout=30)


if __name__ == "__main__":
    unittest.main()
