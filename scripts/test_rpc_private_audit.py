# SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
# SPDX-License-Identifier: LGPL-3.0-only
"""Offline regression tests with synthetic faults and preserved public counters; no workloads."""
import contextlib
import hashlib
import io
import json
import os
from pathlib import Path
import subprocess
import shutil
import shlex
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parent
SCRIPTS = ROOT / "rpc-bench"
sys.path.insert(0, str(SCRIPTS))
import private_audit as module


def bash_executable():
    if os.name == "nt":
        git = shutil.which("git")
        if git:
            candidate = Path(git).resolve().parent.parent / "usr/bin/bash.exe"
            if candidate.is_file():
                return str(candidate)
    found = shutil.which("bash")
    if found:
        return found
    raise unittest.SkipTest("Bash is required for shell integration tests")


def summary(count=6000, failed=0, dropped=0):
    return {"metrics": {"http_reqs": {"values": {"count": count}},
                        "http_req_failed": {"values": {"rate": failed}},
                        "checks": {"values": {"passes": 2 * count, "fails": 0}},
                        "dropped_iterations": {"values": {"count": dropped}}}}


class AuditTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.base = Path(self.temporary.name).resolve()
        (self.base / "scratch").mkdir()
        self.storage = self.base / "scratch/rpc-private-123-1"
        self.storage.mkdir(mode=0o700)
        for name in ("out", "state", "scratch"):
            (self.storage / name).mkdir(mode=0o700)
        self.env = dict(RUNNER_TEMP=str(self.base), GITHUB_RUN_ID="123", GITHUB_RUN_ATTEMPT="1",
                        OUT_DIR=str(self.storage / "out"), STATE_ROOT=str(self.storage / "state"),
                        RPC_PRIVATE_STORAGE_ROOT=str(self.storage),
                        SCRATCH_ROOT=str(self.storage / "scratch"), RPC_PRIVATE_SHARED_SCRATCH_ROOT=str(self.base / "scratch"), LABEL="base", RPC_PRIVATE_AUDIT_PHASE="warm",
                        RPC_PRIVATE_SWEEP_OUTCOME="success", RPC_PRIVATE_TEARDOWN_OUTCOME="success", RPC_PRIVATE_CPU_RESTORE_OUTCOME="success",
                        GITHUB_OUTPUT=str(self.base / "github-output"))
        self.contexts = [
            patch.dict(os.environ, self.env),
            patch.object(module.archive_tool, "private_directory"),
            patch.object(module.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, b"DER")),
            patch.object(module.archive_tool, "CERTIFICATE_SHA256", hashlib.sha256(b"DER").hexdigest()),
        ]
        if os.name == "nt":
            original = module.archive_tool.identity
            self.contexts.append(patch.object(module.archive_tool, "identity", side_effect=lambda stat: (*original(stat)[:-1], 0)))
        for item in self.contexts:
            item.start()
            self.addCleanup(item.stop)
        module.write(self.storage / "owner.json", dict(storage=module.directory_identity(self.storage), shared=module.directory_identity(self.base / "scratch"), scratch=module.directory_identity(self.storage / "scratch")))
        module.prepare(4, 6000, 20000)
        self.root, self.public, _ = module.context()
        self.scratch = self.storage / "scratch"
        (self.scratch / "jsonbench/io/out").mkdir(parents=True)
        self.cell_dir = self.scratch / "warmup-cell/corpus/base"
        self.cell_dir.mkdir(parents=True)
        os.environ["OUT_DIR"] = str(self.cell_dir)

    def run_cell(self, *, code=0, text="2026-10-03 00:00:00 | INFO | completed\n",
                 count=6000, failed=0, dropped=0, stale=False, resources=True, sanitized_count=None):
        if stale:
            (self.scratch / "jsonbench/io/out/summary.json").write_text(json.dumps(summary(count)))
        def child(command, **kwargs):
            self.assertEqual(command, [str(SCRIPTS / "run-jsonbench.sh")])
            self.assertNotIn("shell", kwargs)
            self.assertEqual(kwargs["umask"], 0o022)
            kwargs["stdout"].write(text.encode())
            module.write(self.root / "outputs" / os.environ["LABEL"] / os.environ["RPC_PRIVATE_AUDIT_PHASE"] / "runtime-pin-complete.json", {"complete": True})
            data = json.dumps(summary(count, failed, dropped))
            paths = [self.scratch / "jsonbench/jsonbench-tool.log",
                     self.scratch / "jsonbench/io/out/summary.json", self.cell_dir / "summary.json"]
            paths[0].write_text(text, encoding="utf-8")
            for target in paths[1:]:
                if stale and target == paths[1]:
                    continue
                target.write_text(json.dumps(summary(sanitized_count, failed, dropped))
                                  if target == paths[2] and sanitized_count is not None else data)
            if resources:
                (self.cell_dir / "resources.json").write_text(json.dumps(dict(
                    requests=count, wall_seconds=200, samples=801, cpu_seconds=20,
                    cpu_throttled_usec=0, io_read_bytes=100, io_write_bytes=200)))
            return subprocess.CompletedProcess(command, code)
        with patch.object(module.subprocess, "run", side_effect=child):
            return module.cell()

    def test_cell_copies_raw_before_reuse_and_preserves_originals(self):
        self.assertTrue(self.run_cell())
        private = self.root / "outputs/base/warm"
        self.assertEqual(json.loads((private / "audit.json").read_text(encoding="utf-8"))["files"]["summary.raw.json"]["bytes"],
                         (private / "summary.raw.json").stat().st_size)
        (self.scratch / "jsonbench/jsonbench-tool.log").write_text("NEXT CELL")
        self.assertIn("completed", (private / "tool.log").read_text(encoding="utf-8"))
        self.assertTrue((self.cell_dir / "summary.json").exists())
        self.assertFalse(self.public.exists())

    def test_failure_cases_retain_raw_and_do_not_pass(self):
        cases = [dict(code=1), dict(text="DEBUG System.Exception PRIVATE_PAYLOAD\n"),
                 dict(text="INFO Incorrect JSON RPC parameters: Exception PRIVATE_PAYLOAD\n"),
                 dict(text="2026-10-03 | ERROR | PRIVATE_PAYLOAD\n"),
                 dict(text='time="2026-10-03T00:00:00Z" level=error msg="PRIVATE"\n'),
                 dict(text='ERRO[0001] PRIVATE\n'), dict(count=5999), dict(count=6002),
                 dict(failed=0.01), dict(dropped=1), dict(stale=True)]
        for i, case in enumerate(cases):
            with self.subTest(case=i):
                os.environ["LABEL"] = "arm" + str(i)
                self.assertFalse(self.run_cell(**case))
                directory = self.root / ("outputs/arm" + str(i) + "/warm")
                self.assertTrue((directory / "wrapper.log").exists())
                self.assertFalse(json.loads((directory / "audit.json").read_text(encoding="utf-8"))["complete"])
        self.assertFalse(self.public.exists())

    def test_missing_main_counter_file_is_failure(self):
        os.environ.update(RPC_PRIVATE_AUDIT_PHASE="main", OUT_DIR=str(self.storage / "out"))
        self.cell_dir = self.storage / "out"
        self.assertFalse(self.run_cell(count=20000, resources=False))
        self.assertTrue((self.root / "outputs/base/main/tool.log").exists())

    def test_duplicate_cell_and_root_refuse_overwrite(self):
        self.assertTrue(self.run_cell())
        with self.assertRaises(FileExistsError):
            self.run_cell()
        with self.assertRaises(FileExistsError):
            module.prepare(4, 6000, 20000)
        self.assertTrue((self.root / "outputs/base/warm/audit.json").exists())

    def test_log_classifier_is_unconditional_and_requires_shutdown(self):
        path = self.base / "fixture.log"
        for text, expected in [
            ("2026-10-03 | DEBUG | handled error message\nNethermind is shut down", True),
            ("2026-10-03 | ERROR | failure\nNethermind is shut down", False),
            ("DEBUG Incorrect JSON RPC parameters Exception\nNethermind is shut down", False),
            ("DEBUG Out of memory\nNethermind is shut down", False),
            ("DEBUG Fatal\nNethermind is shut down", False),
            ("DEBUG Invalid Block\nNethermind is shut down", False),
            ("INFO successful", False), ("", False),
        ]:
            with self.subTest(text=text):
                path.write_text(text)
                self.assertEqual(module.log_ok(path, shutdown=True), expected)

    def test_summary_rejects_missing_nonfinite_checks_and_handles_raw_format(self):
        path = self.base / "summary.json"
        for value, expected in [(summary(), True),
                                ({"metrics": {"http_reqs": {"count": 6000}, "http_req_failed": {"value": 0},
                                 "checks": {"passes": 12000, "fails": 0}}}, True), (summary(6001), True)]:
            path.write_text(json.dumps(value))
            self.assertEqual(module.valid_summary(path, 6000), expected)
        for field in ("checks", "http_req_failed"):
            value = summary()
            del value["metrics"][field]
            path.write_text(json.dumps(value))
            with self.assertRaises(KeyError):
                module.valid_summary(path, 6000)
        value = summary()
        value["metrics"]["http_req_failed"]["values"]["rate"] = float("nan")
        path.write_text(json.dumps(value))
        with self.assertRaises(ValueError):
            module.valid_summary(path, 6000)

    def test_changed_source_with_coarse_or_older_mtime_is_not_stale(self):
        path = self.scratch / "resource.json"
        path.write_text("previous")
        os.utime(path, ns=(1000000000, 1000000000))
        previous = module.source_identity(path, self.scratch)
        path.write_text("new observed result")
        os.utime(path, ns=(1000000000, 1000000000))
        self.assertIsNotNone(module.copy_file(path, self.root / "copied", self.scratch, previous=previous))

    def test_main_normalizes_to_actual_count_and_rejects_counter_disagreement(self):
        os.environ.update(RPC_PRIVATE_AUDIT_PHASE="main", OUT_DIR=str(self.storage / "out"))
        self.cell_dir = self.storage / "out"
        self.assertTrue(self.run_cell(count=20001))
        resource = self.root / "outputs/base/main/resources.json"
        self.assertTrue(module.valid_resources(resource, 20001))
        self.assertFalse(module.valid_resources(resource, 20000))
        os.environ["LABEL"] = "disagreement"
        self.assertFalse(self.run_cell(count=20001, sanitized_count=20000))

    def test_copy_refuses_foreign_and_hardlinked_sources(self):
        source = self.base / "source"
        source.write_text("PRIVATE")
        with self.assertRaises(ValueError):
            module.copy_file(source, self.root / "foreign", self.scratch)
        alias = self.base / "alias"
        os.link(source, alias)
        with self.assertRaises(ValueError):
            module.copy_file(source, self.root / "linked", self.base)

    def test_node_preserves_all_evidence_and_rejects_missing_or_bad_logs(self):
        for i, log in enumerate(("INFO ready\nNethermind is shut down", "DEBUG Exception PRIVATE\nNethermind is shut down", "")):
            source = self.storage / "state" / str(i)
            source.mkdir()
            for name, value in {"node.log": log, "node.env": "IMAGE=private", "db-baseline.txt": "same",
                                "db-final.txt": "same"}.items():
                (source / name).write_text(value)
            self.assertEqual(module.node("node" + str(i), source), i == 0)
            self.assertTrue((source / "node.log").exists())

    def fill_arms(self, count=4, fail=False):
        for i in range(count):
            arm = module.arm_directory(self.root, "final" + str(i))
            for phase in ("warm", "main", "node"):
                directory = arm / phase
                directory.mkdir()
                module.write(directory / "audit.json", {"complete": not (fail and i == 0)})
                (directory / "raw.log").write_text("PRIVATE_PAYLOAD_MUST_NEVER_APPEAR_PUBLIC")

    def fake_archive(self, source, output, certificate):
        self.assertEqual(source, self.root)
        self.assertEqual(certificate, module.COMPONENTS / "recipient.crt")
        output.mkdir(parents=True)
        (output / "raw.tar.gz.cms").write_bytes(b"CIPHERTEXT")
        (output / "archive.json").write_text("{}")

    def test_finalize_encrypts_before_upload_ready_and_never_exports_raw(self):
        self.fill_arms()
        with patch.object(module.archive_tool, "archive", side_effect=self.fake_archive), contextlib.redirect_stdout(io.StringIO()):
            self.assertTrue(module.finalize())
        self.assertEqual((self.base / "github-output").read_text(encoding="utf-8"), "archive_ready=true\n")
        public_bytes = b"".join(p.read_bytes() for p in self.public.rglob("*") if p.is_file())
        self.assertNotIn(b"PRIVATE_PAYLOAD", public_bytes)
        self.assertIn(b"UNKNOWN", public_bytes)
        self.assertTrue((self.root / "outputs/final0/warm/raw.log").exists())

    def test_partial_run_encrypted_but_quality_failed(self):
        self.fill_arms(count=1)
        with patch.object(module.archive_tool, "archive", side_effect=self.fake_archive), contextlib.redirect_stdout(io.StringIO()):
            self.assertFalse(module.finalize())
        status = json.loads((self.public / "audit-status.json").read_text(encoding="utf-8"))
        self.assertEqual((status["quality"], status["observed_arms"]), ("FAIL", 1))
        self.assertTrue((self.base / "github-output").exists())

    def test_all_captures_do_not_override_failed_sweep(self):
        self.fill_arms()
        os.environ["RPC_PRIVATE_SWEEP_OUTCOME"] = "failure"
        with patch.object(module.archive_tool, "archive", side_effect=self.fake_archive), contextlib.redirect_stdout(io.StringIO()):
            self.assertFalse(module.finalize())
        status = json.loads((self.public / "audit-status.json").read_text(encoding="utf-8"))
        self.assertFalse(status["sweep_success"])
        self.assertEqual(status["quality"], "FAIL")

    def test_archive_failure_keeps_raw_and_emits_no_upload_ready_or_private_error(self):
        self.fill_arms()
        output = io.StringIO()
        with patch.object(module.archive_tool, "archive", side_effect=subprocess.CalledProcessError(1, ["PRIVATE_COMMAND"])), \
             patch("sys.argv", ["private_audit.py", "finalize"]), contextlib.redirect_stderr(output):
            self.assertEqual(module.main(), 1)
        self.assertEqual(output.getvalue(), "RPC_PRIVATE_AUDIT_OR_ARCHIVE_FAILED\n")
        self.assertFalse((self.base / "github-output").exists())
        self.assertTrue((self.root / "outputs/final0/warm/raw.log").exists())


class StagingTests(unittest.TestCase):
    def test_actual_abba_schedule_and_digest_labels_are_unique(self):
        source = SCRIPTS
        sweep = (source / "run-rpc-sweep.sh").read_text(encoding="utf-8")
        library = (source / "lib.sh").read_text(encoding="utf-8")
        label_function = next(line for line in library.splitlines() if line.startswith("arm_label()"))
        schedule = sweep.split("schedule=()", 1)[1].split('echo "Schedule', 1)[0]
        duplicates = sweep.split('  LABEL_SEEN["$label"]=', 1)[1].split("\n  #", 1)[0]
        images = ["nethermindeth/nethermind@sha256:" + "a" * 64, "nethermindeth/nethermind@sha256:" + "b" * 64]
        script = 'PATH=/usr/bin:/bin\n' + label_function + '\nROUNDS=2\ndeclare -A LABEL_SEEN=()\n'
        script += 'entries=(' + ' '.join("'" + value + "'" for value in images) + ')\nschedule=()' + schedule
        script += '\nfor img in "${schedule[@]}"; do\nlabel="$(arm_label nethermind "$img")"\n'
        script += '  LABEL_SEEN["$label"]=' + duplicates + '\nprintf "%s\\n" "$label"\ndone\n'
        result = subprocess.run([bash_executable(), "-c", script],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        a, b = ["nethermind_" + value.rsplit(":", 1)[1] for value in images]
        self.assertEqual(result.stdout.splitlines(), [a, b, b + "_r2", a + "_r2"])


    def test_actual_warm_calculation_selects_frozen_rate_duration_and_seed(self):
        source = (SCRIPTS / "run-rpc-sweep.sh").read_text(encoding="utf-8")
        function = source.split("warm_node() {", 1)[1].split("\n}\n", 1)[0]
        script = 'warm_node() {' + function + '\n}\n'
        script += 'run_cell() { printf "OBSERVED:%s:%s:%s:%s:%s\\n" "$2" "$3" "$6" "$JB_SEED" "$RPC_PRIVATE_AUDIT_PHASE"; }\n'
        script += 'json_number() { printf 6001; }; report_fail_rate() { :; }\n'
        script += 'RPC_PRIVATE_AUDIT=true WARMUP_SECONDS=60 CORPUS_WARMUP_RPS=100 RPS_LIST=100 CORPUS_TIMINGS_PASSES=40 CORPUS_TIMINGS_RPS=0 CORPUS_WARMUP_RPS_MAX=100 SCRATCH_ROOT=/unused WARMUP_SEED=1001 JB_BENCHMARK_CONFIG=config\n'
        script += 'warm_node corpus unique_label source nethermind\n'
        result = subprocess.run([bash_executable(), "-c", script],
                                capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("OBSERVED:100:60s:unique_label:1001:warm", result.stdout)

    def test_real_historical_summaries_and_resources(self):
        history = json.loads((SCRIPTS / "private_audit/historical-counters.json").read_text(encoding="utf-8"))
        counts, accepted, rejected = set(), 0, 0
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "summary.json"
            resources = path.with_name("resources.json")
            for cell in history["cells"]:
                path.write_text(json.dumps(cell["summary"]))
                resources.write_text(json.dumps(cell["resources"]))
                actual = cell["summary"]["metrics"]["http_reqs"]["values"]["count"]
                counts.add(actual)
                expected = cell["expected_accept"]
                self.assertEqual(module.valid_summary(path, 20000), expected)
                self.assertTrue(module.valid_resources(resources, actual))
                accepted += expected
                rejected += not expected
        self.assertEqual(counts, {20000, 20001})
        self.assertEqual((accepted, rejected), (7, 1))

    def test_reviewed_components_byte_identical(self):
        pins = {
            "archive_private.py": "e7ab70535e6ccd1da7da36cf8e5b14a8b4f31adbddec37e4345fb4b241a5aab3",
            "recipient.crt": "fcec5cbb2b9808f68ed837ac4d70bbeb788d5cc6a0319b424b67c69a3880e0dc",
            "log_quality.py": "43e8afd28ad039a496ae24ddd2294b05e42d2e8b7b34348c7987622b422603da",
        }
        for name, expected in pins.items():
            self.assertEqual(hashlib.sha256((SCRIPTS / "private_audit" / name).read_bytes()).hexdigest(), expected)

    def test_default_shell_path_and_opt_in_wrapper(self):
        bash = bash_executable()
        source = (SCRIPTS / "run-rpc-sweep.sh").read_text(encoding="utf-8")
        function = source.split("run_cell() {", 1)[1].split("\n}\n", 1)[0]
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            (directory / "run-jsonbench.sh").write_text("#!/bin/bash\nprintf NORMAL\n", encoding="utf-8")
            (directory / "python3").write_text("#!/bin/bash\nprintf PRIVATE\n", encoding="utf-8")
            for name in ("run-jsonbench.sh", "python3"):
                (directory / name).chmod(0o755)
            for enabled, expected in [("false", "NORMAL"), ("true", "PRIVATE")]:
                script = 'run_cell() {' + function + '\n}\n'
                script += 'here=' + shlex.quote(directory.as_posix()) + '\nPATH="/usr/bin:/bin"\n'
                if os.name == "nt":
                    script += 'here="$(cygpath -u "$here")"\n'
                script += 'PATH="$here:$PATH"\n'
                script += 'RPC_PRIVATE_AUDIT=' + enabled + '\nCORPUS_RESOURCE_SAMPLING=false\n'
                script += 'RPC=http://unused SCRATCH_ROOT="$here" JB_REF=pinned JB_SEED=1 CORPUS_METHOD=eth_call CORPUS_TRACER=callTracer CORPUS_TRACE_TYPES=trace\n'
                script += 'run_cell config 100 60s "$here/cell" nethermind label corpus\n'
                result = subprocess.run([bash, "-c", script], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stderr, "")
                self.assertEqual(result.stdout, expected)

    def test_default_warm_failure_behavior_preserved_opt_in_fails_closed(self):
        bash = bash_executable()
        source = (SCRIPTS / "run-rpc-sweep.sh").read_text(encoding="utf-8")
        function = source.split("warm_node() {", 1)[1].split("\n}\n", 1)[0]
        for enabled, expected in [("false", 0), ("true", 1)]:
            script = 'warm_node() {' + function + '\n}\nrun_cell() { return 1; }\n'
            script += 'RPC_PRIVATE_AUDIT=' + enabled + '\n'
            script += 'WARMUP_SECONDS=60 CORPUS_WARMUP_RPS=100 RPS_LIST=100 CORPUS_TIMINGS_PASSES=40 CORPUS_TIMINGS_RPS=0 CORPUS_WARMUP_RPS_MAX=100 SCRATCH_ROOT=/unused WARMUP_SEED=1001 JB_BENCHMARK_CONFIG=config\n'
            script += 'warm_node corpus label source nethermind\n'
            result = subprocess.run([bash, "-c", script], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)
            self.assertEqual(result.returncode, expected, result.stderr)


class RuntimeAndRetentionTests(unittest.TestCase):
    setUp = AuditTests.setUp

    def pin_fixture(self, phase="warm", label="base"):
        os.environ.update(LABEL=label, RPC_PRIVATE_AUDIT_PHASE=phase,
                          JB_REF="d" * 40, RPC_URL="http://localhost:8545", SNAPSHOT_BLOCK="25490000")
        corpus = self.base / "corpus.gz"
        if not corpus.exists():
            corpus.write_bytes(b"PRIVATE_CORPUS_CONTENT")
        os.environ["JB_ETH_CALL_CORPUS_FILE"] = str(corpus)
        node = self.storage / "state" / label
        node.mkdir(exist_ok=True)
        (node / "db-baseline.txt").write_text("snapshot fingerprint")
        directory = module.arm_directory(self.root, label) / phase
        directory.mkdir()
        return directory

    def fake_command(self, command, **kwargs):
        if command[0] == "git":
            return "d" * 40 if "rev-parse" in command else ""
        if command[:3] == ["docker", "image", "inspect"]:
            return "sha256:" + "a" * 64
        if command[:3] == ["docker", "inspect", "--type"]:
            return json.dumps(dict(Id="b" * 64, Name="/rpcbench-sweep-" + os.environ["LABEL"] + "-123", Image="sha256:" + "c" * 64,
                                   Config={"Image": "nethermind/image@sha256:" + "e" * 64}, State={"Running": True}))
        self.fail("Unexpected command")

    def run_pin(self, **kwargs):
        with patch.object(module, "command_output", side_effect=self.fake_command), \
             patch.object(module, "owned_cid", return_value="b" * 64), \
             patch.object(module, "tool_version", return_value="k6 v2.1.0 (test fixture)"), \
             patch.object(module, "snapshot_head", return_value={"number": 25490000, "hash": "0x" + "f" * 64}):
            return module.runtime_pin("sha256:" + "a" * 64, "d" * 40, kwargs.get("built_current", "true"))

    def test_runtime_records_actual_identifiers_before_load_and_matches_next_phase(self):
        warm = self.pin_fixture()
        self.assertEqual(self.run_pin(), "sha256:" + "a" * 64)
        record = json.loads((warm / "runtime-pin.json").read_text(encoding="utf-8"))
        self.assertEqual(record["tool"]["json_bench_commit"], "d" * 40)
        self.assertEqual(record["corpus"]["sha256"], hashlib.sha256(b"PRIVATE_CORPUS_CONTENT").hexdigest())
        self.assertNotIn("PRIVATE_CORPUS_CONTENT", (warm / "runtime-pin.json").read_text(encoding="utf-8"))
        self.pin_fixture("main")
        self.assertEqual(self.run_pin(), "sha256:" + "a" * 64)

    def test_corpus_change_preserves_observation_but_refuses_completion(self):
        self.pin_fixture()
        self.run_pin()
        directory = self.pin_fixture("main")
        Path(os.environ["JB_ETH_CALL_CORPUS_FILE"]).write_bytes(b"CHANGED")
        with self.assertRaisesRegex(ValueError, "PROVENANCE_MISMATCH"):
            self.run_pin()
        self.assertTrue((directory / "runtime-pin.json").exists())
        self.assertFalse((directory / "runtime-pin-complete.json").exists())

    def test_changed_tool_snapshot_or_node_identity_refuses_next_phase(self):
        self.pin_fixture()
        self.run_pin()
        for index, field in enumerate(("tool", "snapshot", "node")):
            with self.subTest(field=field):
                directory = self.pin_fixture("main", "arm" + str(index))
                def command(args, **kwargs):
                    value = self.fake_command(args)
                    if field == "tool" and args[:3] == ["docker", "image", "inspect"]:
                        return "sha256:" + "1" * 64
                    if field == "node" and args[:3] == ["docker", "inspect", "--type"]:
                        node = json.loads(value)
                        node["Image"] = "sha256:" + "2" * 64
                        return json.dumps(node)
                    return value
                head = {"number": 25490000, "hash": "0x" + ("3" if field == "snapshot" else "f") * 64}
                with patch.object(module, "command_output", side_effect=command), \
                     patch.object(module, "owned_cid", return_value="b" * 64), \
                     patch.object(module, "tool_version", return_value="k6 v2.1.0 (test fixture)"), \
                     patch.object(module, "snapshot_head", return_value=head):
                    with self.assertRaises(ValueError):
                        module.runtime_pin("sha256:" + "a" * 64, "d" * 40, "true")
                self.assertFalse((directory / "runtime-pin-complete.json").exists())
                # Each mismatch ends a real run; isolate cases from that deliberate incomplete marker.
                (directory / "runtime-pin.json").unlink()

    def test_cached_preparation_cannot_claim_current_source_build(self):
        self.pin_fixture()
        with self.assertRaisesRegex(ValueError, "FRESH_TOOL_BUILD"):
            self.run_pin(built_current="false")

    def test_source_requires_exact_commit_and_clean_tracked_files(self):
        for results in (("c" * 40,), ("d" * 40, subprocess.CalledProcessError(1, ["PRIVATE"]))):
            with self.subTest(results=len(results)), patch.object(module, "command_output", side_effect=results):
                with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    module.verified_source(self.scratch / "jsonbench/src", "d" * 40)
        with patch.object(module, "command_output", side_effect=["d" * 40, ""]) as command:
            self.assertEqual(module.verified_source(self.scratch, "d" * 40), "d" * 40)
            self.assertEqual(command.call_args_list[1].args[0][-4:], ["diff", "--quiet", "HEAD", "--"])

    def test_version_probe_uses_exact_image_and_removes_only_created_container(self):
        directory = self.pin_fixture()
        for version, code, accepted in [("k6 v2.1.0 (fixture)", "0", True), ("k6 v1.5.0", "0", False), ("k6 v2.1.0", "1", False)]:
            with self.subTest(version=version, code=code), patch.object(module, "command_output", side_effect=["c" * 64, version, code]) as command, \
                 patch.object(module, "register_container", return_value=directory / "version.cid"), \
                 patch.object(module, "owned_cid", return_value="c" * 64), patch.object(module, "stop_owned_tool") as cleanup:
                if accepted:
                    self.assertEqual(module.tool_version("sha256:" + "a" * 64, directory), version)
                else:
                    with self.assertRaises(ValueError):
                        module.tool_version("sha256:" + "a" * 64, directory)
                self.assertIn("--read-only", command.call_args_list[0].args[0])
                self.assertIn("none", command.call_args_list[0].args[0])
                cleanup.assert_called_once_with(directory, "version")

    def test_version_probe_timeout_still_cleans_owned_container(self):
        directory = self.pin_fixture()
        with patch.object(module, "command_output", side_effect=["c" * 64, subprocess.TimeoutExpired("probe", 20)]) as command, \
             patch.object(module, "register_container", return_value=directory / "version.cid"), \
             patch.object(module, "owned_cid", return_value="c" * 64), patch.object(module, "stop_owned_tool") as cleanup:
            with self.assertRaises(subprocess.TimeoutExpired):
                module.tool_version("sha256:" + "a" * 64, directory)
            cleanup.assert_called_once_with(directory, "version")

    def test_snapshot_head_requires_local_endpoint_and_exact_height_hash(self):
        with self.assertRaises(ValueError):
            module.snapshot_head("https://example.invalid", 25490000)
        from unittest.mock import MagicMock
        for number, expected in [(25490000, True), (1, False)]:
            response = MagicMock()
            response.__enter__.return_value.read.return_value = json.dumps({"result": {"number": hex(number), "hash": "0x" + "f" * 64}}).encode()
            with patch("urllib.request.urlopen", return_value=response):
                if expected:
                    self.assertEqual(module.snapshot_head("http://localhost:8545", 25490000)["number"], number)
                else:
                    with self.assertRaises(ValueError):
                        module.snapshot_head("http://localhost:8545", 25490000)

    def test_oversized_copy_failure_keeps_full_source_under_private_run_root(self):
        source = self.scratch / "jsonbench/jsonbench-tool.log"
        source.write_bytes(b"PRIVATE" * 10)
        with patch.object(module, "MAX_FILE_BYTES", 8):
            with self.assertRaises(ValueError):
                module.copy_file(source, self.root / "copy", self.scratch)
        self.assertEqual(source.read_bytes(), b"PRIVATE" * 10)
        self.assertFalse((self.root / "copy").exists())
        self.assertTrue(source.is_relative_to(self.storage))
        self.assertFalse(module.cleanup_sources("failure", "success", "success", "success"))
        self.assertTrue(source.exists())

    def test_cleanup_requires_both_successes_and_exact_stable_owner_identity(self):
        for audit, upload in [("failure", "success"), ("success", "failure"), ("success", ""), ("", "success")]:
            self.assertFalse(module.cleanup_sources(audit, upload, "success", "success"))
            self.assertTrue(self.storage.exists())
        original = module.directory_identity
        def changed(path):
            identity = original(path)
            if path == self.storage:
                identity[1] += 1
            return identity
        with patch.object(module, "directory_identity", side_effect=changed):
            with self.assertRaises(ValueError):
                module.cleanup_sources("success", "success", "success", "success")
        sentinel = self.base / "scratch/shared-sentinel"
        sentinel.write_text("preserve")
        with patch.object(module, "mount_records", return_value={}):
            self.assertTrue(module.cleanup_sources("success", "success", "success", "success"))
        self.assertFalse(self.storage.exists())
        self.assertEqual(sentinel.read_text(encoding="utf-8"), "preserve")

    def test_failure_status_does_not_claim_every_raw_source_was_copied(self):
        AuditTests.fill_arms(self, count=1)
        with patch.object(module.archive_tool, "archive", side_effect=lambda *args: args[1].mkdir(parents=True)), contextlib.redirect_stdout(io.StringIO()):
            self.assertFalse(module.finalize())
        status = json.loads((self.public / "audit-status.json").read_text(encoding="utf-8"))
        self.assertNotIn("raw_retained_privately", status)
        self.assertFalse(status["all_required_evidence_copied"])
        self.assertTrue(status["copied_evidence_retained_privately"])
        self.assertEqual(os.environ["RUNNER_TEMP"], str(self.base))

    def test_default_cleanup_loop_cannot_delete_private_prior_run_tree(self):
        source = (SCRIPTS / "cleanup.sh").read_text(encoding="utf-8")
        loop = "for sub in " + source.split("for sub in ", 1)[1].split('\nlog "Defensive cleanup done."', 1)[0]
        sentinel = self.scratch / "oversized-tool.log"
        sentinel.write_bytes(b"PRIVATE")
        shared = self.base / "scratch"
        for name in ("jsonbench", "warmup-cell"):
            (shared / name).mkdir()
            (shared / name / "next-job").write_text("temporary")
        script = 'PATH=/usr/bin:/bin\nas_root() { "$@"; }; assert_no_mounts_under() { :; }; log() { :; }\n'
        script += "SCRATCH_ROOT=" + shlex.quote(shared.as_posix()) + "\n"
        if os.name == "nt":
            script += 'SCRATCH_ROOT="$(cygpath -u "$SCRATCH_ROOT")"\n'
        # setUp mocks subprocess.run; the cleanup regression needs an actual local shell.
        process = subprocess.Popen([bash_executable(), "-c", script + loop], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        output, error = process.communicate(timeout=30)
        self.assertEqual(process.returncode, 0, error.decode())
        self.assertEqual(sentinel.read_bytes(), b"PRIVATE")
        self.assertFalse((shared / "jsonbench").exists(), (output, error))
        self.assertFalse((shared / "warmup-cell").exists())

    def test_storage_path_rejects_foreign_name_and_directory_replacement(self):
        with patch.dict(os.environ, RPC_PRIVATE_STORAGE_ROOT=str(self.base / "scratch")):
            with self.assertRaises(ValueError):
                module.locations()
        moved = self.storage.with_name("original-preserved")
        self.storage.rename(moved)
        self.storage.mkdir(mode=0o700)
        with self.assertRaises((ValueError, FileNotFoundError)):
            module.cleanup_sources("success", "success", "success", "success")
        self.assertTrue(moved.exists())
        self.assertTrue(self.storage.exists())


class PrivatePathWorkflowTests(unittest.TestCase):
    @unittest.skipIf(os.name == "nt", "Git Bash cannot enforce POSIX mkdir -m on this Windows filesystem")
    def test_actual_private_path_block_rejects_same_run_collision_and_separates_jobs(self):
        workflow = (ROOT.parent / ".github/workflows/run-rpc-benchmarks.yml").read_text(encoding="utf-8")
        block = workflow.split('          if [[ "${RPC_PRIVATE_AUDIT}" == "true" ]]; then\n', 1)[1].split("          else\n", 1)[0]
        block = "\n".join(line[12:] if line.startswith("            ") else line for line in block.splitlines())
        with tempfile.TemporaryDirectory() as temporary:
            shared = Path(temporary).resolve()
            database = shared / "database"
            database.mkdir()
            parent = shared / "scratch"
            parent.mkdir()
            for run, expected in ((123, 0), (123, 1), (124, 0)):
                script = "PATH=/usr/bin:/bin\nBENCH_SCRATCH_ROOT=" + shlex.quote(parent.as_posix()) + "\n"
                script += "PRIVATE_DB_SOURCE=" + shlex.quote(database.as_posix()) + "\n"
                script += "GITHUB_ENV=" + shlex.quote((shared / "env").as_posix()) + "\n"
                if os.name == "nt":
                    script += 'BENCH_SCRATCH_ROOT="$(cygpath -u "$BENCH_SCRATCH_ROOT")"\nGITHUB_ENV="$(cygpath -u "$GITHUB_ENV")"\n'
                script += "GITHUB_RUN_ID=" + str(run) + "\nGITHUB_RUN_ATTEMPT=1\n"
                result = subprocess.run([bash_executable(), "-c", script + block], capture_output=True, timeout=30)
                self.assertEqual(result.returncode, expected, result.stderr)
            self.assertTrue((parent / "rpc-private-123-1").is_dir())
            self.assertTrue((parent / "rpc-private-124-1").is_dir())

    def test_upload_confirmation_gates_private_cleanup_and_default_cleanup_is_unchanged(self):
        workflow = (ROOT.parent / ".github/workflows/run-rpc-benchmarks.yml").read_text(encoding="utf-8")
        cleanup = workflow.split("      - name: Remove uploaded private RPC sources\n", 1)[1].split("      - name:", 1)[0]
        self.assertIn("steps.private-audit-upload.outcome == 'success'", cleanup)
        self.assertIn("steps.private-audit-finalize.outcome == 'success'", cleanup)
        normal = workflow.split("      - name: Remove run output\n", 1)[1].split("      - name:", 1)[0]
        self.assertIn("inputs.private_audit != true", normal)
        self.assertNotIn("rm -rf", cleanup)

    def test_preload_pin_precedes_sampler_and_load_and_returns_image_used(self):
        source = (SCRIPTS / "run-jsonbench.sh").read_text(encoding="utf-8")
        self.assertLess(source.index('private_audit.py" verify-source'), source.index('image_tag="$(docker build'))
        pin = source.index('private_audit.py" runtime-pin')
        self.assertLess(pin, source.index("# Resource sampling brackets"))
        self.assertLess(pin, source.index('docker run "${docker_common[@]}" "$image_tag" benchmark'))
        self.assertIn('-v "$work/src:/jb:ro" -v "$work/io:/io"', source)
        self.assertIn('chmod -R a+rwX "$work/io"', source)


class OwnershipTests(unittest.TestCase):
    setUp = AuditTests.setUp

    def node_fixture(self):
        directory = self.storage / "state/sweep/node"
        directory.mkdir(parents=True)
        scratch = self.storage / "scratch/run"
        image = "image@sha256:" + "b" * 64
        with patch.object(module, "command_output", side_effect=["", "sha256:" + "c" * 64]), patch.object(module, "mount_records", return_value={}):
            module.register_container(directory, "node", "rpcbench-owned-123", image, scratch)
        return directory, scratch, image

    def test_preflight_refuses_foreign_resources_and_lookup_failure_without_mutation(self):
        (self.storage / "owner.json").unlink()
        sentinel = self.base / "scratch/foreign-evidence"
        sentinel.write_bytes(b"FOREIGN")
        for names, mounts, error in [("rpcbench-foreign", {}, None), ("", {"foreign": "mount"}, None), ("", {}, subprocess.CalledProcessError(1, ["docker"]))]:
            with self.subTest(names=bool(names), mounts=bool(mounts), error=bool(error)), \
                 patch.object(module, "command_output", return_value=names, side_effect=error) as command, \
                 patch.object(module, "mount_records", return_value=mounts):
                with self.assertRaises((ValueError, subprocess.CalledProcessError)):
                    module.preflight()
                self.assertFalse((self.storage / "owner.json").exists())
                self.assertEqual(sentinel.read_bytes(), b"FOREIGN")
                self.assertEqual(command.call_args_list[0].args[0][:3], ["docker", "ps", "-a"])

    def test_preflight_never_restores_foreign_saved_cpu_state(self):
        (self.storage / "owner.json").unlink()
        saved = self.base / "scratch/cpu-state/cpu-sysfs.orig"
        saved.parent.mkdir()
        saved.write_text("FOREIGN_CPU_STATE")
        with patch.object(module, "command_output", return_value="") as command, patch.object(module, "mount_records", return_value={}):
            with self.assertRaisesRegex(ValueError, "CPU_STATE"):
                module.preflight()
            self.assertEqual(command.call_count, 2)
        self.assertEqual(saved.read_text(), "FOREIGN_CPU_STATE")
        self.assertFalse((self.storage / "owner.json").exists())

    def test_node_registration_refuses_existing_name_scratch_and_foreign_directory(self):
        directory = self.storage / "state/node"
        directory.mkdir()
        with patch.object(module, "command_output", return_value="foreign-id") as command:
            with self.assertRaises(ValueError):
                module.register_container(directory, "node", "rpcbench-name", "image", self.scratch / "run")
            self.assertEqual(command.call_count, 1)
        scratch = self.scratch / "run"
        scratch.mkdir()
        with patch.object(module, "command_output", side_effect=["", "sha256:" + "c" * 64]), patch.object(module, "mount_records", return_value={}):
            with self.assertRaises(ValueError):
                module.register_container(directory, "node", "rpcbench-name", "image", scratch)
        with patch.object(module, "command_output") as command:
            with self.assertRaises(ValueError):
                module.register_container(self.base, "node", "rpcbench-name", "image", scratch)
            command.assert_not_called()
        self.assertFalse((directory / "node-owner.json").exists())

    def test_cid_or_label_or_image_replacement_is_never_signalled(self):
        directory, _, image = self.node_fixture()
        cid = "a" * 64
        (directory / "node.cid").write_text(cid)
        valid = dict(Id=cid, Name="/rpcbench-owned-123", Image="sha256:" + "c" * 64,
                     Config={"Image": image, "Labels": {module.OWNER_LABEL: "123-1"}})
        replacements = [{"Id": "d" * 64}, {"Name": "/foreign"}, {"Image": "sha256:" + "d" * 64},
                        {"Config": {"Image": image, "Labels": {module.OWNER_LABEL: "foreign"}}}]
        for change in replacements:
            with self.subTest(field=next(iter(change))), patch.object(module, "command_output", side_effect=[cid, json.dumps(valid | change)]) as command:
                with self.assertRaises(ValueError):
                    module.stop_owned_tool(directory, "node")
                self.assertEqual(command.call_count, 2)
                self.assertFalse(any("stop" in call.args[0] or "rm" in call.args[0] for call in command.call_args_list))
        with patch.object(module, "command_output", side_effect=[cid, json.dumps(valid), "", ""]) as command:
            module.stop_owned_tool(directory, "node")
            self.assertEqual(command.call_args_list[-2].args[0], ["docker", "stop", "--time", "30", cid])
            self.assertEqual(command.call_args_list[-1].args[0], ["docker", "rm", cid])

    def test_missing_cid_and_failed_absence_lookup_remain_unknown(self):
        directory, _, _ = self.node_fixture()
        with patch.object(module, "command_output") as command:
            with self.assertRaisesRegex(ValueError, "CID_MISSING"):
                module.owned_cid(directory, "node", allow_absent=True)
            command.assert_not_called()
        (directory / "node.cid").write_text("a" * 64)
        with patch.object(module, "command_output", side_effect=subprocess.CalledProcessError(1, ["docker"])) as command:
            with self.assertRaises(subprocess.CalledProcessError):
                module.owned_cid(directory, "node", allow_absent=True)
            self.assertEqual(command.call_count, 1)
        with patch.object(module, "command_output", return_value=""):
            self.assertIsNone(module.owned_cid(directory, "node", allow_absent=True))

    def test_failed_start_without_cid_or_mount_checkpoint_is_retained_without_cleanup(self):
        directory, scratch, _ = self.node_fixture()
        scratch.mkdir()
        (scratch / "evidence").write_text("PARTIAL_START")
        with patch.object(module, "mount_records", return_value={}), patch.object(module, "command_output") as command, \
             patch.object(module.subprocess, "run") as child:
            self.assertFalse(module.teardown())
            command.assert_not_called()
            child.assert_not_called()
        self.assertEqual((scratch / "evidence").read_text(), "PARTIAL_START")
        self.assertTrue((directory / "node-owner.json").exists())

    def test_unmount_requires_stable_checkpoint_and_uses_no_lazy_fallback(self):
        directory, scratch, _ = self.node_fixture()
        scratch.mkdir()
        target = scratch / "merged"
        target.mkdir()
        checkpoint = {str(target): "101 10 0:11 / " + str(target) + " rw - overlay overlay rw"}
        with patch.object(module, "mount_records", return_value=checkpoint):
            module.node_mounted(directory, target)
        for current in ({str(target): "replaced-mount"}, checkpoint | {str(scratch / "foreign"): "foreign-mount"}):
            with patch.object(module, "owned_cid", return_value=None), patch.object(module, "name_available"), \
                 patch.object(module, "mount_records", return_value=current), patch.object(module, "command_output") as command:
                with self.assertRaises(ValueError):
                    module.node_unmount(directory)
                command.assert_not_called()
        with patch.object(module, "owned_cid", return_value=None), patch.object(module, "name_available"), \
             patch.object(module, "mount_records", side_effect=[checkpoint, {}]), patch.object(module, "command_output") as command, \
             patch.object(module.os, "geteuid", return_value=0, create=True):
            module.node_unmount(directory)
            command.assert_called_once_with(["umount", "--", str(target)])
        (directory / "node-mount.json").unlink()
        with patch.object(module, "owned_cid", return_value=None), patch.object(module, "name_available"), patch.object(module, "command_output") as command:
            with self.assertRaises(FileNotFoundError):
                module.node_unmount(directory)
            command.assert_not_called()

    def test_cleanup_refuses_live_mounts_nonempty_cpu_state_or_failed_teardown(self):
        for teardown, cpu in [("failure", "success"), ("success", "failure"), ("", "success")]:
            self.assertFalse(module.cleanup_sources("success", "success", teardown, cpu))
        with patch.object(module, "mount_records", return_value={"private": "live"}):
            with self.assertRaises(ValueError):
                module.cleanup_sources("success", "success", "success", "success")
        saved = self.storage / "scratch/cpu-state/cpu-sysfs.orig"
        saved.parent.mkdir()
        saved.write_text("NOT_RESTORED")
        with patch.object(module, "mount_records", return_value={}):
            with self.assertRaises(ValueError):
                module.cleanup_sources("success", "success", "success", "success")
        self.assertEqual(saved.read_text(), "NOT_RESTORED")

    def test_workflow_restores_after_partial_apply_and_detects_incomplete_restore(self):
        workflow = (ROOT.parent / ".github/workflows/run-rpc-benchmarks.yml").read_text(encoding="utf-8")
        step = workflow.split("      - name: Restore CPU frequency\n", 1)[1].split("      - name:", 1)[0]
        self.assertIn("steps.private-preflight.outcome == 'success'", step)
        self.assertNotIn("steps.cpu-apply.outcome", step)
        wrapper = step.split("        run: |\n", 1)[1]
        wrapper = "\n".join(line[10:] for line in wrapper.splitlines())
        with tempfile.TemporaryDirectory() as temporary:
            base = Path(temporary).resolve()
            script_path = base / "scripts/rpc-bench/cpu-stabilize.sh"
            script_path.parent.mkdir(parents=True)
            state = base / "state"
            state.mkdir()
            saved = state / "cpu-sysfs.orig"
            for retained, expected in [(True, 1), (False, 0)]:
                saved.write_text("PARTIALLY_CHANGED")
                script_path.write_text('#!/bin/bash\n' + ('exit 0\n' if retained else ': > "$STATE_DIR/cpu-sysfs.orig"\n'))
                script_path.chmod(0o755)
                script = 'PATH=/usr/bin:/bin\nRPC_PRIVATE_AUDIT=true\nSTATE_DIR=' + shlex.quote(state.as_posix()) + '\n'
                if os.name == "nt":
                    script += 'STATE_DIR="$(cygpath -u "$STATE_DIR")"\n'
                script += 'export STATE_DIR\npython3() { printf \"%s\\n\" \"$STATE_DIR\"; }\n' + wrapper
                process = subprocess.Popen([bash_executable(), "-c", script], cwd=base, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                _, error = process.communicate(timeout=30)
                self.assertEqual(process.returncode, expected, error.decode())








if __name__ == "__main__":
    unittest.main(verbosity=2)
