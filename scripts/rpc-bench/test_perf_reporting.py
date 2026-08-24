#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only

"""Regression coverage for folded perf profiles and their reporting contract."""

import os
import re
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

import yaml


ROOT = Path(__file__).resolve().parents[2]
PERF_FOLD = ROOT / "scripts" / "perf-fold.awk"
PERF_REPORT = ROOT / "scripts" / "perf-report.sh"
FOLDED_PROFILE_VALIDATOR = ROOT / "scripts" / "validate-folded-profile.sh"
EXPB_WORKFLOW = ROOT / ".github" / "workflows" / "run-expb-reproducible-benchmarks.yml"
RPC_WORKFLOW = ROOT / ".github" / "workflows" / "run-rpc-benchmarks.yml"
RPC_LIB = ROOT / "scripts" / "rpc-bench" / "lib.sh"
START_NODE = ROOT / "scripts" / "rpc-bench" / "start-node.sh"
STOP_NODE = ROOT / "scripts" / "rpc-bench" / "stop-node.sh"


def find_bash() -> str | None:
    if os.name == "nt":
        git_bash = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "bin" / "bash.exe"
        if git_bash.is_file():
            return str(git_bash)
    return shutil.which("bash")


BASH = find_bash()


@unittest.skipUnless(BASH, "bash is required for perf script tests")
class PerfReportingTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temporary_directory = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary_directory.name)

    def tearDown(self) -> None:
        self.temporary_directory.cleanup()

    def write_folded(self, name: str, contents: str) -> Path:
        path = self.directory / name
        path.write_text(contents, encoding="utf-8")
        return path

    def run_report(self, *args: str, locale: str | None = None) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        if locale is not None:
            environment["LC_ALL"] = locale
        return subprocess.run(
            [BASH, str(PERF_REPORT), *args],
            cwd=ROOT,
            check=False,
            text=True,
            capture_output=True,
            env=environment,
        )

    def run_fold(self, perf_script: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, "-c", 'awk -f "$1"', "bash", str(PERF_FOLD)],
            cwd=ROOT,
            check=False,
            text=True,
            input=perf_script,
            capture_output=True,
        )

    def run_folded_profile_validator(self, profile: Path) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, str(FOLDED_PROFILE_VALIDATOR), str(profile)],
            cwd=ROOT,
            check=False,
            text=True,
            capture_output=True,
        )

    def run_rpc_library(self, script: str, environment: dict[str, str]) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            [BASH, "-c", f'source "$1"; {script}', "bash", str(RPC_LIB)],
            cwd=ROOT,
            check=False,
            text=True,
            capture_output=True,
            env=environment,
        )

    @staticmethod
    def data_rows(output: str) -> list[str]:
        return [
            line for line in output.splitlines()
            if re.search(r"\s\d+\.\d+%\s+\d+\.\d+%\s+[+-]\d+\.\d+$", line)
        ]

    def test_folding_preserves_unknown_dsos_and_generic_managed_frames(self) -> None:
        perf_script = (
            ".NET 100/100  1.000000: cycles:\n"
            "\t0000000000000000 [unknown] (/usr/lib/libmystery.so)\n\n"
            ".NET 101/101  2.000000: cycles:\n"
            "\t0000000000000000 instance void [Nethermind.Trie] "
            "Nethermind.Trie.TrieStore`1<class System.Object>::Commit(int64)+0x1 "
            "(/tmp/perf-101.map)\n\n"
        )

        result = self.run_fold(perf_script)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(".NET;[unknown] (libmystery.so) 1", result.stdout)
        self.assertIn(
            ".NET;instance void [Nethermind.Trie] Nethermind.Trie.TrieStore`1<class System.Object>::Commit(int64) 1",
            result.stdout,
        )

    def test_native_view_excludes_generic_managed_frames(self) -> None:
        generic = "instance void [Nethermind.Trie] Nethermind.Trie.TrieStore`1<class System.Object>::Commit(int64)"
        native = "rocksdb::DBImpl::BackgroundCall"
        unknown = "[unknown] (librocksdb.so)"
        profile = self.write_folded(
            "profile.folded",
            f".NET;{generic} 7\n.NET;{native} 5\n.NET;{unknown} 3\n",
        )

        result = self.run_report("native", str(profile), "10")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertNotIn(generic, result.stdout)
        self.assertIn(native, result.stdout)
        self.assertIn(unknown, result.stdout)

    def test_total_label_and_truncation_keep_the_method_tail(self) -> None:
        long_frame = (
            "instance void [Nethermind.Really.Long.Assembly.Name] "
            "Nethermind.Really.Long.Namespace.With.Many.Shared.Prefixes.Type::MethodNameTailForDiscrimination(int32)"
        )
        profile = self.write_folded("long.folded", f".NET;{long_frame} 5\n")

        top = self.run_report("top", str(profile), "1")
        total = self.run_report("total", str(profile), "1")

        self.assertEqual(top.returncode, 0, top.stderr)
        self.assertIn("Self %", top.stdout)
        self.assertIn("MethodNameTailForDiscrimination", top.stdout)
        self.assertEqual(total.returncode, 0, total.stderr)
        self.assertIn("Total %", total.stdout)
        self.assertNotIn("Self %", total.stdout)

    def test_compare_honors_odd_and_single_row_limits(self) -> None:
        before = self.write_folded(
            "before.folded",
            "root;Negative 50\nroot;Middle 10\nroot;Positive 10\nroot;Other 20\nroot;Small 10\n",
        )
        after = self.write_folded(
            "after.folded",
            "root;Negative 5\nroot;Middle 10\nroot;Positive 55\nroot;Other 20\nroot;Small 10\n",
        )

        one = self.run_report("compare", str(before), str(after), "1")
        odd = self.run_report("compare", str(before), str(after), "3")

        self.assertEqual(one.returncode, 0, one.stderr)
        self.assertEqual(len(self.data_rows(one.stdout)), 2)
        self.assertIn("Negative", one.stdout)
        self.assertIn("Positive", one.stdout)
        self.assertIn("...", one.stdout)
        self.assertEqual(odd.returncode, 0, odd.stderr)
        self.assertEqual(len(self.data_rows(odd.stdout)), 4)
        self.assertIn("...", odd.stdout)

    def test_compare_is_bytewise_under_an_ambient_utf8_locale(self) -> None:
        colon = "instance void [Nethermind.Evm] Namespace.Type::Method(int32)"
        spaced = "instance void [Nethermind.Evm] Namespace Type Method(int32)"
        before = self.write_folded("locale-before.folded", f"root;{colon} 50\nroot;{spaced} 50\n")
        after = self.write_folded("locale-after.folded", f"root;{colon} 25\nroot;{spaced} 75\n")

        result = self.run_report("compare", str(before), str(after), "2", locale="en_US.utf8")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(self.data_rows(result.stdout)), 2)
        self.assertIn(colon, result.stdout)
        self.assertIn(spaced, result.stdout)

    def test_positive_folded_profile_validator_rejects_empty_whitespace_and_zero_counts(self) -> None:
        empty = self.write_folded("empty.folded", "")
        whitespace = self.write_folded("whitespace.folded", " \t\n\n")
        zero = self.write_folded("zero.folded", ".NET;Frame 0\n")
        valid = self.write_folded("valid.folded", ".NET;Frame 1\n")

        result = self.run_report("top", str(empty))

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing or empty", result.stderr)
        for profile in (empty, whitespace, zero):
            with self.subTest(profile=profile.name):
                validation = self.run_folded_profile_validator(profile)
                self.assertNotEqual(validation.returncode, 0)
                self.assertIn("no positive-sample stack", validation.stderr)
        self.assertEqual(self.run_folded_profile_validator(valid).returncode, 0)

    def test_perf_recorder_identity_rejects_pid_reuse_without_signaling(self) -> None:
        proc_root = self.directory / "proc"
        process = proc_root / "123"
        process.mkdir(parents=True)
        stat_fields = ["S", *(["0"] * 18), "4243", "0"]
        (process / "stat").write_text(f"123 (perf) {' '.join(stat_fields)}\n", encoding="utf-8")
        (process / "comm").write_text("perf\n", encoding="utf-8")
        (process / "exe").write_text("", encoding="utf-8")
        signal_log = self.directory / "signals.log"
        environment = os.environ.copy()
        environment["RPC_BENCH_PROC_ROOT"] = str(proc_root)
        environment["SIGNAL_LOG"] = str(signal_log)

        result = self.run_rpc_library(
            """
            set -euo pipefail
            kill() { printf '%s %s\\n' "$1" "$2" >> "$SIGNAL_LOG"; }
            IFS=$'\\t' read -r start_time comm executable < <(perf_recorder_identity 123)
            [[ "$start_time" == "4243" && "$comm" == "perf" ]]
            if signal_perf_recorder_if_matches INT 123 4242 "$comm" "$executable"; then exit 1; fi
            [[ ! -s "$SIGNAL_LOG" ]]
            signal_perf_recorder_if_matches INT 123 "$start_time" "$comm" "$executable"
            [[ "$(cat "$SIGNAL_LOG")" == "-INT 123" ]]
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")

    def test_workflow_profile_contracts_cover_both_collectors(self) -> None:
        expb_workflow = EXPB_WORKFLOW.read_text(encoding="utf-8")
        rpc_workflow = RPC_WORKFLOW.read_text(encoding="utf-8")
        start_node = START_NODE.read_text(encoding="utf-8")
        stop_node = STOP_NODE.read_text(encoding="utf-8")

        self.assertEqual(expb_workflow.count('bash scripts/validate-folded-profile.sh "${folded_profile}"'), 2)
        self.assertEqual(expb_workflow.count("-x '*/perf.data'"), 2)
        self.assertEqual(expb_workflow.count('artifact_prefix="dottrace"'), 2)
        self.assertEqual(expb_workflow.count('artifact_prefix="profiling"'), 2)
        self.assertIn("pattern: ${{ needs.resolve.outputs.perf == 'true' && 'profiling-*' || 'dottrace-*' }}", expb_workflow)
        self.assertIn("bash scripts/validate-folded-profile.sh", rpc_workflow)
        self.assertIn("perf record --event cycles:u", start_node)
        self.assertIn('bash "$HERE/../validate-folded-profile.sh" "$folded_tmp"', stop_node)
        self.assertIn('signal_perf_recorder_if_matches INT', stop_node)
        self.assertIn('signal_perf_recorder_if_matches KILL', stop_node)

        workflow = yaml.safe_load(expb_workflow)
        expected_gate = "always() && (needs.resolve.outputs.dottrace == 'true' || needs.resolve.outputs.perf == 'true')"
        for job_name in ("benchmark", "benchmark-multi"):
            steps = workflow["jobs"][job_name]["steps"]
            for step_name in ("Collect and upload profiling artifacts", "Upload profiling artifact"):
                matching_steps = [step for step in steps if step.get("name") == step_name]
                self.assertEqual(len(matching_steps), 1, f"{job_name}/{step_name}")
                self.assertEqual(matching_steps[0].get("if"), expected_gate, f"{job_name}/{step_name}")


if __name__ == "__main__":
    unittest.main()
