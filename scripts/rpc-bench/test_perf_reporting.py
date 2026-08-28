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

ROOT = Path(__file__).resolve().parents[2]
PERF_FOLD = ROOT / "scripts" / "perf-fold.awk"
PERF_REPORT = ROOT / "scripts" / "perf-report.sh"
FOLDED_PROFILE_VALIDATOR = ROOT / "scripts" / "validate-folded-profile.sh"
EXPB_WORKFLOW = ROOT / ".github" / "workflows" / "run-expb-reproducible-benchmarks.yml"
RPC_WORKFLOW = ROOT / ".github" / "workflows" / "run-rpc-benchmarks.yml"
RPC_LIB = ROOT / "scripts" / "rpc-bench" / "lib.sh"
START_NODE = ROOT / "scripts" / "rpc-bench" / "start-node.sh"
STOP_NODE = ROOT / "scripts" / "rpc-bench" / "stop-node.sh"
START_PROFILERS = ROOT / "scripts" / "rpc-bench" / "start-profilers.sh"
RUN_JSONBENCH = ROOT / "scripts" / "rpc-bench" / "run-jsonbench.sh"
PROFILE_ARTIFACT_GATE = "always() && (needs.resolve.outputs.dottrace == 'true' || needs.resolve.outputs.perf == 'true')"

WORKFLOW_JOB_PATTERN = re.compile(
    r"(?ms)^  (?P<name>[A-Za-z0-9_-]+):[^\r\n]*\r?\n"
    r"(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:[^\r\n]*(?:\r?\n|\Z)|\Z)"
)
WORKFLOW_NAMED_STEP_PATTERN = re.compile(
    r"(?ms)^      - name: (?P<name>[^\r\n]+)\r?\n(?P<body>.*?)(?=^      - |\Z)"
)
WORKFLOW_STEP_IF_PATTERN = re.compile(r"(?m)^        if: (?P<condition>[^\r\n]*)\r?$")


def workflow_job_body(workflow: str, job_name: str) -> str:
    jobs = [match for match in WORKFLOW_JOB_PATTERN.finditer(workflow) if match["name"] == job_name]
    if len(jobs) != 1:
        raise AssertionError(f"expected exactly one {job_name!r} job, found {len(jobs)}")
    return jobs[0]["body"]


def workflow_named_step_body(workflow: str, job_name: str, step_name: str) -> str:
    steps = [
        match
        for match in WORKFLOW_NAMED_STEP_PATTERN.finditer(workflow_job_body(workflow, job_name))
        if match["name"] == step_name
    ]
    if len(steps) != 1:
        raise AssertionError(f"expected exactly one {job_name}/{step_name} step, found {len(steps)}")
    return steps[0]["body"]


def workflow_named_step_if(workflow: str, job_name: str, step_name: str) -> str:
    conditions = WORKFLOW_STEP_IF_PATTERN.findall(workflow_named_step_body(workflow, job_name, step_name))
    if len(conditions) != 1:
        raise AssertionError(f"expected exactly one condition for {job_name}/{step_name}, found {len(conditions)}")
    return conditions[0]


RESOLVE_RUN_MARKER = "        run: |\n"


def resolve_script(workflow: str) -> str:
    """The resolve step's shell body, dedented so it can be run under bash on its own."""
    marker = workflow.index(RESOLVE_RUN_MARKER, workflow.index("      - name: Resolve configuration\n"))
    lines = []
    for line in workflow[marker + len(RESOLVE_RUN_MARKER) :].split("\n"):
        if line.strip() and not line.startswith(" " * 10):
            break
        lines.append(line[10:])
    body = "\n".join(lines)
    if "${{" in body or "warmup_seconds=" not in body:
        raise AssertionError("could not extract a runnable resolve body")
    return body


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

    def write_executable(self, name: str, contents: str) -> Path:
        path = self.directory / name
        path.write_text(contents, encoding="utf-8")
        path.chmod(path.stat().st_mode | 0o111)
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
            # Same frame with trailing whitespace: it must fold into the sample above, not into a
            # second "[unknown] (libmystery.so))" frame carrying half of the library's samples.
            ".NET 100/100  3.000000: cycles:\n"
            "\t0000000000000000 [unknown] (/usr/lib/libmystery.so)  \n\n"
        )

        result = self.run_fold(perf_script)

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn(".NET;[unknown] (libmystery.so) 2", result.stdout)
        self.assertNotIn("libmystery.so))", result.stdout)
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
        # `+` is the whole answer here, so the table has to name the direction it points in.
        self.assertIn("before.folded -> after.folded", one.stdout)
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
        valid = self.write_folded(
            "valid.folded",
            ".NET;instance void [Nethermind.Trie] Nethermind.Trie.TrieStore::Commit() 1\n",
        )

        result = self.run_report("top", str(empty))

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing or empty", result.stderr)
        # Every view divides by the profile's total sample count, so none may reach awk with a zero
        # total - including compare, whose process substitution would swallow the failure.
        for arguments in (
            ("top", str(zero)),
            ("total", str(zero)),
            ("native", str(zero)),
            ("compare", str(zero), str(valid)),
            ("compare", str(valid), str(zero)),
        ):
            with self.subTest(view=arguments[0]):
                report = self.run_report(*arguments)
                self.assertNotEqual(report.returncode, 0, report.stdout)
                self.assertIn("no positive sample counts", report.stderr)
                self.assertNotIn("division by zero", report.stderr)
        for profile in (empty, whitespace, zero):
            with self.subTest(profile=profile.name):
                validation = self.run_folded_profile_validator(profile)
                self.assertNotEqual(validation.returncode, 0)
                self.assertIn("no positive-sample stack", validation.stderr)
        validation = self.run_folded_profile_validator(valid)
        self.assertEqual(validation.returncode, 0, validation.stderr)
        self.assertIn("managed=1 (100.00%)", validation.stdout)

    def test_folded_profile_validator_requires_managed_samples_and_reports_leaf_split(self) -> None:
        managed = "instance void [Nethermind.Trie] Nethermind.Trie.TrieStore::Commit()"
        cases = (
            ("unknown", ".NET;[unknown] (libcoreclr.so) 7\n", False, "unknown=7 (100.00%)"),
            ("native", ".NET;rocksdb::DBImpl::BackgroundCall 5\n", False, "native=5 (100.00%)"),
            (
                "mixed",
                f".NET;{managed} 5\n.NET;rocksdb::DBImpl::BackgroundCall 3\n.NET;[unknown] (libcoreclr.so) 2\n",
                True,
                "managed=5 (50.00%), native=3 (30.00%), unknown=2 (20.00%)",
            ),
        )

        for name, contents, succeeds, split in cases:
            with self.subTest(profile=name):
                validation = self.run_folded_profile_validator(self.write_folded(f"{name}.folded", contents))
                self.assertEqual(validation.returncode == 0, succeeds, f"{validation.stdout}\n{validation.stderr}")
                self.assertIn(split, validation.stdout)
                if not succeeds:
                    self.assertIn("no managed leaf samples", validation.stderr)

    def test_managed_frame_pattern_is_identical_in_the_reporter_and_the_validator(self) -> None:
        """The classifier regex is duplicated; a fix landing in only one file is silent.

        In perf-report.sh it decides what `native` lists, in validate-folded-profile.sh whether a
        profile is rejected outright - and each file has its own test, so both keep passing.
        """
        def managed_frame_patterns(path: Path) -> list[str]:
            # Awk regexes, so no literal whitespace; ':: ' is what makes one a managed frame.
            return [p for p in re.findall(r"~ /(\S+)/", path.read_text(encoding="utf-8")) if p.endswith("::")]

        reporter = managed_frame_patterns(PERF_REPORT)
        validator = managed_frame_patterns(FOLDED_PROFILE_VALIDATOR)

        self.assertEqual(len(reporter), 1, reporter)
        self.assertEqual(len(validator), 1, validator)
        self.assertEqual(reporter, validator, "the managed-frame regex must stay identical in both files")

    def test_perf_preflight_and_recorder_use_the_direct_perf_process(self) -> None:
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        self.write_executable(
            "bin/perf",
            "#!/bin/bash\n"
            "set -euo pipefail\n"
            "printf '%s\\n' \"$*\" >> \"$PERF_COMMAND_LOG\"\n"
            "case \"${1:-}\" in\n"
            "  stat) exit 0 ;;\n"
            "  record) exit 0 ;;\n"
            "  *) exit 64 ;;\n"
            "esac\n",
        )
        command_log = self.directory / "perf-commands.log"
        output = self.directory / "perf.data"
        record_log = self.directory / "perf-record.log"
        environment = os.environ.copy()
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        environment["PERF_COMMAND_LOG"] = str(command_log)
        environment["PERF_OUTPUT"] = str(output)
        environment["PERF_RECORD_LOG"] = str(record_log)

        result = self.run_rpc_library(
            """
            set -euo pipefail
            id() {
              if [[ "${1:-}" == "-u" ]]; then printf '0\\n'; else command id "$@"; fi
            }
            require_perf_access
            start_perf_recorder 99 4321 "$PERF_OUTPUT" "$PERF_RECORD_LOG"
            wait "$PERF_RECORDER_PID"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        commands = command_log.read_text(encoding="utf-8").splitlines()
        self.assertEqual(commands[0], "stat --event cycles:u -- true")
        self.assertIn("record --event cycles:u --freq 99 --call-graph fp --pid 4321", commands[1])
        # The invariant is that perf is launched directly: any wrapper would make $! the wrapper's
        # PID and break the identity tracking teardown depends on. Assert that rather than the
        # source text, so reflowing the command does not red the job.
        recorder = re.search(r"(?ms)^start_perf_recorder\(\) \{.*?^\}", RPC_LIB.read_text(encoding="utf-8"))
        self.assertIsNotNone(recorder, "start_perf_recorder must be defined in lib.sh")
        self.assertRegex(recorder[0], r"(?m)^\s*perf record\b")
        self.assertNotRegex(recorder[0], r"\b(sudo|as_root)\b")
        self.assertIn("PERF_RECORDER_PID=$!", recorder[0])

    def test_perf_preflight_falls_back_to_cpu_clock_when_cycles_are_unavailable(self) -> None:
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        self.write_executable(
            "bin/perf",
            "#!/bin/bash\n"
            "printf '%s\\n' \"$*\" >> \"$PERF_COMMAND_LOG\"\n"
            "case \"${1:-} ${3:-}\" in\n"
            "  'stat cycles:u') exit 1 ;;\n"
            "  'stat cpu-clock:u') exit 0 ;;\n"
            "  record*) exit 0 ;;\n"
            "  *) exit 64 ;;\n"
            "esac\n",
        )
        command_log = self.directory / "perf-commands.log"
        environment = os.environ.copy()
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        environment["PERF_COMMAND_LOG"] = str(command_log)

        result = self.run_rpc_library(
            """
            set -euo pipefail
            id() { printf '0\\n'; }
            require_perf_access
            start_perf_recorder 99 4321 ignored.data ignored.log
            wait "$PERF_RECORDER_PID"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertIn("perf sampling event: cpu-clock:u", result.stdout)
        commands = command_log.read_text(encoding="utf-8").splitlines()
        self.assertEqual(commands[:2], ["stat --event cycles:u -- true", "stat --event cpu-clock:u -- true"])
        self.assertIn("record --event cpu-clock:u --freq 99 --call-graph fp --pid 4321", commands[2])
        self.assertEqual(len(commands), 3, "the probed event is cached; the recorder must not probe again")

    def test_perf_preflight_rejects_non_root_without_running_perf(self) -> None:
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        self.write_executable("bin/id", "#!/usr/bin/env bash\necho 1000\n")
        self.write_executable(
            "bin/perf",
            "#!/usr/bin/env bash\nprintf 'called\\n' >> \"$PERF_COMMAND_LOG\"\nexit 0\n",
        )
        command_log = self.directory / "perf-commands.log"
        environment = os.environ.copy()
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        environment["PERF_COMMAND_LOG"] = str(command_log)

        result = self.run_rpc_library("set -euo pipefail; require_perf_access", environment)

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires the self-hosted runner to execute as root", result.stderr)
        self.assertFalse(command_log.exists())

    def test_perf_preflight_rejects_unusable_cycles_access_without_recording(self) -> None:
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        self.write_executable(
            "bin/perf",
            "#!/bin/bash\n"
            "printf '%s\\n' \"$*\" >> \"$PERF_COMMAND_LOG\"\n"
            "case \"${1:-}\" in\n"
            "  stat) exit 1 ;;\n"
            "  record) exit 99 ;;\n"
            "  *) exit 64 ;;\n"
            "esac\n",
        )
        command_log = self.directory / "perf-commands.log"
        environment = os.environ.copy()
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        environment["PERF_COMMAND_LOG"] = str(command_log)

        result = self.run_rpc_library(
            """
            id() { printf '0\\n'; }
            require_perf_access
            start_perf_recorder 99 4321 ignored.data ignored.log
            """,
            environment,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("perf can sample neither cycles:u nor cpu-clock:u as root", result.stderr)
        self.assertEqual(
            command_log.read_text(encoding="utf-8").splitlines(),
            ["stat --event cycles:u -- true", "stat --event cpu-clock:u -- true"],
        )

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

    def profiler_environment(self) -> tuple[dict[str, str], Path, Path]:
        """Fake perf (its `record` publishes its own identity under a fake /proc and stays alive), a
        fake /proc entry for the client process, and an empty diag dir."""
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        self.write_executable(
            "bin/perf",
            "#!/bin/bash\n"
            "printf '%s\\n' \"$*\" >> \"$PERF_COMMAND_LOG\"\n"
            "case \"${1:-}\" in\n"
            "  stat) exit 0 ;;\n"
            "  record)\n"
            "    mkdir -p \"$RPC_BENCH_PROC_ROOT/$$\"\n"
            "    printf '%s (perf) S 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 4243 0\\n' \"$$\" > \"$RPC_BENCH_PROC_ROOT/$$/stat\"\n"
            "    printf 'perf\\n' > \"$RPC_BENCH_PROC_ROOT/$$/comm\"\n"
            "    : > \"$RPC_BENCH_PROC_ROOT/$$/exe\"\n"
            "    sleep 4 ;;\n"
            "  *) exit 64 ;;\n"
            "esac\n",
        )
        proc_root = self.directory / "proc"
        (proc_root / "1300").mkdir(parents=True)
        (proc_root / "1300" / "status").write_text("Name:\tnethermind\nNSpid:\t1300\t42\n", encoding="utf-8")
        command_log = self.directory / "perf-commands.log"
        diag = self.directory / "diag"
        (diag / "perf").mkdir(parents=True)
        (diag / "dottrace").mkdir()
        (diag / "dottrace" / "control.svc").write_bytes(b"")
        environment = os.environ.copy()
        environment["PATH"] = f"{fake_bin}{os.pathsep}{environment['PATH']}"
        environment["PERF_COMMAND_LOG"] = str(command_log)
        environment["DOCKER_COMMAND_LOG"] = (self.directory / "docker-commands.log").as_posix()
        environment["RPC_BENCH_PROC_ROOT"] = str(proc_root)
        environment["DOTTRACE_START_TIMEOUT"] = "2"
        environment["DOTNET_TRACE_STOP_TIMEOUT"] = "10"
        environment["DIAG"] = diag.as_posix()
        return environment, diag, command_log

    def docker_commands(self) -> list[str]:
        log = self.directory / "docker-commands.log"
        return log.read_text(encoding="utf-8").splitlines() if log.exists() else []

    def write_node_env(self, diag: Path, dotnet_trace: bool = False) -> Path:
        values = {
            "CLIENT": "nethermind",
            "INSTANCE_SUFFIX": "",
            "CONTAINER_NAME": "rpcbench-primary",
            # Forward slashes: the file is `source`d, and unquoted backslashes would not survive.
            "DIAG_DIR": diag.as_posix(),
            "DOTTRACE": "true",
            "DOTTRACE_DEFERRED": "true",
            "PERF": "true",
            "PERF_FREQUENCY": "99",
            "PROFILE_AFTER_WARMUP": "true",
        }
        if dotnet_trace:
            values["DOTNET_TRACE"] = "true"
            (diag / "dotnet-trace").mkdir(exist_ok=True)
        env_file = self.directory / "node.env"
        env_file.write_text("".join(f"{k}={v}\n" for k, v in values.items()), encoding="utf-8")
        return env_file

    # `docker top` lists the dotTrace launcher, the client and, once attached, the dotnet-trace collector;
    # `docker logs` acknowledges a start message only once it has been appended to the control file, as
    # the real launcher would. `docker exec` answers the runtime probe and plays the collector: it publishes
    # its identity under the fake /proc, then waits to be stopped and writes the .nettrace on the way out.
    FAKE_DOCKER = r"""
        docker() {
          printf '%s\n' "$*" >> "$DOCKER_COMMAND_LOG"
          case "$1" in
            top)
              printf '%s\n' 'PID ARGS' '1200 /opt/dottrace/dottrace start --framework=NetCore' '1300 /nethermind/nethermind --datadir=/execution-data'
              if [[ -f "$DIAG/collector.pid" ]]; then
                printf '%s /opt/dotnet-trace/dotnet-trace collect -p 42 --clrevents gc+contention+threading+exception\n' "$(cat "$DIAG/collector.pid")"
              fi ;;
            logs)
              printf '##dotTrace["connected", {pid: 1300, path: "/nethermind/nethermind"}]\n'
              if [[ "${DOTTRACE_ACK:-true}" == "true" ]] && grep -qF '##dotTrace["start"]' "$DIAG/dottrace/control.svc" 2>/dev/null; then
                printf '##dotTrace["started", {pid: 1300, path: "/nethermind/nethermind"}]\n'
              fi ;;
            exec)
              shift
              while [[ "${1:-}" == "-e" ]]; do shift 2; done
              shift
              case "$*" in
                'dotnet --list-runtimes')
                  printf 'Microsoft.AspNetCore.App 10.0.11 [/usr/share/dotnet/shared/Microsoft.AspNetCore.App]\n'
                  printf 'Microsoft.NETCore.App 10.0.11 [/usr/share/dotnet/shared/Microsoft.NETCore.App]\n' ;;
                'test -d '*) [[ "${DOTNET_FXR:-true}" == "true" ]] ;;
                '/opt/dotnet-trace/dotnet-trace collect '*)
                  if [[ "${DOTNET_TRACE_STARTS:-true}" != "true" ]]; then
                    printf 'You must install or update .NET to run this application.\n.NET location: Not found\n' >&2
                    return 1
                  fi
                  printf '%s' "$BASHPID" > "$DIAG/collector.pid"
                  mkdir -p "$RPC_BENCH_PROC_ROOT/$BASHPID"
                  printf 'Name:\tdotnet-trace\nNSpid:\t%s\t77\n' "$BASHPID" > "$RPC_BENCH_PROC_ROOT/$BASHPID/status"
                  # Background jobs of a non-interactive shell ignore SIGINT, so the in-container
                  # `kill -INT` below is played back as SIGTERM to this fake.
                  sleep 30 & sleeper=$!
                  trap 'kill "$sleeper" 2>/dev/null; printf nettrace > "$DIAG/dotnet-trace/rpcbench.nettrace"; exit 0' TERM
                  wait "$sleeper" ;;
                'sh -c kill -INT "$1" sh 77') kill -TERM "$(cat "$DIAG/collector.pid")" ;;
                *) return 64 ;;
              esac ;;
            *) return 64 ;;
          esac
        }
    """

    def test_start_profilers_records_the_recorder_identity_and_refuses_to_run_twice(self) -> None:
        environment, diag, command_log = self.profiler_environment()
        env_file = self.write_node_env(diag)
        environment["NODE_ENV"] = str(env_file)

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            set -euo pipefail
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            if (start_profilers "$NODE_ENV"); then echo "second start accepted"; exit 1; fi
            source "$NODE_ENV"
            kill "$PERF_PID"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertIn("refusing to start a second recorder", result.stderr)
        self.assertIn("dotTrace: data collection started", result.stdout)
        self.assertEqual(
            (diag / "dottrace" / "control.svc").read_bytes(),
            b'\n##dotTrace["start"]\r\n',
            "service messages must start on a new line and end with a carriage return",
        )
        commands = command_log.read_text(encoding="utf-8").splitlines()
        records = [c for c in commands if c.startswith("record ")]
        self.assertEqual(len(records), 1, commands)
        self.assertIn("--pid 1300", records[0])
        node_env = env_file.read_text(encoding="utf-8")
        self.assertIn("PERF_NODE_PID=1300\n", node_env)
        self.assertIn("PERF_CONTAINER_PID=42\n", node_env)
        self.assertIn("PERF_RECORDER_START_TIME=4243\n", node_env)
        self.assertIn("PERF_RECORDER_COMM=perf\n", node_env)
        self.assertEqual(node_env.count("DOTTRACE_STARTED_AT="), 1)
        self.assertEqual(node_env.count("PROFILERS_STARTED_AT="), 1)
        self.assertEqual(
            [c for c in self.docker_commands() if c.startswith("exec ")],
            [],
            "dotnet-trace must stay off unless DOTNET_TRACE=true",
        )
        self.assertNotIn("DOTNET_TRACE_PID=", node_env)

    def test_start_profilers_attaches_dotnet_trace_inside_the_container_and_stops_it_before_the_node(self) -> None:
        environment, diag, command_log = self.profiler_environment()
        env_file = self.write_node_env(diag, dotnet_trace=True)
        environment["NODE_ENV"] = str(env_file)
        environment["DOTNET_TRACE_MAX_SECONDS"] = "3900"

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            set -euo pipefail
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            source "$NODE_ENV"
            kill -0 "$DOTNET_TRACE_PID"
            stop_dotnet_trace_collector rpcbench-primary "$DOTNET_TRACE_PID" "$DOTNET_TRACE_COLLECTOR_PID"
            if kill -0 "$DOTNET_TRACE_PID" 2>/dev/null; then echo "collector still running"; exit 1; fi
            kill "$PERF_PID"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        docker_commands = self.docker_commands()
        self.assertIn("exec rpcbench-primary dotnet --list-runtimes", docker_commands)
        self.assertIn("exec rpcbench-primary test -d /usr/share/dotnet/host/fxr", docker_commands)
        self.assertEqual(
            [c for c in docker_commands if " collect " in c],
            [
                "exec -e DOTNET_ROOT=/usr/share/dotnet -e DOTNET_ROLL_FORWARD=Major rpcbench-primary "
                "/opt/dotnet-trace/dotnet-trace collect -p 42 "
                "--clrevents gc+contention+threading+exception --clreventlevel verbose "
                "-o /dotnet-trace-output/rpcbench.nettrace --duration 01:05:00"
            ],
            "the collector attaches exactly once, to the client's container pid",
        )
        self.assertIn('exec rpcbench-primary sh -c kill -INT "$1" sh 77', docker_commands)
        self.assertIn(
            "dotnet-trace collecting gc+contention+threading+exception (verbose) from container pid 42, capped at 3900s",
            result.stdout,
        )
        self.assertEqual((diag / "dotnet-trace" / "rpcbench.nettrace").read_bytes(), b"nettrace")
        node_env = env_file.read_text(encoding="utf-8")
        self.assertIn("DOTNET_TRACE_COLLECTOR_PID=77\n", node_env)
        self.assertEqual(node_env.count("DOTNET_TRACE_PID="), 1)
        self.assertLess(
            node_env.index("DOTNET_TRACE_PID="),
            node_env.index("PERF_PID="),
            "dotnet-trace attaches before perf so a failure leaves no recorder behind",
        )
        self.assertEqual(len([c for c in command_log.read_text(encoding="utf-8").splitlines() if c.startswith("record ")]), 1)

    def test_start_profilers_dies_with_the_log_when_the_dotnet_trace_collector_exits_immediately(self) -> None:
        environment, diag, command_log = self.profiler_environment()
        env_file = self.write_node_env(diag, dotnet_trace=True)
        environment["NODE_ENV"] = str(env_file)
        environment["DOTNET_TRACE_STARTS"] = "false"

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            """,
            environment,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("dotnet-trace exited immediately", result.stdout)
        self.assertIn(".NET location: Not found", result.stdout, "the collector log must be dumped")
        self.assertIn("dotnet-trace did not start", result.stderr)
        self.assertFalse(
            command_log.exists() and "record" in command_log.read_text(encoding="utf-8"),
            "perf must not start when the dotnet-trace collector could not be attached",
        )
        node_env = env_file.read_text(encoding="utf-8")
        self.assertNotIn("DOTNET_TRACE_PID=", node_env)
        self.assertNotIn("PROFILERS_STARTED_AT=", node_env)

    def test_start_profilers_requires_hostfxr_under_the_container_dotnet_root(self) -> None:
        environment, diag, _ = self.profiler_environment()
        env_file = self.write_node_env(diag, dotnet_trace=True)
        environment["NODE_ENV"] = str(env_file)
        environment["DOTNET_FXR"] = "false"

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            """,
            environment,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("no .NET root with host/fxr found inside rpcbench-primary", result.stderr)
        self.assertEqual([c for c in self.docker_commands() if " collect " in c], [])

    def test_container_dotnet_root_probe_does_not_consume_the_runtime_listing(self) -> None:
        """The probe runs inside the loop reading the listing, so it must not share its stdin."""
        environment, _, _ = self.profiler_environment()

        result = self.run_rpc_library(
            r"""
            set -euo pipefail
            docker() {
              shift                                        # exec
              while [[ "${1:-}" == "-e" ]]; do shift 2; done
              shift                                        # container
              case "$*" in
                'dotnet --list-runtimes')
                  printf 'Microsoft.NETCore.App 8.0.0 [/opt/dotnet-a/shared/Microsoft.NETCore.App]\n'
                  printf 'Microsoft.NETCore.App 10.0.0 [/opt/dotnet-b/shared/Microsoft.NETCore.App]\n' ;;
                'test -d /opt/dotnet-a/host/fxr') cat > /dev/null; return 1 ;;
                'test -d /opt/dotnet-b/host/fxr') cat > /dev/null; return 0 ;;
                *) return 64 ;;
              esac
            }
            container_dotnet_root rpcbench-primary
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertEqual(result.stdout.strip(), "/opt/dotnet-b")

    def test_stop_dotnet_trace_collector_signals_only_the_collector_it_started(self) -> None:
        environment, diag, _ = self.profiler_environment()

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            set -euo pipefail
            ( : ) & finished=$!
            wait "$finished"
            if stop_dotnet_trace_collector rpcbench-primary "$finished" 77; then echo "unexpected success"; exit 1; fi
            sleep 5 & alive=$!
            if stop_dotnet_trace_collector rpcbench-primary "$alive" 77; then echo "unexpected success"; exit 1; fi
            kill "$alive"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertIn("exited before it was stopped; the trace does not cover the measured phase", result.stdout)
        self.assertIn("is pid '<none>', expected 77; refusing to signal", result.stdout)
        self.assertEqual([c for c in self.docker_commands() if "kill -INT" in c], [])

    def test_start_profilers_fails_when_dottrace_never_acknowledges_the_start(self) -> None:
        environment, diag, command_log = self.profiler_environment()
        env_file = self.write_node_env(diag)
        environment["NODE_ENV"] = str(env_file)
        environment["DOTTRACE_ACK"] = "false"

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            """,
            environment,
        )

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("did not acknowledge the start message", result.stderr)
        self.assertFalse(
            command_log.exists() and "record" in command_log.read_text(encoding="utf-8"),
            "perf must not start when dotTrace collection could not be started",
        )
        self.assertNotIn("PROFILERS_STARTED_AT=", env_file.read_text(encoding="utf-8"))

    def test_start_profilers_retry_after_a_perf_failure_leaves_the_collecting_dottrace_alone(self) -> None:
        environment, diag, command_log = self.profiler_environment()
        env_file = self.write_node_env(diag)
        # The first attempt got dotTrace collecting and then died on perf, so its start was recorded.
        with env_file.open("a", encoding="utf-8") as f:
            f.write("DOTTRACE_STARTED_AT=2026-08-26T00:00:00Z\n")
        environment["NODE_ENV"] = str(env_file)
        environment["DOTTRACE_ACK"] = "false"

        result = self.run_rpc_library(
            self.FAKE_DOCKER
            + """
            set -euo pipefail
            id() { printf '0\\n'; }
            require_perf_access
            start_profilers "$NODE_ENV"
            source "$NODE_ENV"
            kill "$PERF_PID"
            """,
            environment,
        )

        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertIn("dotTrace: data collection already started at 2026-08-26T00:00:00Z", result.stdout)
        self.assertEqual((diag / "dottrace" / "control.svc").read_bytes(), b"", "no second start message")
        self.assertEqual(len([c for c in command_log.read_text(encoding="utf-8").splitlines() if c.startswith("record ")]), 1)
        node_env = env_file.read_text(encoding="utf-8")
        self.assertEqual(node_env.count("DOTTRACE_STARTED_AT="), 1)
        self.assertEqual(node_env.count("PROFILERS_STARTED_AT="), 1)

    FAKE_GIT = r"""#!/bin/bash
printf '%s\n' "$*" >> "$FAKE_STATE/git.log"
if [[ "$1" == "ls-remote" ]]; then
  [[ -z "${FAKE_GIT_SHA:-}" ]] || printf '%s\trefs/heads/%s\n' "$FAKE_GIT_SHA" "$3"
fi
if [[ "$1" == "init" ]]; then mkdir -p "${@: -1}/runner" && : > "${@: -1}/runner/Dockerfile"; fi
exit 0
"""

    # `build` registers the tag `image inspect` answers for; `run` numbers its results.csv so a
    # stale output republished by a later invocation is told apart from that invocation's own.
    FAKE_DOCKER_CLI = r"""#!/bin/bash
printf '%s\n' "$*" >> "$FAKE_STATE/docker.log"
case "$1 $2" in
  "build -q") prev=""; for a in "$@"; do [[ "$prev" == "-t" ]] && : > "$FAKE_STATE/images/${a//:/_}"; prev="$a"; done ;;
  "image inspect") [[ -f "$FAKE_STATE/images/${3//:/_}" ]] ;;
  "run --rm")
    n=$(( $(cat "$FAKE_STATE/runs" 2>/dev/null || echo 0) + 1 )); printf '%s' "$n" > "$FAKE_STATE/runs"
    for a in "$@"; do [[ "$a" == *:/io ]] && printf 'run %s\n' "$n" > "${a%:/io}/out/results.csv"; done
    true ;;
esac
"""

    def run_jsonbench(self, environment: dict[str, str], out_dir: Path) -> subprocess.CompletedProcess[str]:
        environment = dict(environment)
        environment["OUT_DIR"] = out_dir.as_posix()
        # Git Bash needs /c/... paths for the scripts' absolute-path guards, and the fakes must precede
        # the real git/docker/sudo on its PATH; elsewhere cygpath is absent and the fallback is a no-op.
        return subprocess.run(
            [
                BASH, "-c",
                'to_posix() { cygpath -u "$1" 2>/dev/null || printf "%s" "$1"; }; '
                'export PATH="$(to_posix "$FAKE_BIN"):$PATH" SCRATCH_ROOT="$(to_posix "$SCRATCH_ROOT")" OUT_DIR="$(to_posix "$OUT_DIR")"; exec "$1"',
                "bash", str(RUN_JSONBENCH),
            ],
            cwd=ROOT,
            check=False,
            text=True,
            capture_output=True,
            env=environment,
        )

    def test_every_rpc_bench_script_with_a_shebang_is_committed_executable(self) -> None:
        # The workflow, run-rpc-sweep.sh and run_jsonbench above all run these by path rather than
        # through `bash <path>`, so a script committed 100644 dies with exit 126 wherever the
        # checkout's mode bits are honoured. The index mode is the only platform-independent record
        # of the bit — a Windows working tree reports nothing useful about it.
        listing = subprocess.run(
            ["git", "ls-files", "-s", "--", "scripts/rpc-bench"],
            cwd=ROOT,
            check=False,
            text=True,
            capture_output=True,
        )
        if listing.returncode != 0:
            self.skipTest(f"git is unavailable: {listing.stderr.strip()}")
        not_executable = []
        for line in listing.stdout.splitlines():
            metadata, _, path = line.partition("\t")
            if not path.endswith(".sh") or not (ROOT / path).read_bytes().startswith(b"#!"):
                continue
            mode = metadata.split(" ", 1)[0]
            if mode != "100755":
                not_executable.append(f"{path} ({mode})")
        self.assertEqual(not_executable, [], "a script with a shebang must be committed executable")

    def test_run_jsonbench_reuses_the_preparation_only_when_asked_and_unchanged(self) -> None:
        fake_bin = self.directory / "bin"
        fake_bin.mkdir()
        state = self.directory / "state"
        (state / "images").mkdir(parents=True)
        self.write_executable("bin/sudo", '#!/bin/bash\nexec "$@"\n')
        self.write_executable("bin/git", self.FAKE_GIT)
        self.write_executable("bin/docker", self.FAKE_DOCKER_CLI)
        environment = os.environ.copy()
        environment["FAKE_BIN"] = fake_bin.as_posix()
        environment["FAKE_STATE"] = state.as_posix()
        environment["RPC_URL"] = "http://localhost:1"
        environment["SCRATCH_ROOT"] = (self.directory / "scratch").as_posix()
        environment["JB_REF"] = "testref"
        environment["JB_REUSE_PREPARED"] = "true"

        def preparations() -> tuple[int, int]:
            git_log = (state / "git.log").read_text(encoding="utf-8").splitlines()
            docker_log = (state / "docker.log").read_text(encoding="utf-8").splitlines()
            return (
                len([c for c in git_log if " fetch " in f" {c} "]),
                len([c for c in docker_log if c.startswith("build ")]),
            )

        first = self.run_jsonbench(environment, self.directory / "out1")
        self.assertEqual(first.returncode, 0, f"{first.stdout}\n{first.stderr}")
        self.assertEqual(preparations(), (1, 1))
        self.assertEqual((self.directory / "out1" / "results.csv").read_text(encoding="utf-8"), "run 1\n")
        self.assertEqual(
            (self.directory / "scratch" / "jsonbench" / "prepared").read_text(encoding="utf-8"),
            "https://github.com/NethermindEth/json-bench.git@testref\n",
        )

        # Same repo/ref: the checkout and image are reused, the previous outputs are not.
        second = self.run_jsonbench(environment, self.directory / "out2")
        self.assertEqual(second.returncode, 0, f"{second.stdout}\n{second.stderr}")
        self.assertEqual(preparations(), (1, 1))
        self.assertIn("Reusing the json-bench checkout, runner image and fixture", second.stdout)
        self.assertEqual((self.directory / "out2" / "results.csv").read_text(encoding="utf-8"), "run 2\n")

        # Default (the sweep, a run without warm-up): wiped and prepared afresh as before.
        environment["JB_REUSE_PREPARED"] = "false"
        third = self.run_jsonbench(environment, self.directory / "out3")
        self.assertEqual(third.returncode, 0, f"{third.stdout}\n{third.stderr}")
        self.assertEqual(preparations(), (2, 2))
        self.assertNotIn("Reusing", third.stdout)

        # A different ref never reuses another ref's checkout.
        environment["JB_REUSE_PREPARED"] = "true"
        environment["JB_REF"] = "otherref"
        fourth = self.run_jsonbench(environment, self.directory / "out4")
        self.assertEqual(fourth.returncode, 0, f"{fourth.stdout}\n{fourth.stderr}")
        self.assertEqual(preparations(), (3, 3))
        self.assertNotIn("Reusing", fourth.stdout)

        # A ref that resolves is keyed on the commit, so the same name over a moved branch misses.
        environment["JB_REF"] = "movingref"
        environment["FAKE_GIT_SHA"] = "a" * 40
        fifth = self.run_jsonbench(environment, self.directory / "out5")
        self.assertEqual(fifth.returncode, 0, f"{fifth.stdout}\n{fifth.stderr}")
        self.assertEqual(preparations(), (4, 4))
        self.assertEqual(
            (self.directory / "scratch" / "jsonbench" / "prepared").read_text(encoding="utf-8"),
            f"https://github.com/NethermindEth/json-bench.git@{'a' * 40}\n",
        )
        sixth = self.run_jsonbench(environment, self.directory / "out6")
        self.assertEqual(sixth.returncode, 0, f"{sixth.stdout}\n{sixth.stderr}")
        self.assertEqual(preparations(), (4, 4))
        self.assertIn("Reusing", sixth.stdout)

        environment["FAKE_GIT_SHA"] = "b" * 40
        seventh = self.run_jsonbench(environment, self.directory / "out7")
        self.assertEqual(seventh.returncode, 0, f"{seventh.stdout}\n{seventh.stderr}")
        self.assertEqual(preparations(), (5, 5))
        self.assertNotIn("Reusing", seventh.stdout)

    def resolve(self, **inputs: str) -> tuple[subprocess.CompletedProcess[str], dict[str, str]]:
        """Run the workflow's resolve body with these dispatch inputs; return it plus its outputs."""
        script = self.directory / "resolve.sh"
        script.write_text(resolve_script(RPC_WORKFLOW.read_text(encoding="utf-8")), encoding="utf-8", newline="\n")
        output = self.directory / "github-output"
        output.write_text("", encoding="utf-8")
        environment = os.environ.copy()
        environment.update(
            EVENT_NAME="workflow_dispatch",
            PUSH_BRANCH="feature/profiling",
            GITHUB_OUTPUT=output.as_posix(),
            **inputs,
        )
        result = subprocess.run(
            [BASH, str(script)], cwd=ROOT, check=False, text=True, capture_output=True, env=environment
        )
        values: dict[str, str] = {}
        for line in output.read_text(encoding="utf-8").splitlines():
            name, separator, value = line.partition("=")
            if separator:
                values.setdefault(name, value)
        return result, values

    @unittest.skipUnless(shutil.which("jq"), "jq is required to run the resolve body")
    def test_dotnet_trace_is_only_resolved_where_a_warmup_precedes_the_measured_cell(self) -> None:
        # The collector attaches between the warm-up and the cell, and nettrace-report.cs states GC
        # pause and contention as a share of the window it covers. So dotnet_trace is accepted only on
        # the shape that has a warm-up, and it supplies one when the dispatch does not: otherwise the
        # window would also hold json-bench's clone, image build and corpus conversion, deflating both
        # shares with nothing in the artifact to show it had happened.
        jsonbench = {"IN_TOOL": "jsonbench", "IN_CLIENT": "nethermind"}

        result, outputs = self.resolve(**jsonbench, IN_DOTNET_TRACE="true", IN_TOOL_CONFIG='{"duration":"600s"}')
        self.assertEqual(result.returncode, 0, f"{result.stdout}\n{result.stderr}")
        self.assertEqual(outputs["warmup_seconds"], "60")
        # The cap runs from the attach, which the warm-up now keeps immediately ahead of the cell.
        self.assertEqual(outputs["dotnet_trace_max_seconds"], "1200")

        # An explicitly requested warm-up is kept as given; without dotnet_trace nothing is implied.
        _, outputs = self.resolve(
            **jsonbench, IN_DOTNET_TRACE="true", IN_TOOL_CONFIG='{"duration":"600s","corpus_warmup_duration":"120"}'
        )
        self.assertEqual(outputs["warmup_seconds"], "120")
        _, outputs = self.resolve(**jsonbench, IN_TOOL_CONFIG='{"duration":"600s"}')
        self.assertEqual(outputs["warmup_seconds"], "0")

        for label, inputs in (
            ("an explicit zero warm-up", {**jsonbench, "IN_TOOL_CONFIG": '{"corpus_warmup_duration":0}'}),
            ("a tool with no warm-up", {"IN_TOOL": "flood", "IN_CLIENT": "nethermind"}),
            ("a comparison run", {**jsonbench, "IN_REFERENCE_CLIENT": "geth"}),
        ):
            with self.subTest(rejected=label):
                result, _ = self.resolve(IN_DOTNET_TRACE="true", **inputs)
                self.assertEqual(result.returncode, 1, f"{result.stdout}\n{result.stderr}")
                self.assertIn("::error::", result.stdout)

    def test_profilers_start_between_the_warmup_and_the_measured_cell(self) -> None:
        start_node = START_NODE.read_text(encoding="utf-8")
        start_profilers = START_PROFILERS.read_text(encoding="utf-8")
        rpc_workflow = RPC_WORKFLOW.read_text(encoding="utf-8")

        # No warm-up: as before — perf starts right after RPC is ready and dotTrace collects from launch.
        self.assertIn(
            'if [[ "$PROFILE_AFTER_WARMUP" == "true" ]]; then\n'
            '  log "profilers deferred: run start-profilers.sh after the warm-up"\n'
            'elif [[ "$PERF" == "true" || "$DOTNET_TRACE" == "true" ]]; then\n'
            '  start_profilers "$STATE_DIR/node$SUFFIX.env"\n'
            "fi",
            start_node,
        )
        self.assertIn(
            '[[ "$DOTTRACE" == "true" && "$PROFILE_AFTER_WARMUP" == "true" ]] && DOTTRACE_DEFERRED="true"',
            start_node,
        )
        self.assertIn(
            'entry_args+=(--collect-data-from-start=off --service-output=on "--service-input=/dottrace-output/$DOTTRACE_CONTROL_FILE_NAME")',
            start_node,
        )
        self.assertNotIn("perf record", start_node)
        self.assertIn('start_profilers "$NODE_ENV_FILE"', start_profilers)
        self.assertIn('"${PROFILE_AFTER_WARMUP:-false}" != "true"', start_profilers)

        job = workflow_job_body(rpc_workflow, "benchmark")
        order = [
            "- name: Start node\n",
            "- name: Warm up node\n",
            "- name: Start profilers\n",
            "- name: Run json-bench benchmark\n",
            "- name: Stop node and verify DB integrity\n",
        ]
        positions = [job.index(step) for step in order]
        self.assertEqual(positions, sorted(positions), "profilers must start after the warm-up and before the measured cell")
        self.assertIn(
            "PROFILE_AFTER_WARMUP: ${{ needs.resolve.outputs.warmup_seconds != '0' && 'true' || 'false' }}",
            workflow_named_step_body(rpc_workflow, "benchmark", "Start node"),
        )
        warmup = workflow_named_step_body(rpc_workflow, "benchmark", "Warm up node")
        self.assertIn('export OUT_DIR="${SCRATCH_ROOT}/warmup-cell/single"', warmup)
        self.assertIn('JB_MAX_FAIL_RATE_PCT="100"', warmup)
        self.assertIn('JB_REUSE_PREPARED: "true"', warmup)
        self.assertIn(
            "JB_REUSE_PREPARED: ${{ needs.resolve.outputs.warmup_seconds != '0' && 'true' || 'false' }}",
            workflow_named_step_body(rpc_workflow, "benchmark", "Run json-bench benchmark"),
        )
        self.assertEqual(
            workflow_named_step_if(rpc_workflow, "benchmark", "Start profilers"),
            "needs.resolve.outputs.benchmark_tool == 'jsonbench' && needs.resolve.outputs.warmup_seconds != '0' "
            "&& (needs.resolve.outputs.perf == 'true' || needs.resolve.outputs.dottrace == 'true' "
            "|| needs.resolve.outputs.dotnet_trace == 'true')",
        )

    def test_dotnet_trace_sidecar_is_stopped_before_the_node_and_shipped_as_its_own_artifact(self) -> None:
        start_node = START_NODE.read_text(encoding="utf-8")
        stop_node = STOP_NODE.read_text(encoding="utf-8")
        rpc_workflow = RPC_WORKFLOW.read_text(encoding="utf-8")

        self.assertIn('-v "$DOTNET_TRACE_HOST_PATH:$DOTNET_TRACE_CONTAINER_PATH:ro"', start_node)
        self.assertIn('-v "$DIAG_DIR/dotnet-trace:$DOTNET_TRACE_OUTPUT_PATH:rw"', start_node)
        self.assertIn('echo "DOTNET_TRACE=$DOTNET_TRACE"', start_node)
        # SIGINT inside the container finalizes the .nettrace, so the collector must go before the
        # container does — and before perf's finalization, which reads through the live container too.
        self.assertLess(
            stop_node.index("stop_dotnet_trace_collector"),
            stop_node.index("signal_perf_recorder_if_matches INT"),
        )
        self.assertLess(stop_node.index("stop_dotnet_trace_collector"), stop_node.index('docker stop -t "$STOP_GRACE"'))
        self.assertIn("dotnet-trace collection FAILED", stop_node)

        job = workflow_job_body(rpc_workflow, "benchmark")
        order = [
            "- name: Stop node and verify DB integrity\n",
            "- name: Collect dotnet-trace\n",
            "- name: Upload perf profile\n",
            "- name: Upload dotnet-trace\n",
        ]
        positions = [job.index(step) for step in order]
        self.assertEqual(positions, sorted(positions))
        dotnet_trace_gate = "always() && needs.resolve.outputs.dotnet_trace == 'true'"
        for step_name in ("Collect dotnet-trace", "Upload dotnet-trace"):
            self.assertEqual(workflow_named_step_if(rpc_workflow, "benchmark", step_name), dotnet_trace_gate)
        self.assertIn("name: dotnet-trace-rpcbench", workflow_named_step_body(rpc_workflow, "benchmark", "Upload dotnet-trace"))
        self.assertIn(
            "DOTNET_TRACE: ${{ needs.resolve.outputs.dotnet_trace }}",
            workflow_named_step_body(rpc_workflow, "benchmark", "Start node"),
        )
        self.assertIn('DOTNET_TRACE: "false"', workflow_named_step_body(rpc_workflow, "benchmark", "Start reference node"))
        self.assertIn("rpcbench.nettrace", workflow_named_step_body(rpc_workflow, "benchmark", "Publish step summary"))

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
        self.assertEqual(
            expb_workflow.count(
                "# Remove this temporary pin once default main advertises --perf in execute-scenarios --help (execution-payloads-benchmarks#27); the help probe below is the runtime guard."
            ),
            2,
        )
        for job_name in ("benchmark", "benchmark-multi"):
            job_body = workflow_job_body(expb_workflow, job_name)
            self.assertIn(
                'if [[ "${PERF}" == "true" && "${EXPB_REPO}" == "NethermindEth/execution-payloads-benchmarks" && "${EXPB_BRANCH}" == "main" ]]; then',
                job_body,
            )
            self.assertIn('expb_help="$("${expb_bin}" execute-scenarios --help 2>&1)"', job_body)
        self.assertIn("bash scripts/validate-folded-profile.sh", rpc_workflow)
        self.assertIn("zip -9r \"${ARCHIVE}\" perf -x '*/perf.data'", rpc_workflow)
        self.assertIn("require_perf_access", rpc_workflow)
        self.assertIn("require_perf_access", start_node)
        self.assertLess(
            rpc_workflow.index("- name: Verify perf profiling prerequisites"),
            rpc_workflow.index("- name: Ensure Docker is installed"),
        )
        self.assertLess(
            start_node.index('if [[ "$PERF" == "true" ]]; then\n  require_perf_access'),
            start_node.index('mkdir -p "$STATE_DIR"'),
        )
        self.assertIn(
            "# 6) Start the profilers once the node serves RPC, so they exclude startup. With a warm-up the\n"
            "#    workflow starts them via start-profilers.sh after it, so they exclude the warm-up as well.",
            start_node,
        )
        self.assertNotIn("itself rather than startup and warm-up", start_node)
        self.assertIn('perf record --event "$PERF_SAMPLING_EVENT"', RPC_LIB.read_text(encoding="utf-8"))
        self.assertIn('bash "$HERE/../validate-folded-profile.sh" "$folded_tmp"', stop_node)
        self.assertIn('signal_perf_recorder_if_matches INT', stop_node)
        self.assertIn('signal_perf_recorder_if_matches KILL', stop_node)

        for job_name in ("benchmark", "benchmark-multi"):
            for step_name in ("Collect and upload profiling artifacts", "Upload profiling artifact"):
                self.assertEqual(workflow_named_step_if(expb_workflow, job_name, step_name), PROFILE_ARTIFACT_GATE)

        perf_preflight = workflow_named_step_body(
            rpc_workflow,
            "benchmark",
            "Verify perf profiling prerequisites",
        )
        self.assertIn("source scripts/rpc-bench/lib.sh", perf_preflight)
        self.assertIn("require_perf_access", perf_preflight)
        mutated_rpc_workflow = rpc_workflow.replace("          require_perf_access\n", "", 1)
        self.assertNotEqual(mutated_rpc_workflow, rpc_workflow)
        with self.assertRaises(AssertionError):
            self.assertIn(
                "require_perf_access",
                workflow_named_step_body(
                    mutated_rpc_workflow,
                    "benchmark",
                    "Verify perf profiling prerequisites",
                ),
            )

        dottrace_only = "always() && needs.resolve.outputs.dottrace == 'true'"
        mutated_workflow = expb_workflow.replace(PROFILE_ARTIFACT_GATE, dottrace_only, 1)
        self.assertNotEqual(mutated_workflow, expb_workflow)
        with self.assertRaises(AssertionError):
            for job_name in ("benchmark", "benchmark-multi"):
                for step_name in ("Collect and upload profiling artifacts", "Upload profiling artifact"):
                    self.assertEqual(workflow_named_step_if(mutated_workflow, job_name, step_name), PROFILE_ARTIFACT_GATE)

    def test_perf_and_dotnet_trace_preconditions_are_resolved_before_the_runner_is_paid_for(self) -> None:
        rpc_workflow = RPC_WORKFLOW.read_text(encoding="utf-8")
        resolve = workflow_job_body(rpc_workflow, "resolve")

        for collector in ("dottrace", "perf", "dotnet_trace"):
            with self.subTest(collector=collector):
                self.assertIn(f'if [[ "${{{collector}}}" == "true" && "${{client}}" != "nethermind" ]]; then', resolve)

        # The cap runs from the attach, which the implied warm-up keeps immediately ahead of the cell
        # (behaviour covered by test_dotnet_trace_is_only_resolved_where_a_warmup_precedes_the_measured_cell);
        # sizing it before the warm-up is resolved would cap against the wrong window.
        implied_warmup = 'if [[ "${dotnet_trace}" == "true" && "${warmup_seconds}" == "0" ]]; then'
        self.assertIn(implied_warmup, resolve)
        self.assertLess(
            resolve.index(implied_warmup),
            resolve.index('dotnet_trace_max_seconds=""'),
            "the cap is sized against the cell the warm-up keeps the attach in front of",
        )
        self.assertIn(
            "the dotnet-trace collector exited before it was stopped; the trace does not cover the measured phase",
            RPC_LIB.read_text(encoding="utf-8"),
        )

        # Pinned collector: the rig pins expb and json-bench for the same reason.
        start_node = START_NODE.read_text(encoding="utf-8")
        self.assertRegex(start_node, r'DOTNET_TRACE_VERSION="\$\{DOTNET_TRACE_VERSION:-[0-9]+\.[0-9]+\.[0-9]+\}"')
        self.assertEqual(start_node.count('dotnet tool install --version "$DOTNET_TRACE_VERSION"'), 2)

    def test_expb_profile_archive_precedes_deferred_perf_failure(self) -> None:
        expb_workflow = EXPB_WORKFLOW.read_text(encoding="utf-8")
        archive = 'zip -9r "${archive}" "${profiling_dirs[@]}" -x \'*/perf.data\''
        deferred_failure = 'if [[ "${perf_validation_failed}" == "true" ]]; then'

        for job_name in ("benchmark", "benchmark-multi"):
            collector = workflow_named_step_body(expb_workflow, job_name, "Collect and upload profiling artifacts")
            self.assertIn("perf_validation_failed=false", collector)
            self.assertIn("perf_validation_failed=true", collector)
            self.assertLess(
                collector.index(archive),
                collector.index(deferred_failure),
                f"{job_name} must archive dotTrace/EventPipe data before failing invalid perf output",
            )
            self.assertIn("exit 1", collector[collector.index(deferred_failure) :])


if __name__ == "__main__":
    unittest.main()
