#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import hashlib
import os
from pathlib import Path
import stat
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("run-rpc-sweep.sh")
START_NODE = Path(__file__).with_name("start-node.sh")
LIB = Path(__file__).with_name("lib.sh")


class RpcSweepTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "sweep harness tests require POSIX bash")
    def test_rejects_malformed_flags_before_any_node_starts(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            environment = {
                "OUT_DIR": str(root / "out"),
                "STATE_ROOT": str(root / "state"),
                "SCRATCH_ROOT": str(root / "scratch"),
                "NM_IMAGE": "nethermind:test",
                "SNAPSHOT_BLOCK": "1",
                "SNAPSHOT_ROOT": str(root / "snapshots"),
                "JB_REF": "test",
                "JB_BENCHMARK_CONFIG": "config.yaml",
                "CLIENTS": "nethermind+--JsonRpc.EnabledModules=Eth;;Debug",
                "RPS_LIST": "",
            }
            result = subprocess.run(
                ["bash", str(SCRIPT)], env={**os.environ, **environment},
                text=True, capture_output=True, check=False,
            )

        self.assertEqual(result.returncode, 1)
        self.assertIn("malformed flag list", result.stdout)
        self.assertNotIn("Starting", result.stdout)

    @unittest.skipIf(os.name == "nt", "sweep harness tests require POSIX bash")
    def test_preserves_comma_values_and_hashes_the_complete_flag_specification(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            runner = root / "runner"
            runner.mkdir()
            (runner / "lib.sh").write_bytes(LIB.read_bytes())
            (runner / "run-rpc-sweep.sh").write_bytes(SCRIPT.read_bytes())
            (runner / "stop-node.sh").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
            (runner / "start-node.sh").write_text(
                "#!/usr/bin/env bash\n"
                "printf '%s|%s|%s\\n' \"$(basename \"$STATE_DIR\")\" \"$ADDITIONAL_FLAGS\" \"$ARM_SCRATCH_DIR\" >> \"$CAPTURE\"\n"
                "exit 0\n",
                encoding="utf-8",
            )
            fake_bin = root / "bin"
            fake_bin.mkdir()
            (fake_bin / "docker").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
            for path in (
                runner / "run-rpc-sweep.sh", runner / "start-node.sh",
                runner / "stop-node.sh", fake_bin / "docker",
            ):
                path.chmod(path.stat().st_mode | stat.S_IXUSR)

            scratch = root / "scratch"
            scratch.mkdir()
            snapshots = root / "snapshots"
            (snapshots / "nethermind-flat-1").mkdir(parents=True)
            capture = root / "capture.txt"
            first_flags = "--JsonRpc.EnabledModules=Eth,Debug;--LongFlagPrefixThatIsSharedByBothArms=one;--Cache.Path={ARM_SCRATCH}"
            second_flags = "--JsonRpc.EnabledModules=Eth,Debug;--LongFlagPrefixThatIsSharedByBothArms=two;--Cache.Path={ARM_SCRATCH}"
            environment = {
                "PATH": f"{fake_bin}{os.pathsep}{os.environ['PATH']}",
                "OUT_DIR": str(root / "out"),
                "STATE_ROOT": str(root / "state"),
                "SCRATCH_ROOT": str(scratch),
                "NM_IMAGE": "nethermind:test",
                "SNAPSHOT_BLOCK": "1",
                "SNAPSHOT_ROOT": str(snapshots),
                "JB_REF": "test",
                "JB_BENCHMARK_CONFIG": "config.yaml",
                "CLIENTS": f"nethermind@repo:image+{first_flags} nethermind@repo:image+{second_flags}",
                "RPS_LIST": "",
                "CAPTURE": str(capture),
                "JB_ETH_CALL_CORPUS": "false",
            }
            result = subprocess.run(
                ["bash", str(runner / "run-rpc-sweep.sh")],
                env={**os.environ, **environment}, text=True, capture_output=True, check=False,
            )

            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            rows = capture.read_text(encoding="utf-8").splitlines()
            self.assertEqual(len(rows), 2)
            labels = [row.split("|", 1)[0] for row in rows]
            self.assertNotEqual(labels[0], labels[1])
            self.assertNotIn("_r2", " ".join(labels))
            self.assertIn(hashlib.sha256(first_flags.encode()).hexdigest()[:12], labels[0])
            self.assertIn(hashlib.sha256(second_flags.encode()).hexdigest()[:12], labels[1])
            self.assertIn("--JsonRpc.EnabledModules=Eth,Debug", rows[0])
            for row, label in zip(rows, labels):
                _, captured_flags, captured_scratch = row.split("|", 2)
                self.assertNotIn("{ARM_SCRATCH}", captured_flags)
                self.assertIn(f"--Cache.Path={captured_scratch}", captured_flags)
                self.assertEqual(captured_scratch, str(scratch / "arm" / label))
                self.assertTrue((scratch / "arm" / label).is_dir())

    def test_per_arm_scratch_is_wired_as_an_identical_path_bind_mount(self):
        sweep = SCRIPT.read_text(encoding="utf-8")
        start = START_NODE.read_text(encoding="utf-8")
        self.assertIn('ARM_SCRATCH_DIR="$arm_scratch_dir"', sweep)
        self.assertIn(
            'docker_args+=(--mount "type=bind,source=$ARM_SCRATCH_DIR,target=$ARM_SCRATCH_DIR")',
            start,
        )
        self.assertIn("direct mode does not refresh the fingerprint anchor", start)
        self.assertIn("as_root rm -rf -- \"$arm_scratch_dir\"", sweep)
        self.assertIn("as_root mkdir -p -- \"$arm_scratch_dir\"", sweep)

    def test_docs_describe_separator_and_direct_sweep_limitations(self):
        readme = Path(__file__).with_name("README.md").read_text(encoding="utf-8")
        workflow = Path(__file__).parents[2] / ".github" / "workflows" / "run-rpc-benchmarks.yml"
        workflow_text = workflow.read_text(encoding="utf-8")
        self.assertIn("semicolon\nis the flag separator", readme)
        self.assertIn("order-dependent", readme)
        self.assertIn("not a clean A/B", readme)
        self.assertIn("direct reth", readme)
        self.assertIn("does not refresh the cross-run fingerprint anchor", readme)
        self.assertIn("old anchor exists", readme)
        self.assertIn("+flag;flag", workflow_text)
        self.assertIn("commas stay", workflow_text)
        self.assertIn("does not refresh the fingerprint anchor", workflow_text)
        self.assertIn("any old anchor is diagnostic only", workflow_text)


if __name__ == "__main__":
    unittest.main()
