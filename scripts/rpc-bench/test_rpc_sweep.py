#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

import hashlib
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).parent))
# The workflow resolver harness; imported as a module so its test classes are not collected twice.
import test_workflow_contracts as workflow_contracts  # noqa: E402


SCRIPT = Path(__file__).with_name("run-rpc-sweep.sh")
START_NODE = Path(__file__).with_name("start-node.sh")
LIB = Path(__file__).with_name("lib.sh")
POSIX_ONLY = unittest.skipIf(os.name == "nt", "sweep harness tests require POSIX bash")


def flag_label(flag_spec):
    """The label suffix run-rpc-sweep.sh derives from a `+` flag list: a readable prefix plus a hash of the whole list."""
    prefix = re.sub(r"[^a-zA-Z0-9]", "_", flag_spec)[:24]
    return f"{prefix}_{hashlib.sha256(flag_spec.encode()).hexdigest()[:12]}"


class FakeRunner:
    """The sweep with start-node.sh replaced by a recorder, so arms "start" without Docker or a snapshot."""

    def __init__(self, root: Path):
        self.root = root
        self.scratch = root / "scratch"
        self.snapshots = root / "snapshots"
        self.capture = root / "capture.txt"
        runner = root / "runner"
        runner.mkdir()
        (runner / "lib.sh").write_bytes(LIB.read_bytes())
        (runner / "run-rpc-sweep.sh").write_bytes(SCRIPT.read_bytes())
        (runner / "stop-node.sh").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
        (runner / "start-node.sh").write_text(
            "#!/usr/bin/env bash\n"
            "printf '%s|%s|%s|%s\\n' \"$(basename \"$STATE_DIR\")\" \"$ADDITIONAL_FLAGS\" \"$ARM_SCRATCH_DIR\" "
            "\"$NODE_ENV_VARS\" >> \"$CAPTURE\"\n"
            "exit 0\n",
            encoding="utf-8",
        )
        fake_bin = root / "bin"
        fake_bin.mkdir()
        (fake_bin / "docker").write_text("#!/usr/bin/env bash\nexit 0\n", encoding="utf-8")
        (fake_bin / "sudo").write_text(
            "#!/usr/bin/env bash\n"
            "if [[ \"$1\" == mkdir && \"${!#}\" == \"$SUDO_ROOT\" ]]; then\n"
            "  echo \"unexpected privileged SCRATCH_ROOT creation\" >&2\n"
            "  exit 1\n"
            "fi\n"
            "exec \"$@\"\n",
            encoding="utf-8",
        )
        for path in (runner / "run-rpc-sweep.sh", runner / "start-node.sh", runner / "stop-node.sh",
                     fake_bin / "docker", fake_bin / "sudo"):
            path.chmod(path.stat().st_mode | stat.S_IXUSR)
        self.script = runner / "run-rpc-sweep.sh"
        self.path = f"{fake_bin}{os.pathsep}{os.environ['PATH']}"

    def snapshot(self, name):
        (self.snapshots / name).mkdir(parents=True, exist_ok=True)
        return self.snapshots / name

    def run(self, clients, **overrides):
        environment = {
            "PATH": self.path,
            "OUT_DIR": str(self.root / "out"),
            "STATE_ROOT": str(self.root / "state"),
            "SCRATCH_ROOT": str(self.scratch),
            "NM_IMAGE": "nethermind:test",
            "SNAPSHOT_BLOCK": "1",
            "SNAPSHOT_ROOT": str(self.snapshots),
            "JB_BENCHMARK_CONFIG": "config.yaml",
            "CLIENTS": clients,
            "RPS_LIST": "",
            "JB_ETH_CALL_CORPUS": "false",
            "NODE_ENV_VARS": "",
            "GITHUB_STEP_SUMMARY": str(self.root / "summary.md"),
            "CAPTURE": str(self.capture),
            "SUDO_ROOT": str(self.scratch),
        }
        environment.update(overrides)
        return subprocess.run(["bash", str(self.script)], env={**os.environ, **environment},
                              text=True, capture_output=True, check=False)

    def rows(self):
        """One (label, flags, arm scratch, env) tuple per arm start-node.sh was asked to start."""
        return [tuple(row.split("|", 3)) for row in self.capture.read_text(encoding="utf-8").splitlines()]


class RpcSweepTests(unittest.TestCase):
    @POSIX_ONLY
    def test_rejects_malformed_entries_before_any_node_starts(self):
        for clients, expected in (
            ("nethermind+--JsonRpc.EnabledModules=Eth;;Debug", "malformed flag list"),
            ("nethermind+", "empty arm flag list"),
            ("nethermind+JsonRpc.Enabled=true", "malformed arm flag"),
            # An env suffix written after the flags would otherwise become part of the last flag's value.
            ("nethermind@repo:image+--Pruning.Mode=None#NETHERMIND_FOO=1", "malformed arm flag"),
            ("nethermind@", "has an empty image"),
            ("nethermind@#NETHERMIND_FOO=1", "has an empty image"),
            ("@repo:image", "malformed sweep client entry"),
            ("nethermind\nreth", "single line"),
        ):
            with self.subTest(clients=clients), tempfile.TemporaryDirectory() as directory:
                runner = FakeRunner(Path(directory))
                runner.snapshot("nethermind-flat-1")
                result = runner.run(clients)

                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn(expected, result.stdout)
                self.assertFalse(runner.capture.exists(), "a node started before the entries were validated")

    @POSIX_ONLY
    def test_flags_and_env_reach_only_their_own_arm(self):
        with tempfile.TemporaryDirectory() as directory:
            runner = FakeRunner(Path(directory))
            runner.snapshot("nethermind-flat-1")
            first_flags = "--JsonRpc.EnabledModules=Eth,Debug;--LongFlagPrefixThatIsSharedByBothArms=one;--Cache.Path={ARM_SCRATCH}"
            second_flags = "--JsonRpc.EnabledModules=Eth,Debug;--LongFlagPrefixThatIsSharedByBothArms=two;--Cache.Path={ARM_SCRATCH}"
            result = runner.run(f"nethermind@repo:image#NETHERMIND_FOOCONFIG_BAR=1+{first_flags} "
                                f"nethermind@repo:image+{second_flags}")

            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            rows = runner.rows()
            self.assertEqual(len(rows), 2)
            labels = [row[0] for row in rows]
            # The env suffix and the flag list are both folded into the label, env first, as in the entry.
            self.assertEqual(labels[0], f"nethermind_image_BAR_1_{flag_label(first_flags)}")
            self.assertEqual(labels[1], f"nethermind_image_{flag_label(second_flags)}")
            self.assertNotIn("_r2", " ".join(labels))
            # The env suffix is docker -e for its own arm only; flags never carry it.
            self.assertEqual(rows[0][3].split(), ["NETHERMIND_FOOCONFIG_BAR=1"])
            self.assertEqual(rows[1][3].split(), [])
            self.assertIn("--JsonRpc.EnabledModules=Eth,Debug", rows[0][1])
            for label, flags, scratch, _ in rows:
                self.assertNotIn("#", flags)
                self.assertNotIn("{ARM_SCRATCH}", flags)
                self.assertIn(f"--Cache.Path={scratch}", flags)
                self.assertEqual(scratch, str(runner.scratch / "arm" / label))
                self.assertTrue((runner.scratch / "arm" / label).is_dir())
                self.assertEqual(stat.S_IMODE((runner.scratch / "arm" / label).stat().st_mode), 0o777)

    @POSIX_ONLY
    def test_arm_scratch_is_wiped_for_every_arm_and_round(self):
        with tempfile.TemporaryDirectory() as directory:
            runner = FakeRunner(Path(directory))
            runner.snapshot("nethermind-flat-1")
            flags = "--Cache.Path={ARM_SCRATCH}"
            label = f"nethermind_{flag_label(flags)}"
            # Leftovers of an earlier dispatch, for both rounds' arms, and a sibling the wipe must not reach.
            for stale in (runner.scratch / "arm" / label, runner.scratch / "arm" / f"{label}_r2"):
                stale.mkdir(parents=True)
                (stale / "stale").write_text("previous arm", encoding="utf-8")
            (runner.scratch / "keep").write_text("not per-arm", encoding="utf-8")

            result = runner.run(f"nethermind+{flags}", ROUNDS="2")

            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual([row[0] for row in runner.rows()], [label, f"{label}_r2"])
            for arm in (label, f"{label}_r2"):
                self.assertTrue((runner.scratch / "arm" / arm).is_dir())
                self.assertEqual(list((runner.scratch / "arm" / arm).iterdir()), [])
            self.assertTrue((runner.scratch / "keep").exists())

    @POSIX_ONLY
    def test_scratch_root_must_stay_disjoint_from_every_snapshot(self):
        cases = (
            ("nethermind", lambda r: r.snapshots / "nethermind-flat-1" / "scratch", "must not be inside the nethermind snapshot"),
            ("nethermind", lambda r: r.snapshots, "must not contain the nethermind snapshot"),
            ("nethermind", lambda r: r.snapshots / "nethermind-flat-1", "must not equal the nethermind snapshot"),
            # Every requested type's set is checked, not only the first.
            ("nethermind reth", lambda r: r.snapshots / "reth-1" / "scratch", "must not be inside the reth snapshot"),
            # Compared on canonical paths: a symlink cannot smuggle scratch into a snapshot.
            ("nethermind", lambda r: r.root / "link" / "scratch", "must not be inside the nethermind snapshot"),
        )
        for clients, scratch, expected in cases:
            with self.subTest(clients=clients, expected=expected), tempfile.TemporaryDirectory() as directory:
                runner = FakeRunner(Path(directory))
                runner.snapshot("nethermind-flat-1")
                runner.snapshot("reth-1")
                (runner.root / "link").symlink_to(runner.snapshots / "nethermind-flat-1")
                scratch_root = scratch(runner)
                result = runner.run(clients, SCRATCH_ROOT=str(scratch_root), SUDO_ROOT=str(scratch_root))

                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertIn(expected, result.stdout)
                self.assertFalse(runner.capture.exists())

    @POSIX_ONLY
    def test_arm_image_strips_every_per_arm_option(self):
        def arm_image(entry):
            return subprocess.run(["bash", "-c", 'source "$1"; arm_image "$2"', "bash", str(LIB), entry],
                                  capture_output=True, text=True, check=True).stdout.strip()

        # The ARM runner's reclaim keep-list and the sweep both take the image from here.
        self.assertEqual(arm_image("nethermind@repo/nm:pr+--Foo=a@b"), "repo/nm:pr")
        self.assertEqual(arm_image("nethermind@repo/nm:pr#NETHERMIND_FOO=1+--Bar=x,y"), "repo/nm:pr")
        self.assertEqual(arm_image("reth@ghcr.io/paradigmxyz/reth:v2.2.0+--rpc.max-connections=10"),
                         "ghcr.io/paradigmxyz/reth:v2.2.0")
        self.assertEqual(arm_image("nethermind+--Network.StaticPeers=enode://x@1.2.3.4:30303"), "")

    @unittest.skipUnless(workflow_contracts.BASH, "no usable bash to execute the workflow resolver")
    def test_the_resolver_takes_the_bare_image_from_arms_with_options(self):
        clients = "nethermind@repo/nm:a#NETHERMIND_FOOCONFIG_BAR=1+--Foo=1 nethermind@repo/nm:a+--Foo=2"
        harness = workflow_contracts.ResolverExecutionTests("test_jsonbench_sweep_accepts_complete_user_clients_with_cache_sentinel")
        result, output = harness.run_resolver(json.dumps({"clients": clients}))

        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        outputs = workflow_contracts.github_outputs(output)
        self.assertEqual(outputs["image_ref"], "repo/nm:a")
        self.assertEqual(json.loads(outputs["tool_config"])["clients"], clients)

    def test_per_arm_scratch_is_wired_as_an_identical_path_bind_mount(self):
        sweep = SCRIPT.read_text(encoding="utf-8")
        start = START_NODE.read_text(encoding="utf-8")
        cleanup = Path(__file__).with_name("cleanup.sh").read_text(encoding="utf-8")
        self.assertRegex(sweep, r'ADDITIONAL_FLAGS="\$arm_flags" ARM_SCRATCH_DIR="\$arm_scratch_dir"')
        self.assertRegex(
            start,
            r'docker_args\+=\(--mount "type=bind,source=\$ARM_SCRATCH_DIR,target=\$ARM_SCRATCH_DIR"\)',
        )
        self.assertRegex(start, r'(?m)^\s*echo "::warning::direct mode does not refresh the fingerprint anchor;')
        self.assertRegex(cleanup, r'(?m)^for sub in .*\barm\b')

        # Flags reach docker as an array, so a value cannot undergo pathname expansion on the way.
        self.assertNotIn("node_args+=($ADDITIONAL_FLAGS)", start)
        self.assertRegex(start, r'while IFS= read -r additional_flags_line .*?\n\s+line_args=\(\)')
        self.assertIn('additional_args+=("${line_args[@]}")', start)

    def test_docs_describe_the_entry_grammar_and_direct_sweep_limits(self):
        readme = " ".join(Path(__file__).with_name("README.md").read_text(encoding="utf-8").split())
        self.assertIn("client[@image][#K=V[,K=V]][+flag[;flag]]", readme)
        self.assertIn("ctype[@image][#K=V[,K=V]][+flag[;flag]]", readme)
        self.assertIn("semicolon is the flag separator", readme)
        self.assertIn("{ARM_SCRATCH}", readme)
        self.assertIn("order-dependent", readme)
        self.assertIn("not a clean A/B", readme)
        self.assertIn("direct reth path does not refresh the cross-run fingerprint anchor", readme)
        self.assertIn("old anchor exists", readme)


if __name__ == "__main__":
    unittest.main()
