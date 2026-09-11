# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(sys.platform == "linux", "watchdog uses Linux process groups")
class WatchdogTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.work = Path(directory.name)
        self.tools = self.work / "tools"
        self.tools.mkdir()
        self.diag = self.work / "diagnostics"
        self.scheduler = self.work / "scheduler"
        self.scheduler.mkdir()
        self.env = dict(os.environ, PATH=f"{self.tools}:{os.environ['PATH']}",
                        DIAG_DIR=str(self.diag), MSBUILDDEBUGPATH=str(self.scheduler),
                        STALL_SECONDS="1", POLL_SECONDS="1", DIAGNOSTIC_SECONDS="3",
                        INSTALL_TIMEOUT_SECONDS="1", STACK_TIMEOUT_SECONDS="1", TERMINATION_SECONDS="2")
        for tool in ("dotnet", "dotnet-stack", "pgrep"):
            self.tool(tool, "exit 0")
        # Exercise the supervisor PID differing from the build's session leader.
        self.tool("setsid", f'exec {shutil.which("setsid")} --fork "$@"')

    def tool(self, name, command):
        path = self.tools / name
        path.write_text("#!/bin/bash\n" + command + "\n")
        path.chmod(0o755)

    def run_watchdog(self, *command, **env):
        try:
            return subprocess.run(["bash", str(ROOT / "scripts/ci/run-with-stall-watchdog.sh"), *command],
                                  env=self.env | env, capture_output=True, text=True, timeout=20)
        finally:
            # Failed assertions/timeouts must not leave our synthetic build tree running.
            for name in ("command.pid", "fallback.pid"):
                path = self.diag / name
                if path.exists():
                    self.addCleanup(self.kill_group, int(path.read_text()))

    @staticmethod
    def kill_group(pid):
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass

    def test_hung_diagnostics_are_bounded_and_build_is_killed(self):
        for stalled_tool in ("dotnet", "dotnet-stack"):
            with self.subTest(tool=stalled_tool):
                (self.scheduler / "SchedulerState.txt").write_text("scheduler evidence")
                for tool in ("dotnet", "dotnet-stack"):
                    self.tool(tool, "sleep 60" if stalled_tool == tool else "exit 0")
                self.tool("pgrep", 'cat "$DIAG_DIR/command.pid"')
                result = self.run_watchdog("sleep", "60")
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertEqual((self.diag / "msbuild-debug/SchedulerState.txt").read_text(), "scheduler evidence")
                pid = int((self.diag / "command.pid").read_text())
                with self.assertRaises(ProcessLookupError):
                    os.kill(pid, 0)

    def test_output_resets_silence_and_healthy_command_completes(self):
        result = self.run_watchdog("bash", "-c", "for i in {1..6}; do echo tick; sleep 2; done",
                                   STALL_SECONDS="3")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.diag / "command.log").read_text().count("tick"), 6)
        self.assertFalse((self.diag / "system.txt").exists())

    def test_command_exit_code_is_preserved(self):
        for status in (0, 7):
            with self.subTest(status=status):
                result = self.run_watchdog("bash", "-c", f"exit {status}")
                self.assertEqual(result.returncode, status, result.stdout + result.stderr)
                self.assertFalse((self.diag / "system.txt").exists())

    def test_aggregate_deadline_stops_capturing_more_processes(self):
        self.tool("pgrep", "seq 8")
        self.tool("dotnet-stack", 'echo capture >> "$DIAG_DIR/captures"; sleep 60')
        result = self.run_watchdog("sleep", "60")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        captures = (self.diag / "captures").read_text().splitlines()
        self.assertGreater(len(captures), 0)
        self.assertLessEqual(len(captures), 3)
        self.assertEqual(list((self.diag / "msbuild-debug").iterdir()), [])
        self.assertNotIn("cannot open", result.stderr)

    def test_installation_is_clamped_to_aggregate_deadline(self):
        self.tool("dotnet", "sleep 60")
        self.tool("pgrep", "seq 8")
        result = self.run_watchdog("sleep", "60", INSTALL_TIMEOUT_SECONDS="60", DIAGNOSTIC_SECONDS="1")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertEqual(list(self.diag.glob("stack-*.txt")), [])

    def test_missing_pid_file_uses_command_pid(self):
        self.tool("setsid", f'exec {shutil.which("setsid")} "$@"')
        result = self.run_watchdog("bash", "-c",
                                   'mv "$DIAG_DIR/command.pid" "$DIAG_DIR/fallback.pid"; exec sleep 60')
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertNotIn("command.pid", result.stderr)
        with self.assertRaises(ProcessLookupError):
            os.kill(int((self.diag / "fallback.pid").read_text()), 0)

    def test_child_ignoring_sigterm_is_killed_after_parent_exits(self):
        child = (
            "import os,signal,time; from pathlib import Path; "
            "signal.signal(signal.SIGTERM, signal.SIG_IGN); "
            "Path(os.environ['DIAG_DIR'], 'child.pid').write_text(str(os.getpid())); time.sleep(60)"
        )
        parent = f"import subprocess,sys,time; subprocess.Popen([sys.executable, '-c', {child!r}]); time.sleep(60)"
        result = self.run_watchdog(sys.executable, "-c", parent)
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        pid = int((self.diag / "child.pid").read_text())
        status = Path(f"/proc/{pid}/stat")
        # An orphaned zombie is dead but can remain until the container's init reaps it.
        self.assertTrue(not status.exists() or status.read_text().split()[2] == "Z", "child survived cleanup")


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

    def test_existing_generator_executes_once_with_full_path(self):
        with tempfile.TemporaryDirectory(prefix="generator target ") as directory:
            work = Path(directory)
            result = self.run_target(work, 1, False, existing=True)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            calls = (work / "calls.jsonl").read_text().splitlines()
            self.assertEqual(len(calls), 1)
            args = json.loads(calls[0])
            self.assertEqual(Path(args[0]), work / "generator 0.py")
            self.assertIn("--output", args)
            self.assertIn("--references", args)

    @staticmethod
    def run_target(work, count, design_time, existing=False):
        project = ET.Element("Project")
        props = ET.SubElement(project, "PropertyGroup")
        ET.SubElement(props, "GenerateRpcJsonContext").text = "true"
        ET.SubElement(props, "DesignTimeBuild").text = str(design_time).lower()
        if existing:
            ET.SubElement(props, "_DotnetHost").text = sys.executable
        ET.SubElement(project, "Import", Project=str(ROOT / "src/Nethermind/Directory.Build.targets"))
        ET.SubElement(project, "Target", Name="ResolveReferences")
        items = ET.SubElement(project, "ItemGroup")
        for index in range(count):
            name = f"generator {index}.py" if existing else f"missing-{index}.dll"
            if existing:
                (work / name).write_text(
                    "import json,sys\nfrom pathlib import Path\n"
                    "with Path(__file__).with_name('calls.jsonl').open('a') as out:\n"
                    "    out.write(json.dumps(sys.argv) + '\\n')\n"
                )
            ET.SubElement(items, "_RpcJsonContextGeneratorTool", Include=name)
        path = work / "test.proj"
        ET.ElementTree(project).write(path)
        return subprocess.run(["dotnet", "msbuild", str(path), "-nologo", "-t:_GenerateRpcJsonSerializerContext"],
                              cwd=work, capture_output=True, text=True, timeout=30)


if __name__ == "__main__":
    unittest.main()
